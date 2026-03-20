using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpcDaClient
{
    /// <summary>
    /// OPC → EMS 数据转发器
    /// 使用原生 OPC DA COM 接口，不依赖 OPCAutomation
    /// </summary>
    public class DataForwarder : IDisposable
    {
        private readonly ForwarderConfig _config;
        private IPollingReader _reader;
        private bool _disposed;

        // DCOM 通道 COM 对象（IOPCServer），程序运行期间保持
        private object _dcomChannel;

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

            // 1. 枚举远程 OPC 服务器，验证 ProgId，获取 CLSID
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
                    OnLog("[警告] 配置的 ProgId '" + _config.ServerProgId + "' 未找到");
                    OnLog("远程可用的 OPC DA 服务器:");
                    foreach (var srv in servers)
                        OnLog("  " + srv.ProgId + " - " + srv.Description);
                    OnLog("请修改配置文件中的 ProgId");
                    return;
                }
            }
            else
            {
                OnLog("[警告] 无法枚举远程服务器，直接尝试连接...");
            }

            // 2. 注册 ProgID 到本地 + 建立 DCOM 通道（无限重试）
            if (serverClsid != Guid.Empty)
            {
                DcomHelper.RegisterProgIdLocally(_config.ServerProgId, serverClsid,
                    msg => OnLog(msg));
            }

            OnLog("建立 DCOM 通道...");
            ConnectDcom(serverClsid);

            if (_dcomChannel == null)
            {
                OnLog("[错误] DCOM 通道建立失败");
                return;
            }

            // 3. 验证配置的点位
            if (_config.Points.Count == 0)
            {
                OnLog("[错误] 配置文件中没有点位，请在 [Points] 段添加");
                return;
            }
            OnLog("使用配置文件中的 " + _config.Points.Count + " 个点位");

            // 构建映射字典
            _mappingDict = new Dictionary<string, PointMapping>();
            foreach (var p in _config.Points)
                _mappingDict[p.OpcItemId] = p;

            // 4. 预热 EMS ID 缓存
            OnLog("预热 EMS 变量 ID...");
            EmsPlus.ClearCache();
            int ok = 0, fail = 0;
            foreach (var p in _config.Points)
            {
                try
                {
                    switch (p.DataType)
                    {
                        case EmsDataType.Dx: EmsPlus.GetDxId(p.EmsTagName); break;
                        case EmsDataType.Cx: EmsPlus.GetCxId(p.EmsTagName); break;
                        default: EmsPlus.GetAxId(p.EmsTagName); break;
                    }
                    ok++;
                }
                catch { fail++; }
            }
            OnLog("EMS ID: " + ok + " 成功, " + fail + " 失败");

            // 5. 启动轮询
            StartPolling();

            IsRunning = true;
            _totalForwarded = 0;
            _totalErrors = 0;

            OnLog("转发运行中...");
            OnLog("----------------------------------------");
        }

        /// <summary>
        /// 建立 DCOM 通道（无限重试）
        /// </summary>
        private void ConnectDcom(Guid clsid)
        {
            if (clsid == Guid.Empty)
            {
                // 没有 CLSID（OpcEnum 不可用），尝试通过 ProgID
                try
                {
                    _dcomChannel = DcomHelper.CreateRemoteInstance(_config.ServerProgId, _config.Host);
                    OnLog("[DCOM] 通道已建立");
                    return;
                }
                catch (Exception ex)
                {
                    OnLog("[DCOM] 通过 ProgID 创建失败: " + ex.Message);
                    return;
                }
            }

            int attempt = 0;
            int delayMs = _config.RetryDelayMs;

            while (true)
            {
                attempt++;
                try
                {
                    if (attempt <= 3 || attempt % 10 == 0)
                        OnLog("[DCOM] CoCreateInstanceEx 第 " + attempt + " 次...");

                    _dcomChannel = DcomHelper.CreateRemoteInstanceByClsid(clsid, _config.Host);

                    if (_dcomChannel != null)
                    {
                        OnLog("[DCOM] 通道已建立");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    if (attempt <= 3 || attempt % 10 == 0)
                        OnLog("[DCOM] 第 " + attempt + " 次失败: " + ex.Message);
                }

                Thread.Sleep(delayMs);
                if (attempt >= 20) delayMs = 60000;
                else if (attempt >= 10) delayMs = 30000;
                else if (attempt >= 5) delayMs = 10000;
                else if (attempt >= 3) delayMs = 5000;
            }
        }

        private void StartPolling()
        {
            var opcItemIds = _config.GetOpcItemIds();
            OnLog("启动轮询: " + opcItemIds.Length + " 项, 间隔 " + _config.PollingIntervalMs + "ms");

            _reader = new PollingReader(_dcomChannel, opcItemIds, _config.GetReadConfig(), _config.PollingIntervalMs);
            _reader.DataReceived += OnDataReceived;
            _reader.ErrorOccurred += OnPollingError;
            _reader.Start();
        }

        /// <summary>
        /// 断线重连（无限重试）
        /// </summary>
        private void TryReconnect()
        {
            OnLog("[重连] OPC 断开，开始重连...");

            // 清理旧轮询器
            if (_reader != null)
            {
                _reader.DataReceived -= OnDataReceived;
                _reader.ErrorOccurred -= OnPollingError;
                try { _reader.Stop(); } catch { }
                try { _reader.Dispose(); } catch { }
                _reader = null;
            }

            // 释放旧 DCOM 通道
            if (_dcomChannel != null)
            {
                try { Marshal.FinalReleaseComObject(_dcomChannel); } catch { }
                _dcomChannel = null;
            }

            int attempt = 0;
            int delayMs = _config.RetryDelayMs;

            // 需要重新获取 CLSID
            Guid clsid = Guid.Empty;
            try
            {
                var servers = DcomHelper.EnumRemoteServers(_config.Host);
                foreach (var srv in servers)
                {
                    if (string.Equals(srv.ProgId, _config.ServerProgId,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        clsid = srv.Clsid;
                        break;
                    }
                }
            }
            catch { }

            while (IsRunning)
            {
                attempt++;
                try
                {
                    if (clsid != Guid.Empty)
                        _dcomChannel = DcomHelper.CreateRemoteInstanceByClsid(clsid, _config.Host);
                    else
                        _dcomChannel = DcomHelper.CreateRemoteInstance(_config.ServerProgId, _config.Host);

                    if (_dcomChannel != null)
                    {
                        OnLog("[重连] 第 " + attempt + " 次成功");
                        StartPolling();
                        OnLog("[重连] 轮询已恢复");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    if (attempt <= 5 || attempt % 10 == 0)
                        OnLog("[重连] 第 " + attempt + " 次失败: " + ex.Message +
                              " | " + delayMs / 1000 + "s 后重试");
                }

                Thread.Sleep(delayMs);
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

            if (_dcomChannel != null)
            {
                try { Marshal.FinalReleaseComObject(_dcomChannel); } catch { }
                _dcomChannel = null;
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
                catch { errors++; }
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

            if (e.ConsecutiveErrors >= 3)
            {
                OnLog("[断线检测] 连续 3 次失败，触发重连...");
                var reconnectThread = new Thread(() =>
                {
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
                });
                reconnectThread.IsBackground = true;
                reconnectThread.Name = "OPC_Reconnect";
                reconnectThread.Start();
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
