using System;
using System.Collections.Generic;

namespace OpcDaClient
{
    /// <summary>
    /// OPC → EMS 数据转发器
    /// 自动连接 → 自动浏览点位 → 自动轮询 → 自动转发
    /// </summary>
    public class DataForwarder : IDisposable
    {
        private readonly ForwarderConfig _config;
        private OpcDaClient _client;
        private IPollingReader _reader;
        private bool _disposed;

        // OpcItemId -> PointMapping
        private Dictionary<string, PointMapping> _mappingDict;

        private int _totalForwarded;
        private int _totalErrors;

        public bool IsRunning { get; private set; }
        public int TotalForwarded { get { return _totalForwarded; } }
        public int TotalErrors { get { return _totalErrors; } }
        public int PointCount { get { return _mappingDict != null ? _mappingDict.Count : 0; } }

        public event EventHandler<ForwarderLogEventArgs> Log;

        public DataForwarder(ForwarderConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// 启动：连接 → 浏览 → 轮询 → 转发
        /// </summary>
        public void Start()
        {
            if (IsRunning) return;

            OnLog("========================================");
            OnLog("OPC → EMS 数据转发器启动");
            OnLog("========================================");

            // 1. 连接 OPC 服务器
            OnLog("连接 OPC: " + _config.ServerProgId + "@" + _config.Host);
            _client = new OpcDaClient();
            _client.Connect(_config.ServerProgId, _config.Host);
            OnLog("OPC 连接成功");

            // 2. 确定点位列表
            if (_config.Points.Count > 0)
            {
                // 配置文件中已指定点位，直接使用
                OnLog("使用配置文件中的 " + _config.Points.Count + " 个点位");
            }
            else
            {
                // 自动浏览 OPC 服务器，发现所有点位
                OnLog("自动浏览 OPC 服务器...");
                AutoDiscoverPoints();
                OnLog("发现 " + _config.Points.Count + " 个点位");
            }

            if (_config.Points.Count == 0)
            {
                OnLog("[错误] 未发现任何点位，停止");
                _client.Disconnect();
                _client.Dispose();
                _client = null;
                return;
            }

            // 构建映射字典
            _mappingDict = new Dictionary<string, PointMapping>();
            foreach (var p in _config.Points)
            {
                _mappingDict[p.OpcItemId] = p;
            }

            // 3. 预热 EMS ID 缓存
            OnLog("预热 EMS 变量 ID...");
            EmsPlus.ClearCache();
            int ok = 0, fail = 0;
            foreach (var p in _config.Points)
            {
                try
                {
                    int emsId;
                    switch (p.DataType)
                    {
                        case EmsDataType.Dx:
                            emsId = EmsPlus.GetDxId(p.EmsTagName, p.EmsSrvNo);
                            break;
                        case EmsDataType.Cx:
                            emsId = EmsPlus.GetCxId(p.EmsTagName, p.EmsSrvNo);
                            break;
                        default:
                            emsId = EmsPlus.GetAxId(p.EmsTagName, p.EmsSrvNo);
                            break;
                    }
                    ok++;
                }
                catch
                {
                    fail++;
                }
            }
            OnLog("EMS ID: " + ok + " 成功, " + fail + " 失败");

            // 4. 启动轮询
            var readConfig = _config.GetReadConfig();
            var opcItemIds = _config.GetOpcItemIds();

            OnLog("启动轮询: " + opcItemIds.Length + " 项, 间隔 " + _config.PollingIntervalMs + "ms");

            _reader = _client.CreatePollingReader(opcItemIds, readConfig, _config.PollingIntervalMs);
            _reader.DataReceived += OnDataReceived;
            _reader.ErrorOccurred += OnPollingError;
            _reader.Start();

            IsRunning = true;
            _totalForwarded = 0;
            _totalErrors = 0;

            OnLog("转发运行中...");
            OnLog("----------------------------------------");
        }

        /// <summary>
        /// 自动浏览 OPC 服务器所有点位，生成映射
        /// OPC ItemId 直接作为 EMS 点名，默认 Ax 类型
        /// </summary>
        private void AutoDiscoverPoints()
        {
            _config.Points.Clear();

            // 先获取所有分支
            var branches = _client.BrowseServer();

            if (branches.Count == 0)
            {
                // 无分支，直接浏览根节点
                var items = _client.BrowseItems("");
                foreach (var item in items)
                {
                    _config.Points.Add(new PointMapping
                    {
                        OpcItemId = item.ItemId,
                        EmsTagName = item.ItemId,
                        DataType = EmsDataType.Ax,
                        EmsSrvNo = 0
                    });
                }
            }
            else
            {
                // 遍历每个分支
                foreach (var branch in branches)
                {
                    try
                    {
                        var items = _client.BrowseItems(branch);
                        OnLog("  " + branch + ": " + items.Count + " 个点位");
                        foreach (var item in items)
                        {
                            _config.Points.Add(new PointMapping
                            {
                                OpcItemId = item.ItemId,
                                EmsTagName = item.ItemId,
                                DataType = EmsDataType.Ax,
                                EmsSrvNo = 0
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLog("  " + branch + ": 浏览失败 - " + ex.Message);
                    }
                }
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;

            OnLog("----------------------------------------");
            OnLog("正在停止...");

            if (_reader != null)
            {
                _reader.DataReceived -= OnDataReceived;
                _reader.ErrorOccurred -= OnPollingError;
                _reader.Stop();
                _reader.Dispose();
                _reader = null;
            }

            if (_client != null)
            {
                _client.Disconnect();
                _client.Dispose();
                _client = null;
            }

            IsRunning = false;
            OnLog("已停止 | 转发: " + _totalForwarded + " | 错误: " + _totalErrors);
            OnLog("========================================");
        }

        /// <summary>
        /// 数据到达 → 转发到 EMS
        /// </summary>
        private void OnDataReceived(object sender, PollingDataEventArgs e)
        {
            int forwarded = 0;
            int errors = 0;

            foreach (var kvp in e.Values)
            {
                if (kvp.Value.Quality == OpcQuality.Bad || kvp.Value.Value == null)
                    continue;

                PointMapping mapping;
                if (!_mappingDict.TryGetValue(kvp.Key, out mapping))
                    continue;

                try
                {
                    switch (mapping.DataType)
                    {
                        case EmsDataType.Ax:
                            EmsPlus.WriteAnalog(mapping.EmsTagName,
                                Convert.ToSingle(kvp.Value.Value), mapping.EmsSrvNo);
                            break;
                        case EmsDataType.Dx:
                            EmsPlus.WriteDigital(mapping.EmsTagName,
                                Convert.ToBoolean(kvp.Value.Value), mapping.EmsSrvNo);
                            break;
                        case EmsDataType.Cx:
                            EmsPlus.WriteString(mapping.EmsTagName,
                                Convert.ToString(kvp.Value.Value), 0, mapping.EmsSrvNo);
                            break;
                    }
                    forwarded++;
                }
                catch
                {
                    errors++;
                }
            }

            _totalForwarded += forwarded;
            _totalErrors += errors;

            OnLog("[#" + e.ReadCount + " " + e.ReadTime.ToString("HH:mm:ss") + "] " +
                  e.Values.Count + " 项 (" + e.ReadDurationMs + "ms) → " +
                  forwarded + " 转发" +
                  (errors > 0 ? ", " + errors + " 失败" : ""));
        }

        private void OnPollingError(object sender, PollingErrorEventArgs e)
        {
            OnLog("[轮询错误] " + e.Exception.Message + " (第 " + e.ConsecutiveErrors + " 次)");
            if (e.ConsecutiveErrors >= 10)
            {
                OnLog("[自动停止] 连续错误超过 10 次");
                IsRunning = false;
            }
        }

        private void OnLog(string message)
        {
            Log?.Invoke(this, new ForwarderLogEventArgs { Message = message, Time = DateTime.Now });
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _disposed = true;
            }
        }
    }

    public class ForwarderLogEventArgs : EventArgs
    {
        public string Message { get; set; }
        public DateTime Time { get; set; }
    }
}
