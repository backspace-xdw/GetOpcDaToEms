using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using OPCAutomation;

namespace OpcDaClient
{
    /// <summary>
    /// 轮询读取器接口
    /// </summary>
    public interface IPollingReader : IDisposable
    {
        /// <summary>是否正在运行</summary>
        bool IsRunning { get; }

        /// <summary>轮询间隔（毫秒），运行中可动态修改</summary>
        int IntervalMs { get; set; }

        /// <summary>读取配置</summary>
        ReadConfig Config { get; }

        /// <summary>已完成的读取次数</summary>
        int ReadCount { get; }

        /// <summary>数据到达事件</summary>
        event EventHandler<PollingDataEventArgs> DataReceived;

        /// <summary>读取错误事件</summary>
        event EventHandler<PollingErrorEventArgs> ErrorOccurred;

        /// <summary>启动轮询</summary>
        void Start();

        /// <summary>停止轮询</summary>
        void Stop();
    }

    /// <summary>
    /// 轮询数据事件参数
    /// </summary>
    public class PollingDataEventArgs : EventArgs
    {
        /// <summary>读取到的数据</summary>
        public Dictionary<string, OpcItemValue> Values { get; set; }

        /// <summary>本次读取耗时（毫秒）</summary>
        public long ReadDurationMs { get; set; }

        /// <summary>累计读取次数</summary>
        public int ReadCount { get; set; }

        /// <summary>读取时间</summary>
        public DateTime ReadTime { get; set; }
    }

    /// <summary>
    /// 轮询错误事件参数
    /// </summary>
    public class PollingErrorEventArgs : EventArgs
    {
        /// <summary>异常信息</summary>
        public Exception Exception { get; set; }

        /// <summary>连续错误次数</summary>
        public int ConsecutiveErrors { get; set; }
    }

    /// <summary>
    /// 后台轮询读取器
    /// 在独立线程中按配置的间隔循环读取 OPC 数据项
    /// 复用持久化 OPCGroup，避免频繁创建/销毁开销
    /// </summary>
    public class PollingReader : IPollingReader
    {
        private readonly OPCServer _opcServer;
        private readonly string[] _itemIds;
        private ReadConfig _config;
        private volatile int _intervalMs;

        // OPC 持久化资源（创建一次，反复使用）
        private OPCGroup _group;
        private string _groupName;
        private Array _serverHandleArray;

        // 线程控制
        private Thread _pollingThread;
        private volatile bool _running;
        private readonly ManualResetEvent _stopSignal;

        // 异步读取同步器
        private ManualResetEvent _asyncReadDone;
        private Dictionary<string, OpcItemValue> _asyncReadResult;
        private Exception _asyncReadError;

        // 统计
        private int _readCount;
        private int _consecutiveErrors;

        // SyncRead 是否支持 7 参数版本（首次调用时检测）
        private bool _syncReadChecked;
        private bool _syncReadHasQualityTimestamp;

        private bool _disposed;

        public bool IsRunning { get { return _running; } }
        public int IntervalMs { get { return _intervalMs; } set { _intervalMs = value; } }
        public ReadConfig Config { get { return _config; } }
        public int ReadCount { get { return _readCount; } }

        public event EventHandler<PollingDataEventArgs> DataReceived;
        public event EventHandler<PollingErrorEventArgs> ErrorOccurred;

        /// <summary>
        /// 创建轮询读取器（由 OpcDaClient.CreatePollingReader 调用）
        /// </summary>
        internal PollingReader(OPCServer opcServer, string[] itemIds, ReadConfig config, int intervalMs)
        {
            _opcServer = opcServer;
            _itemIds = (string[])itemIds.Clone();
            _config = config ?? ReadConfig.SyncCache;
            _intervalMs = intervalMs;
            _stopSignal = new ManualResetEvent(false);
        }

        /// <summary>
        /// 启动后台轮询
        /// </summary>
        public void Start()
        {
            if (_running)
                throw new InvalidOperationException("轮询已在运行中");

            if (_opcServer == null)
                throw new InvalidOperationException("OPC 服务器未连接");

            // 创建持久化 Group 并添加数据项
            InitGroup();

            // 启动轮询线程
            _running = true;
            _readCount = 0;
            _consecutiveErrors = 0;
            _stopSignal.Reset();

            _pollingThread = new Thread(PollingLoop);
            _pollingThread.IsBackground = true;
            _pollingThread.Name = "OPC_Polling";
            _pollingThread.Start();
        }

