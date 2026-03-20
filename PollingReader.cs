using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using OPCAutomation;

namespace OpcDaClient
{
    public interface IPollingReader : IDisposable
    {
        bool IsRunning { get; }
        int IntervalMs { get; set; }
        int ReadCount { get; }

        event EventHandler<PollingDataEventArgs> DataReceived;
        event EventHandler<PollingErrorEventArgs> ErrorOccurred;

        void Start();
        void Stop();
    }

    public class PollingDataEventArgs : EventArgs
    {
        public Dictionary<string, OpcItemValue> Values { get; set; }
        public long ReadDurationMs { get; set; }
        public int ReadCount { get; set; }
        public DateTime ReadTime { get; set; }
    }

    public class PollingErrorEventArgs : EventArgs
    {
        public Exception Exception { get; set; }
        public int ConsecutiveErrors { get; set; }
    }

    /// <summary>
    /// 后台轮询读取器，支持同步/异步模式切换
    /// </summary>
    public class PollingReader : IPollingReader
    {
        private readonly OPCServer _opcServer;
        private readonly string[] _itemIds;
        private readonly ReadConfig _config;
        private volatile int _intervalMs;

        private OPCGroup _group;
        private string _groupName;
        private Array _serverHandleArray;

        private Thread _pollingThread;
        private volatile bool _running;
        private readonly ManualResetEvent _stopSignal;

        // 异步读取同步器
        private ManualResetEvent _asyncReadDone;
        private Dictionary<string, OpcItemValue> _asyncReadResult;
        private Exception _asyncReadError;

        private int _readCount;
        private int _consecutiveErrors;
        private bool _disposed;

        public bool IsRunning { get { return _running; } }
        public int IntervalMs { get { return _intervalMs; } set { _intervalMs = value; } }
        public int ReadCount { get { return _readCount; } }

        public event EventHandler<PollingDataEventArgs> DataReceived;
        public event EventHandler<PollingErrorEventArgs> ErrorOccurred;

        internal PollingReader(OPCServer opcServer, string[] itemIds, ReadConfig config, int intervalMs)
        {
            _opcServer = opcServer;
            _itemIds = (string[])itemIds.Clone();
            _config = config ?? new ReadConfig();
            _intervalMs = intervalMs;
            _stopSignal = new ManualResetEvent(false);
        }

        public void Start()
        {
            if (_running)
                throw new InvalidOperationException("轮询已在运行中");

            InitGroup();

            _running = true;
            _readCount = 0;
            _consecutiveErrors = 0;
            _stopSignal.Reset();

            _pollingThread = new Thread(PollingLoop);
            _pollingThread.IsBackground = true;
            _pollingThread.Name = "OPC_Polling";
            _pollingThread.Start();
        }

        public void Stop()
        {
            if (!_running) return;

            _running = false;
            _stopSignal.Set();

            if (_pollingThread != null && _pollingThread.IsAlive)
                _pollingThread.Join(5000);
            _pollingThread = null;

            CleanupGroup();
        }

        #region 初始化与清理

        private void InitGroup()
        {
            _groupName = "Polling_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            if (_config.Mode == ReadMode.Sync)
            {
                // 同步模式：组不激活，避免 DAdvise 回调
                _opcServer.OPCGroups.DefaultGroupIsActive = false;
            }
            else
            {
                // 异步模式：组需要激活
                _opcServer.OPCGroups.DefaultGroupIsActive = true;
            }

            try
            {
                _group = _opcServer.OPCGroups.Add(_groupName);
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("RPC") || ex.Message.Contains("DAdvise"))
                {
                    try
                    {
                        _group = _opcServer.OPCGroups.GetOPCGroup(_groupName);
                    }
                    catch
                    {
                        throw new Exception("创建 OPC 组失败: " + ex.Message, ex);
                    }
                }
                else
                {
                    throw;
                }
            }

            if (_config.Mode == ReadMode.Sync)
            {
                try { _group.IsActive = false; } catch { }
            }
            else
            {
                try { _group.IsActive = true; } catch { }
            }
            try { _group.IsSubscribed = false; } catch { }

            // 添加数据项并缓存 handle
            var opcItems = _group.OPCItems;
            var serverHandles = new int[_itemIds.Length];

            for (int i = 0; i < _itemIds.Length; i++)
            {
                var opcItem = opcItems.AddItem(_itemIds[i], i + 1);
                serverHandles[i] = opcItem.ServerHandle;
            }

            _serverHandleArray = Array.CreateInstance(
                typeof(int), new int[] { _itemIds.Length }, new int[] { 1 });
            for (int i = 0; i < serverHandles.Length; i++)
            {
                _serverHandleArray.SetValue(serverHandles[i], i + 1);
            }

            // 异步模式注册回调
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
                        _group.AsyncReadComplete -= OnAsyncReadComplete;
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

                    if (_consecutiveErrors >= 10)
                    {
                        _running = false;
                        break;
                    }
                }

                if (_running)
                    _stopSignal.WaitOne(_intervalMs);
            }
        }

        #endregion

        #region 同步读取

        private Dictionary<string, OpcItemValue> DoSyncRead()
        {
            Array handleArray = (Array)_serverHandleArray.Clone();

            Array values;
            Array errors;
            object qualities;
            object timeStamps;

            // 同步模式组未激活，强制 Device；异步模式可用配置的 DataSource
            short source = (_config.Mode == ReadMode.Sync)
                ? (short)OpcDataSource.Device
                : (short)_config.DataSource;

            _group.SyncRead(
                source,
                _itemIds.Length,
                ref handleArray,
                out values,
                out errors,
                out qualities,
                out timeStamps);

            return ParseResults(values, errors, qualities, timeStamps);
        }

        #endregion

        #region 异步读取

        private Dictionary<string, OpcItemValue> DoAsyncRead()
        {
            _asyncReadResult = null;
            _asyncReadError = null;
            _asyncReadDone.Reset();

            Array handleArray = (Array)_serverHandleArray.Clone();
            Array asyncErrors;
            int cancelId;

            _group.AsyncRead(
                _itemIds.Length,
                ref handleArray,
                out asyncErrors,
                _readCount + 1,
                out cancelId);

            int timeout = _config.AsyncTimeoutMs > 0 ? _config.AsyncTimeoutMs : 5000;
            bool completed = _asyncReadDone.WaitOne(timeout);

            if (!completed)
                throw new TimeoutException("异步读取超时 (" + timeout + "ms)");

            if (_asyncReadError != null)
                throw _asyncReadError;

            return _asyncReadResult ?? new Dictionary<string, OpcItemValue>();
        }

        private void OnAsyncReadComplete(
            int transactionId, int numItems,
            ref Array clientHandles, ref Array itemValues,
            ref Array qualities, ref Array timeStamps, ref Array errors)
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

        #region 结果解析

        private Dictionary<string, OpcItemValue> ParseResults(
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

        private OpcQuality ParseQuality(object qualities, int index)
        {
            try
            {
                if (qualities != null)
                {
                    var raw = Convert.ToInt32(((Array)qualities).GetValue(index));
                    var major = raw & 0xC0;
                    if (Enum.IsDefined(typeof(OpcQuality), major))
                        return (OpcQuality)major;
                }
            }
            catch { }
            return OpcQuality.Good;
        }

        private DateTime ParseTimestamp(object timeStamps, int index)
        {
            try
            {
                if (timeStamps != null)
                    return (DateTime)((Array)timeStamps).GetValue(index);
            }
            catch { }
            return DateTime.Now;
        }

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _stopSignal.Dispose();
                _disposed = true;
            }
        }
    }
}
