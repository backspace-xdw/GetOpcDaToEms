using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

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
    /// 后台轮询读取器
    /// 使用原生 OPC DA COM 接口（IOPCServer/IOPCSyncIO），不依赖 OPCAutomation
    /// </summary>
    public class PollingReader : IPollingReader
    {
        private readonly object _serverObj;    // 原生 COM 对象（IOPCServer）
        private readonly string[] _itemIds;
        private volatile int _intervalMs;

        private object _groupObj;              // IOPCGroupStateMgt / IOPCItemMgt / IOPCSyncIO
        private int _serverGroupHandle;
        private int[] _serverHandles;

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

        /// <summary>轮询内部日志</summary>
        public event Action<string> PollingLog;

        internal PollingReader(object serverObj, string[] itemIds, ReadConfig config, int intervalMs)
        {
            _serverObj = serverObj;
            _itemIds = (string[])itemIds.Clone();
            _intervalMs = intervalMs;
            _stopSignal = new ManualResetEvent(false);
        }

        private void PLog(string msg)
        {
            PollingLog?.Invoke(msg);
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
            string groupName = "Poll_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            PLog("[轮询] AddGroup: " + groupName);
            RawOpcHelper.AddGroup(_serverObj, groupName, true, 1000,
                out _serverGroupHandle, out _groupObj);
            PLog("[轮询] AddGroup 成功, handle=" + _serverGroupHandle);

            PLog("[轮询] AddItems: " + _itemIds.Length + " 项");
            for (int i = 0; i < _itemIds.Length; i++)
                PLog("[轮询]   " + _itemIds[i]);

            _serverHandles = RawOpcHelper.AddItems(_groupObj, _itemIds);
            PLog("[轮询] AddItems 成功");
            for (int i = 0; i < _serverHandles.Length; i++)
                PLog("[轮询]   " + _itemIds[i] + " → handle=" + _serverHandles[i]);
        }

        private void CleanupGroup()
        {
            RawOpcHelper.RemoveGroup(_serverObj, _serverGroupHandle);
            _groupObj = null;
            _serverHandles = null;
        }

        private void PollingLoop()
        {
            PLog("[轮询] 轮询线程已启动");

            while (_running)
            {
                try
                {
                    PLog("[轮询] SyncRead 开始...");
                    var sw = Stopwatch.StartNew();

                    var results = RawOpcHelper.SyncRead(
                        _groupObj, _serverHandles, _itemIds, 2);

                    sw.Stop();
                    PLog("[轮询] SyncRead 完成, " + results.Count + " 项, " + sw.ElapsedMilliseconds + "ms");
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