        /// <summary>
        /// 停止轮询
        /// </summary>
        public void Stop()
        {
            if (!_running) return;

            _running = false;
            _stopSignal.Set();

            // 等待线程退出（最多 5 秒）
            if (_pollingThread != null && _pollingThread.IsAlive)
            {
                _pollingThread.Join(5000);
            }
            _pollingThread = null;

            // 清理 OPC 资源
            CleanupGroup();
        }

        #region 初始化与清理

        private void InitGroup()
        {
            _groupName = "Polling_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _group = _opcServer.OPCGroups.Add(_groupName);
            _group.IsActive = true;
            _group.IsSubscribed = false;

            // 添加数据项并缓存 ServerHandle
            var opcItems = _group.OPCItems;
            var serverHandles = new int[_itemIds.Length];

            for (int i = 0; i < _itemIds.Length; i++)
            {
                var opcItem = opcItems.AddItem(_itemIds[i], i + 1);
                serverHandles[i] = opcItem.ServerHandle;
            }

            // 构建 1-based 数组并缓存（每次读取复用）
            _serverHandleArray = Array.CreateInstance(
                typeof(int), new int[] { _itemIds.Length }, new int[] { 1 });
            for (int i = 0; i < serverHandles.Length; i++)
            {
                _serverHandleArray.SetValue(serverHandles[i], i + 1);
            }

            // 异步模式需要注册回调
            if (_config.Mode == ReadMode.Async)
            {
                _asyncReadDone = new ManualResetEvent(false);
                _group.AsyncReadComplete += OnAsyncReadComplete;
            }
        }

        private void CleanupGroup()
        {
            try
            {
                if (_group != null)
                {
                    if (_config.Mode == ReadMode.Async)
                    {
                        _group.AsyncReadComplete -= OnAsyncReadComplete;
                    }
                    _opcServer.OPCGroups.Remove(_groupName);
                }
            }
            catch { }

            _group = null;
            _serverHandleArray = null;

            if (_asyncReadDone != null)
            {
                _asyncReadDone.Dispose();
                _asyncReadDone = null;
            }
        }

        #endregion

        #region 轮询循环

        private void PollingLoop()
        {
            while (_running)
            {
                try
                {
                    var sw = Stopwatch.StartNew();

                    Dictionary<string, OpcItemValue> results;
                    if (_config.Mode == ReadMode.Async)
                        results = DoAsyncRead();
                    else
                        results = DoSyncRead();

                    sw.Stop();
                    _readCount++;
                    _consecutiveErrors = 0;

                    DataReceived?.Invoke(this, new PollingDataEventArgs
                    {
                        Values = results,
                        ReadDurationMs = sw.ElapsedMilliseconds,
                        ReadCount = _readCount,
                        ReadTime = DateTime.Now
                    });
                }
                catch (Exception ex)
                {
                    _consecutiveErrors++;

                    ErrorOccurred?.Invoke(this, new PollingErrorEventArgs
                    {
                        Exception = ex,
                        ConsecutiveErrors = _consecutiveErrors
                    });

                    // 连续错误过多，自动停止（防止无限重试）
                    if (_consecutiveErrors >= 10)
                    {
                        _running = false;
                        break;
                    }
                }

                // 可中断的等待（Stop 时通过 _stopSignal 立即唤醒）
                if (_running)
                {
                    _stopSignal.WaitOne(_intervalMs);
                }
            }
        }

        #endregion

        #region 同步读取（复用持久 Group）

        private Dictionary<string, OpcItemValue> DoSyncRead()
        {
            var results = new Dictionary<string, OpcItemValue>();

            // 复制缓存的 handle 数组（ref 参数安全）
            Array handleArray = (Array)_serverHandleArray.Clone();

            Array values;
            Array errors;

            if (!_syncReadChecked)
            {
                // 首次读取：检测 SyncRead 是否支持 7 参数版本
                _syncReadChecked = true;
                try
                {
                    object qualities, timeStamps;
                    _group.SyncRead(
                        (short)_config.DataSource,
                        _itemIds.Length,
                        ref handleArray,
                        out values,
                        out errors,
                        out qualities,
                        out timeStamps);
                    _syncReadHasQualityTimestamp = true;
                    return ParseSyncResults(values, errors, qualities, timeStamps);
                }
                catch (System.Reflection.TargetParameterCountException)
                {
                    _syncReadHasQualityTimestamp = false;
                    // 回退到 5 参数版本
                    handleArray = (Array)_serverHandleArray.Clone();
                    _group.SyncRead(
                        (short)_config.DataSource,
                        _itemIds.Length,
                        ref handleArray,
                        out values,
                        out errors);
                    return ParseSyncResults(values, errors, null, null);
                }
            }

            if (_syncReadHasQualityTimestamp)
            {
                object qualities, timeStamps;
                _group.SyncRead(
                    (short)_config.DataSource,
                    _itemIds.Length,
                    ref handleArray,
                    out values,
                    out errors,
                    out qualities,
                    out timeStamps);
                return ParseSyncResults(values, errors, qualities, timeStamps);
            }
            else
            {
                _group.SyncRead(
                    (short)_config.DataSource,
                    _itemIds.Length,
                    ref handleArray,
                    out values,
                    out errors);
                return ParseSyncResults(values, errors, null, null);
            }
        }

