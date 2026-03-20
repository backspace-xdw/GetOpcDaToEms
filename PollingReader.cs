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
    /// 后台轮询读取器
    /// 优先用 OPCAutomation 类型，类型转换失败则回退反射
    /// </summary>
    public class PollingReader : IPollingReader
    {
        private readonly object _opcServer;
        private readonly string[] _itemIds;
        private readonly ReadConfig _config;
        private volatile int _intervalMs;

        private OPCGroup _typedGroup;   // OPCAutomation 类型（优先）
        private object _rawGroup;       // 反射回退
        private bool _useReflection;
        private string _groupName;
        private int[] _serverHandles;
        private Array _cachedHandleArray;

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

        internal PollingReader(object opcServer, string[] itemIds, ReadConfig config, int intervalMs)
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

        #region 初始化

        private void InitGroup()
        {
            _groupName = "Polling_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _useReflection = false;

            // 先尝试 OPCAutomation 类型
            try
            {
                var server = (OPCServer)_opcServer;
                var groups = server.OPCGroups;
                _typedGroup = groups.Add(_groupName);
                try { _typedGroup.IsActive = false; } catch { }
                try { _typedGroup.IsSubscribed = false; } catch { }

                var opcItems = _typedGroup.OPCItems;
                _serverHandles = new int[_itemIds.Length];
                for (int i = 0; i < _itemIds.Length; i++)
                {
                    var opcItem = opcItems.AddItem(_itemIds[i], i + 1);
                    _serverHandles[i] = opcItem.ServerHandle;
                }

                BuildHandleArray();
                return;
            }
            catch (InvalidCastException)
            {
                // OPCAutomation 类型转换失败，回退反射
                _useReflection = true;
            }

            // 回退：反射方式
            InitGroupReflection();
        }

        private void InitGroupReflection()
        {
            object groups = Reflect.Get(_opcServer, "OPCGroups");
            _rawGroup = Reflect.Call(groups, "Add", _groupName);
            try { Reflect.Set(_rawGroup, "IsActive", false); } catch { }
            try { Reflect.Set(_rawGroup, "IsSubscribed", false); } catch { }

            object opcItems = Reflect.Get(_rawGroup, "OPCItems");
            _serverHandles = new int[_itemIds.Length];
            for (int i = 0; i < _itemIds.Length; i++)
            {
                object opcItem = Reflect.Call(opcItems, "AddItem", _itemIds[i], i + 1);
                _serverHandles[i] = (int)Reflect.Get(opcItem, "ServerHandle");
            }

            BuildHandleArray();
        }

        private void BuildHandleArray()
        {
            _cachedHandleArray = Array.CreateInstance(
                typeof(int), new int[] { _itemIds.Length }, new int[] { 1 });
            for (int i = 0; i < _serverHandles.Length; i++)
                _cachedHandleArray.SetValue(_serverHandles[i], i + 1);
        }

        private void CleanupGroup()
        {
            try
            {
                if (_useReflection && _rawGroup != null)
                {
                    object groups = Reflect.Get(_opcServer, "OPCGroups");
                    Reflect.Call(groups, "Remove", _groupName);
                }
                else if (_typedGroup != null)
                {
                    ((OPCServer)_opcServer).OPCGroups.Remove(_groupName);
                }
            }
            catch { }

            _typedGroup = null;
            _rawGroup = null;
            _serverHandles = null;
            _cachedHandleArray = null;
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

        #endregion

        #region 同步读取

        private Dictionary<string, OpcItemValue> DoSyncRead()
        {
            Array handleArray = (Array)_cachedHandleArray.Clone();

            if (_useReflection)
                return SyncReadReflection(handleArray);
            else
                return SyncReadTyped(handleArray);
        }

        private Dictionary<string, OpcItemValue> SyncReadTyped(Array handleArray)
        {
            Array values;
            Array errors;
            object qualities;
            object timeStamps;

            _typedGroup.SyncRead(
                (short)OpcDataSource.Device,
                _itemIds.Length,
                ref handleArray,
                out values,
                out errors,
                out qualities,
                out timeStamps);

            return ParseResults(values, errors, qualities, timeStamps);
        }

        private Dictionary<string, OpcItemValue> SyncReadReflection(Array handleArray)
        {
            object[] args = new object[]
            {
                (short)OpcDataSource.Device, _itemIds.Length,
                handleArray, null, null, null, null
            };

            _rawGroup.GetType().InvokeMember("SyncRead",
                System.Reflection.BindingFlags.InvokeMethod,
                null, _rawGroup, args);

            return ParseResults((Array)args[3], (Array)args[4], args[5], args[6]);
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

        /// <summary>
        /// 反射辅助（回退用，绕过 OPCAutomation COM 类型转换问题）
        /// </summary>
        private static class Reflect
        {
            public static object Get(object obj, string name)
            {
                return obj.GetType().InvokeMember(name,
                    System.Reflection.BindingFlags.GetProperty, null, obj, null);
            }

            public static void Set(object obj, string name, object value)
            {
                obj.GetType().InvokeMember(name,
                    System.Reflection.BindingFlags.SetProperty, null, obj, new object[] { value });
            }

            public static object Call(object obj, string name, params object[] args)
            {
                return obj.GetType().InvokeMember(name,
                    System.Reflection.BindingFlags.InvokeMethod, null, obj, args);
            }
        }
    }
}
