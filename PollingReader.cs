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
    /// 后台轮询读取器（纯同步，无任何异步操作）
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

        private void InitGroup()
        {
            _groupName = "Polling_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // 关键：创建组之前设为不激活，避免触发 IDataObject::DAdvise 回调
            // 对方服务器不支持回调会导致 RPC 不可用
            _opcServer.OPCGroups.DefaultGroupIsActive = false;

            try
            {
                _group = _opcServer.OPCGroups.Add(_groupName);
            }
            catch (Exception ex)
            {
                // 即使 DAdvise 报错，组可能已创建成功，尝试继续
                if (ex.Message.Contains("RPC") || ex.Message.Contains("DAdvise"))
                {
                    // 尝试获取已创建的组
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

            // 确保不激活、不订阅（纯同步读取不需要）
            try { _group.IsActive = false; } catch { }
            try { _group.IsSubscribed = false; } catch { }

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
        }

        private void CleanupGroup()
        {
            try
            {
                if (_group != null)
                    _opcServer.OPCGroups.Remove(_groupName);
            }
            catch { }

            _group = null;
            _serverHandleArray = null;
        }

        private void PollingLoop()
        {
            while (_running)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    var results = DoSyncRead();
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

        private Dictionary<string, OpcItemValue> DoSyncRead()
        {
            Array handleArray = (Array)_serverHandleArray.Clone();

            Array values;
            Array errors;
            object qualities;
            object timeStamps;

            // 组未激活状态下 Cache 无数据，强制从 Device 读取
            _group.SyncRead(
                (short)OpcDataSource.Device,
                _itemIds.Length,
                ref handleArray,
                out values,
                out errors,
                out qualities,
                out timeStamps);

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
