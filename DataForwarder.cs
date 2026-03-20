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

            // 1. 枚举远程 OPC 服务器，验证配置的 ProgId，获取 CLSID
            OnLog("枚举 " + _config.Host + " 上的 OPC DA 服务器...");
            Guid serverClsid = Guid.Empty;
            var servers = DcomHelper.EnumRemoteServers(_config.Host, msg => OnLog(msg));

            if (servers.Count > 0)
            {
                bool found = false;
                foreach (var srv in servers)
                {
                    if (string.Equals(srv.ProgId, _config.ServerProgId,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        serverClsid = srv.Clsid;
                        OnLog("匹配成功: " + srv.ProgId + " (" + srv.Description + ")");
                        OnLog("CLSID: " + srv.Clsid);
                        break;
                    }
                }

                if (!found)
                {
                    OnLog("[警告] 配置的 ProgId '" + _config.ServerProgId + "' 未在远程服务器上找到");
                    OnLog("远程可用的 OPC DA 服务器:");
                    foreach (var srv in servers)
                    {
                        OnLog("  " + srv.ProgId + " - " + srv.Description);
                    }
                    OnLog("请修改配置文件中的 ProgId 为以上之一");
                    return;
                }
            }
            else
            {
                OnLog("[警告] 无法枚举远程服务器，直接尝试连接...");
            }

            // 2. 用 OpcEnum 拿到的 CLSID：注册本地 + 预热 DCOM 通道
            if (serverClsid != Guid.Empty)
            {
                // 注册 ProgID → CLSID 到本地注册表，让 OPCAutomation 查得到
                DcomHelper.RegisterProgIdLocally(_config.ServerProgId, serverClsid,
                    msg => OnLog(msg));

                OnLog("使用 CLSID 预热 DCOM 通道...");
                WarmUpWithClsid(serverClsid);
            }

            // 3. 连接 OPC 服务器（无限重试）
            OnLog("连接 OPC: " + _config.ServerProgId + "@" + _config.Host);
            _client = new OpcDaClient();
            _client.ConnectLog += (msg) => OnLog(msg);

            ConnectWithInfiniteRetry();

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
                            EmsPlus.GetDxId(p.EmsTagName);
                            break;
                        case EmsDataType.Cx:
                            EmsPlus.GetCxId(p.EmsTagName);
                            break;
                        default:
                            EmsPlus.GetAxId(p.EmsTagName);
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
                        DataType = EmsDataType.Ax
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
                                DataType = EmsDataType.Ax
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
        /// 用 CLSID 预热 DCOM（OpcEnum 已经拿到 CLSID，不依赖本地注册）
        /// </summary>
        private void WarmUpWithClsid(Guid clsid)
        {
            int attempt = 0;
            int delayMs = _config.RetryDelayMs;

            while (IsRunning || attempt == 0)
            {
                attempt++;
                try
                {
                    OnLog("[DCOM] CoCreateInstanceEx (CLSID) 第 " + attempt + " 次...");
                    object instance = DcomHelper.CreateRemoteInstanceByClsid(clsid, _config.Host);
                    if (instance != null)
                    {
                        Marshal.FinalReleaseComObject(instance);
                        OnLog("[DCOM] DCOM 通道已建立");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    if (attempt <= 3 || attempt % 10 == 0)
                        OnLog("[DCOM] 第 " + attempt + " 次失败: " + ex.Message);
                }

                Thread.Sleep(delayMs);
                if (attempt >= 10) delayMs = Math.Min(delayMs * 2, 30000);
            }
        }

        /// <summary>
        /// 首次连接（无限重试）
        /// </summary>
        private void ConnectWithInfiniteRetry()
        {
            int attempt = 0;
            int delayMs = _config.RetryDelayMs;

            while (true)
            {
                attempt++;
                try
                {
                    _client.Connect(_config.ServerProgId, _config.Host, 1, 0);
                    OnLog("[连接成功]");
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt <= 5 || attempt % 10 == 0)
                    {
                        OnLog("[连接] 第 " + attempt + " 次失败: " + ex.Message +
                              " | " + delayMs / 1000 + "s 后重试");
                    }
                }

                Thread.Sleep(delayMs);
                // 逐步增加间隔：3s → 5s → 10s → 30s → 60s
                if (attempt >= 20) delayMs = 60000;
                else if (attempt >= 10) delayMs = 30000;
                else if (attempt >= 5) delayMs = 10000;
                else if (attempt >= 3) delayMs = 5000;
            }
        }

        /// <summary>
        /// 断线重连（无限重试，直到恢复或用户手动停止）
        /// </summary>
        private void TryReconnect()
        {
            OnLog("[重连] OPC 服务器断开，开始无限重连...");

            // 清理旧轮询器
            if (_reader != null)
            {
                _reader.DataReceived -= OnDataReceived;
                _reader.ErrorOccurred -= OnPollingError;
                try { _reader.Stop(); } catch { }
                try { _reader.Dispose(); } catch { }
                _reader = null;
            }

            int attempt = 0;
            int delayMs = _config.RetryDelayMs;

            while (IsRunning)
            {
                attempt++;
                try
                {
                    _client.Reconnect(1, 0);
                    OnLog("[重连] 第 " + attempt + " 次重连成功");

                    StartPolling();
                    OnLog("[重连] 轮询已恢复");
                    return;
                }
                catch (Exception ex)
                {
                    // 每 10 次打印一次日志，避免刷屏
                    if (attempt <= 5 || attempt % 10 == 0)
                    {
                        OnLog("[重连] 第 " + attempt + " 次失败: " + ex.Message +
                              " | 下次重试 " + delayMs / 1000 + "s 后");
                    }
                }

                // 等待（可被 Stop 中断）
                Thread.Sleep(delayMs);

                // 逐步增加重试间隔：3s → 5s → 10s → 30s → 最大 60s
                if (attempt >= 20) delayMs = 60000;
                else if (attempt >= 10) delayMs = 30000;
                else if (attempt >= 5) delayMs = 10000;
                else if (attempt >= 3) delayMs = 5000;
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
                                Convert.ToSingle(kvp.Value.Value));
                            break;
                        case EmsDataType.Dx:
                            EmsPlus.WriteDigital(mapping.EmsTagName,
                                Convert.ToBoolean(kvp.Value.Value));
                            break;
                        case EmsDataType.Cx:
                            EmsPlus.WriteString(mapping.EmsTagName,
                                Convert.ToString(kvp.Value.Value));
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
            OnLog("[轮询错误] " + e.Exception.Message + " (连续第 " + e.ConsecutiveErrors + " 次)");

            // 连续 3 次错误就触发重连（不等到 PollingReader 的 10 次上限）
            if (e.ConsecutiveErrors >= 3)
            {
                OnLog("[断线检测] 连续 3 次读取失败，判定为断线，触发重连...");
                // 先停掉当前 PollingReader（防止它继续报错）
                var reader = _reader;
                if (reader != null)
                {
                    reader.DataReceived -= OnDataReceived;
                    reader.ErrorOccurred -= OnPollingError;
                    try { reader.Stop(); } catch { }
                    try { reader.Dispose(); } catch { }
                    _reader = null;
                }
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