        private Dictionary<string, OpcItemValue> ParseSyncResults(
            Array values, Array errors, object qualities, object timeStamps)
        {
            var results = new Dictionary<string, OpcItemValue>();
            var errorArray = (Array)errors;

            for (int i = 0; i < _itemIds.Length; i++)
            {
                var errorCode = Convert.ToInt32(errorArray.GetValue(i + 1));
                if (errorCode == 0)
                {
                    results[_itemIds[i]] = new OpcItemValue
                    {
                        Value = values.GetValue(i + 1),
                        Quality = ParseQuality(qualities, i + 1),
                        Timestamp = ParseTimestamp(timeStamps, i + 1)
                    };
                }
                else
                {
                    results[_itemIds[i]] = new OpcItemValue
                    {
                        Value = null,
                        Quality = OpcQuality.Bad,
                        Timestamp = DateTime.Now
                    };
                }
            }

            return results;
        }

        #endregion

        #region 异步读取（复用持久 Group）

        private Dictionary<string, OpcItemValue> DoAsyncRead()
        {
            _asyncReadResult = null;
            _asyncReadError = null;
            _asyncReadDone.Reset();

            // 复制 handle 数组
            Array handleArray = (Array)_serverHandleArray.Clone();
            Array asyncErrors;
            int cancelId;

            _group.AsyncRead(
                _itemIds.Length,
                ref handleArray,
                out asyncErrors,
                _readCount + 1, // TransactionID
                out cancelId);

            // 等待回调完成（可超时）
            int timeout = _config.AsyncTimeoutMs > 0 ? _config.AsyncTimeoutMs : 5000;
            bool completed = _asyncReadDone.WaitOne(timeout);

            if (!completed)
                throw new TimeoutException("异步读取超时 (" + timeout + "ms)");

            if (_asyncReadError != null)
                throw _asyncReadError;

            return _asyncReadResult ?? new Dictionary<string, OpcItemValue>();
        }

        private void OnAsyncReadComplete(
            int transactionId,
            int numItems,
            ref Array clientHandles,
            ref Array itemValues,
            ref Array qualities,
            ref Array timeStamps,
            ref Array errors)
        {
            try
            {
                var results = new Dictionary<string, OpcItemValue>();

                for (int i = 1; i <= numItems; i++)
                {
                    var clientHandle = (int)clientHandles.GetValue(i);
                    if (clientHandle >= 1 && clientHandle <= _itemIds.Length)
                    {
                        var itemId = _itemIds[clientHandle - 1];
                        var errorCode = Convert.ToInt32(errors.GetValue(i));

                        results[itemId] = errorCode == 0
                            ? new OpcItemValue
                            {
                                Value = itemValues.GetValue(i),
                                Quality = ParseQuality(qualities, i),
                                Timestamp = ParseTimestamp(timeStamps, i)
                            }
                            : new OpcItemValue
                            {
                                Value = null,
                                Quality = OpcQuality.Bad,
                                Timestamp = DateTime.Now
                            };
                    }
                }

                _asyncReadResult = results;
            }
            catch (Exception ex)
            {
                _asyncReadError = ex;
            }
            finally
            {
                _asyncReadDone.Set();
            }
        }

        #endregion

        #region 辅助方法

        private OpcQuality ParseQuality(object qualities, int index)
        {
            try
            {
                if (qualities != null)
                {
                    var qualityArray = (Array)qualities;
                    var raw = Convert.ToInt32(qualityArray.GetValue(index));
                    var major = raw & 0xC0;
                    if (Enum.IsDefined(typeof(OpcQuality), major))
                        return (OpcQuality)major;
                    return (OpcQuality)raw;
                }
            }
            catch { }
            return OpcQuality.Good; // 读取成功默认 Good
        }

        private DateTime ParseTimestamp(object timeStamps, int index)
        {
            try
            {
                if (timeStamps != null)
                {
                    var tsArray = (Array)timeStamps;
                    return (DateTime)tsArray.GetValue(index);
                }
            }
            catch { }
            return DateTime.Now;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _stopSignal.Dispose();
                _disposed = true;
            }
        }

        #endregion
    }
}
