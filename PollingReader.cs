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

        internal PollingReader(object serverObj, string[] itemIds, ReadConfig config, int intervalMs)
        {
            _serverObj = serverObj;
            _itemIds = (string[])itemIds.Clone();
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
            string groupName = "Poll_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // 原生接口：IOPCServer.AddGroup
            RawOpcHelper.AddGroup(_serverObj, groupName, false, 1000,
                out _serverGroupHandle, out _groupObj);

            // IOPCItemMgt.AddItems
            _serverHandles = RawOpcHelper.AddItems(_groupObj, _itemIds);
        }

        private void CleanupGroup()
        {
            RawOpcHelper.RemoveGroup(_serverObj, _serverGroupHandle);
            _groupObj = null;
            _serverHandles = null;
        }

        private void PollingLoop()
        {
            while (_running)
            {
                try
                {
                    var sw = Stopwatch.StartNew();

                    // IOPCSyncIO.Read（从 Device 读取）
                    var results = RawOpcHelper.SyncRead(
                        _groupObj, _serverHandles, _itemIds, 2);

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
