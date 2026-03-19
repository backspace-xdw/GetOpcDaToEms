using System;
using System.Collections.Generic;
using System.Threading;

namespace OpcDaClient
{
    /// <summary>
    /// OPC → EMS 数据转发器
    /// 自动连接（带重试）→ 自动浏览 → 自动轮询 → 自动转发
    /// 轮询中断线自动重连
    /// </summary>
    public class DataForwarder : IDisposable
    {
        private readonly ForwarderConfig _config;
        private OpcDaClient _client;
        private IPollingReader _reader;
        private bool _disposed;

        private Dictionary<string, PointMapping> _mappingDict;

        private int _totalForwarded;
        private int _totalErrors;

        public bool IsRunning { get; private set; }
        public int TotalForwarded { get { return _totalForwarded; } }
        public int TotalErrors { get { return _totalErrors; } }

        public event EventHandler<ForwarderLogEventArgs> Log;

        public DataForwarder(ForwarderConfig config)
        {
            _config = config;
        }

        public void Start()
        {
            if (IsRunning) return;

            OnLog("========================================");
            OnLog("OPC → EMS 数据转发器启动");
            OnLog("========================================");

            // 1. 连接 OPC 服务器（带重试）
            OnLog("连接 OPC: " + _config.ServerProgId + "@" + _config.Host +
                  " (最多重试 " + _config.RetryCount + " 次, 间隔 " + _config.RetryDelayMs + "ms)");
            _client = new OpcDaClient();

            for (int attempt = 1; attempt <= _config.RetryCount; attempt++)
            {
                try
                {
                    _client.Connect(_config.ServerProgId, _config.Host, 1, 0);
                    OnLog("OPC 连接成功");
                    break;
                }
                catch (Exception ex)
                {
                    OnLog("[第 " + attempt + "/" + _config.RetryCount + " 次] 连接失败: " + ex.Message);
                    if (attempt >= _config.RetryCount)
                    {
                        OnLog("[错误] 连接重试耗尽，退出");
                        _client.Dispose();
                        _client = null;
                        return;
                    }
                    OnLog("等待 " + _config.RetryDelayMs + "ms 后重试...");
                    Thread.Sleep(_config.RetryDelayMs);
                }
            }

            // 2. 确定点位列表
            if (_config.Points.Count > 0)
            {
                OnLog("使用配置文件中的 " + _config.Points.Count + " 个点位");
            }
            else
            {
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
                    switch (p.DataType)
                    {
                        case EmsDataType.Dx:
                            EmsPlus.GetDxId(p.EmsTagName, p.EmsSrvNo);
                            break;
                        case EmsDataType.Cx:
                            EmsPlus.GetCxId(p.EmsTagName, p.EmsSrvNo);
                            break;
                        default:
                            EmsPlus.GetAxId(p.EmsTagName, p.EmsSrvNo);
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
            StartPolling();

            IsRunning = true;
            _totalForwarded = 0;
            _totalErrors = 0;

            OnLog("转发运行中...");
            OnLog("----------------------------------------");
        }

        private void StartPolling()
        {
            var readConfig = _config.GetReadConfig();
            var opcItemIds = _config.GetOpcItemIds();

            OnLog("启动轮询: " + opcItemIds.Length + " 项, 间隔 " + _config.PollingIntervalMs + "ms");

            _reader = _client.CreatePollingReader(opcItemIds, readConfig, _config.PollingIntervalMs);
            _reader.DataReceived += OnDataReceived;
            _reader.ErrorOccurred += OnPollingError;
            _reader.Start();
        }

        private void AutoDiscoverPoints()
        {
            _config.Points.Clear();

            var branches = _client.BrowseServer();

            if (branches.Count == 0)
            {
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

        /// <summary>
        /// 断线重连
        /// </summary>
        private void TryReconnect()
        {
            OnLog("[重连] 尝试重新连接 OPC 服务器...");

            // 清理旧轮询器
            if (_reader != null)
            {
                _reader.DataReceived -= OnDataReceived;
                _reader.ErrorOccurred -= OnPollingError;
                try { _reader.Stop(); } catch { }
                try { _reader.Dispose(); } catch { }
                _reader = null;
            }

            for (int attempt = 1; attempt <= _config.RetryCount; attempt++)
            {
                try
                {
                    _client.Reconnect(1, 0);
                    OnLog("[重连] 第 " + attempt + " 次重连成功");

                    // 重新启动轮询
                    StartPolling();
                    OnLog("[重连] 轮询已恢复");
                    return;
                }
                catch (Exception ex)
                {
                    OnLog("[重连] 第 " + attempt + "/" + _config.RetryCount + " 次失败: " + ex.Message);
                    if (attempt < _config.RetryCount)
                    {
                        Thread.Sleep(_config.RetryDelayMs);
                    }
                }
            }

            OnLog("[重连] 重连耗尽，停止转发");
            IsRunning = false;
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

            // 连续错误达到阈值，尝试重连而不是直接退出
            if (e.ConsecutiveErrors >= 5)
            {
                OnLog("[轮询中断] 连续 5 次错误，触发重连...");
                TryReconnect();
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
