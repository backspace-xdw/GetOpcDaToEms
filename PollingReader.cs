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
    ///
    /// AddItems 失败处理策略：
    ///   - 整批 COM 失败（连接/组无效）：本地重试 3 次，仍失败则抛出，由上层触发重连
    ///   - 单项失败（如 0xC004080C 等地址空间未就绪）：成功项立即投入轮询，
    ///     失败项交给后台 RetryLoop 周期性重试，成功后无缝并入轮询
    /// </summary>
    public class PollingReader : IPollingReader
    {
        private const int InitAddItemsRetryCount = 3;
        private const int InitAddItemsRetryDelayMs = 5000;
        private const int PendingRetryIntervalMs = 30000;

        private readonly object _serverObj;    // 原生 COM 对象（IOPCServer）
        private readonly string[] _itemIds;
        private volatile int _intervalMs;

        private object _groupObj;              // IOPCGroupStateMgt / IOPCItemMgt / IOPCSyncIO
        private int _serverGroupHandle;

        // 已成功加入轮询的项
        private readonly List<string> _activeItemIds = new List<string>();
        private readonly List<int> _activeServerHandles = new List<int>();
        // 等待重试加入的项
        private readonly List<string> _pendingItemIds = new List<string>();
        // 只保护 active/pending 列表的读写，COM 调用应在 lock 外进行
        // （OPC 服务器须支持多线程客户端，MTA 模型本身允许并发 COM 调用）
        private readonly object _stateLock = new object();

        private Thread _pollingThread;
        private Thread _retryThread;
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

            // 有待重试项时启动后台重试线程
            bool hasPending;
            lock (_stateLock) { hasPending = _pendingItemIds.Count > 0; }
            if (hasPending)
            {
                _retryThread = new Thread(RetryLoop);
                _retryThread.IsBackground = true;
                _retryThread.Name = "OPC_RetryAddItems";
                _retryThread.Start();
            }
        }

        public void Stop()
        {
            if (!_running) return;

            _running = false;
            _stopSignal.Set();

            if (_pollingThread != null && _pollingThread.IsAlive)
                _pollingThread.Join(5000);
            _pollingThread = null;

            if (_retryThread != null && _retryThread.IsAlive)
                _retryThread.Join(5000);
            _retryThread = null;

            CleanupGroup();
        }

        private void InitGroup()
        {
            string groupName = "Poll_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            RawOpcHelper.AddGroup(_serverObj, groupName, true, 1000,
                out _serverGroupHandle, out _groupObj);

            // 整批 COM 调用失败（连接/组无效）时本地重试，仍失败则抛出由上层重连
            AddItemsResult result = null;
            Exception lastException = null;
            for (int attempt = 1; attempt <= InitAddItemsRetryCount; attempt++)
            {
                try
                {
                    result = RawOpcHelper.AddItems(_groupObj, _itemIds);
                    lastException = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    PLog("[轮询] AddItems 整批调用失败 (第 " + attempt + "/" +
                         InitAddItemsRetryCount + " 次): " + ex.Message);
                    if (attempt < InitAddItemsRetryCount)
                        Thread.Sleep(InitAddItemsRetryDelayMs);
                }
            }
            if (lastException != null) throw lastException;

            // 按错误码分类：成功项进 active，失败项进 pending
            lock (_stateLock)
            {
                _activeItemIds.Clear();
                _activeServerHandles.Clear();
                _pendingItemIds.Clear();

                for (int i = 0; i < _itemIds.Length; i++)
                {
                    if (result.ErrorCodes[i] == 0)
                    {
                        _activeItemIds.Add(_itemIds[i]);
                        _activeServerHandles.Add(result.ServerHandles[i]);
                    }
                    else
                    {
                        _pendingItemIds.Add(_itemIds[i]);
                        PLog("[轮询] 项 '" + _itemIds[i] + "' 添加失败: " +
                             RawOpcHelper.FormatOpcError(result.ErrorCodes[i]) +
                             ", 将在后台每 " + (PendingRetryIntervalMs / 1000) + "s 重试");
                    }
                }
            }

            PLog("[轮询] 组创建成功, " + _activeItemIds.Count + " 个数据项已加入轮询" +
                 (_pendingItemIds.Count > 0 ? ", " + _pendingItemIds.Count + " 个待重试" : ""));
        }

        private void CleanupGroup()
        {
            RawOpcHelper.RemoveGroup(_serverObj, _serverGroupHandle);
            _groupObj = null;
            lock (_stateLock)
            {
                _activeItemIds.Clear();
                _activeServerHandles.Clear();
                _pendingItemIds.Clear();
            }
        }

        private void PollingLoop()
        {
            while (_running)
            {
                try
                {
                    // 只在 lock 内取快照，COM 调用在 lock 外
                    int[] handles;
                    string[] ids;
                    object groupObj;
                    lock (_stateLock)
                    {
                        handles = _activeServerHandles.ToArray();
                        ids = _activeItemIds.ToArray();
                        groupObj = _groupObj;
                    }

                    if (handles.Length == 0 || groupObj == null)
                    {
                        // 全部项都在 pending（或刚 cleanup），等下一轮
                        if (_running) _stopSignal.WaitOne(_intervalMs);
                        continue;
                    }

                    var sw = Stopwatch.StartNew();
                    var results = RawOpcHelper.SyncRead(groupObj, handles, ids, 2);
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

        /// <summary>
        /// 后台周期性重试 pending 项。成功的项动态并入 active 列表，
        /// 让轮询线程下一轮自动开始读取。
        /// </summary>
        private void RetryLoop()
        {
            while (_running)
            {
                if (_stopSignal.WaitOne(PendingRetryIntervalMs)) break;
                if (!_running) break;

                // 只在 lock 内取快照，AddItems 在 lock 外
                string[] toRetry;
                object groupObj;
                lock (_stateLock)
                {
                    if (_pendingItemIds.Count == 0) continue;
                    if (_groupObj == null) continue;
                    toRetry = _pendingItemIds.ToArray();
                    groupObj = _groupObj;
                }

                AddItemsResult result;
                try
                {
                    result = RawOpcHelper.AddItems(groupObj, toRetry);
                }
                catch (Exception ex)
                {
                    PLog("[轮询] pending 项重试 COM 调用失败: " + ex.Message);
                    continue;
                }

                int newlyAdded = 0;
                int stillPending = 0;
                lock (_stateLock)
                {
                    for (int i = 0; i < toRetry.Length; i++)
                    {
                        if (result.ErrorCodes[i] == 0)
                        {
                            _activeItemIds.Add(toRetry[i]);
                            _activeServerHandles.Add(result.ServerHandles[i]);
                            _pendingItemIds.Remove(toRetry[i]);
                            newlyAdded++;
                        }
                        else
                        {
                            stillPending++;
                        }
                    }
                }

                if (newlyAdded > 0)
                    PLog("[轮询] 重试成功并入轮询: " + newlyAdded + " 项" +
                         (stillPending > 0 ? " (剩余 " + stillPending + " 项仍失败)" : ""));
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
