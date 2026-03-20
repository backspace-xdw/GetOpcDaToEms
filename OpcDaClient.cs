using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using OPCAutomation;

namespace OpcDaClient
{
    public class OpcDaClient : IOpcDaClient
    {
        private OPCServer _opcServer;
        private readonly List<PollingReader> _activePollingReaders;
        private bool _disposed;

        public bool IsConnected { get; private set; }
        public string ServerProgId { get; private set; }
        public string Host { get; private set; }

        /// <summary>连接过程日志（供外部显示）</summary>
        public event Action<string> ConnectLog;

        #region DCOM 安全

        [DllImport("ole32.dll")]
        private static extern int CoSetProxyBlanket(
            [MarshalAs(UnmanagedType.IUnknown)] object pProxy,
            uint dwAuthnSvc, uint dwAuthzSvc, IntPtr pServerPrincName,
            uint dwAuthnLevel, uint dwImpLevel,
            IntPtr pAuthInfo, uint dwCapabilities);

        private static void SetProxySecurity(object comObject)
        {
            try
            {
                CoSetProxyBlanket(comObject,
                    10, 0, IntPtr.Zero, 1, 3, IntPtr.Zero, 0);
            }
            catch { }
        }

        #endregion

        public OpcDaClient()
        {
            _activePollingReaders = new List<PollingReader>();
        }

        public void Connect(string serverProgId, string host = "localhost")
        {
            Connect(serverProgId, host, 5, 3000);
        }

        public void Connect(string serverProgId, string host, int retryCount, int retryDelayMs)
        {
            ServerProgId = serverProgId;
            Host = host;

            // DCOM 预热由 DataForwarder 用 CLSID 完成，这里直接连接
            Exception lastEx = null;

            for (int attempt = 1; attempt <= retryCount; attempt++)
            {
                try
                {
                    if (_opcServer != null)
                    {
                        try { _opcServer.Disconnect(); } catch { }
                        try { Marshal.FinalReleaseComObject(_opcServer); } catch { }
                        _opcServer = null;
                    }

                    Log("[连接 " + attempt + "/" + retryCount + "] new OPCServer...");
                    _opcServer = new OPCServer();
                    SetProxySecurity(_opcServer);

                    Log("[连接 " + attempt + "/" + retryCount + "] Connect(" + serverProgId + ", " + host + ")...");

                    try
                    {
                        _opcServer.Connect(serverProgId, host);
                    }
                    catch (Exception connectEx)
                    {
                        // 第一次 Connect 可能因 IOPCShutdown 回调失败抛 E_FAIL
                        // 但 DCOM 通道已建立，重新创建 OPCServer 再连一次
                        Log("[连接] 首次 Connect 异常: " + connectEx.Message + "，DCOM 通道可能已建立，重试...");

                        try { Marshal.FinalReleaseComObject(_opcServer); } catch { }
                        _opcServer = new OPCServer();
                        SetProxySecurity(_opcServer);

                        _opcServer.Connect(serverProgId, host);
                        Log("[连接] 重试 Connect 成功");
                    }

                    IsConnected = true;
                    Log("[连接成功]");
                    return;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    Log("[连接 " + attempt + "/" + retryCount + "] 失败: " + ex.Message);
                    if (attempt < retryCount)
                    {
                        Log("等待 " + retryDelayMs + "ms...");
                        Thread.Sleep(retryDelayMs);
                    }
                }
            }

            throw new Exception("连接 OPC 服务器失败 (重试 " + retryCount + " 次): " + lastEx.Message, lastEx);
        }

        /// <summary>
        /// 使用标准 DCOM 方式预热（与 OPC 测试工具一致）
        /// CoCreateInstanceEx + COSERVERINFO + COAUTHINFO
        /// </summary>
        private void WarmUpDcom(string host, int retryCount, int retryDelayMs)
        {
            DcomHelper.WarmUp(ServerProgId, host, retryCount, retryDelayMs,
                msg => Log(msg));
        }

        public void Reconnect(int retryCount = 5, int retryDelayMs = 3000)
        {
            IsConnected = false;
            if (_opcServer != null)
            {
                try { _opcServer.Disconnect(); } catch { }
                try { Marshal.FinalReleaseComObject(_opcServer); } catch { }
                _opcServer = null;
            }
            Connect(ServerProgId, Host, retryCount, retryDelayMs);
        }

        public void Disconnect()
        {
            if (_opcServer != null && IsConnected)
            {
                foreach (var reader in _activePollingReaders.ToArray())
                {
                    try { reader.Stop(); } catch { }
                }
                _activePollingReaders.Clear();

                _opcServer.Disconnect();
                IsConnected = false;
            }
        }

        public List<string> BrowseServer()
        {
            EnsureConnected();
            var branches = new List<string>();
            OPCBrowser browser = _opcServer.CreateBrowser();
            browser.MoveToRoot();
            browser.ShowBranches();
            foreach (string branch in browser)
                branches.Add(branch);
            return branches;
        }

        public List<OpcItem> BrowseItems(string branch = "")
        {
            EnsureConnected();
            var items = new List<OpcItem>();
            OPCBrowser browser = _opcServer.CreateBrowser();
            if (!string.IsNullOrEmpty(branch))
            {
                browser.MoveToRoot();
                browser.MoveDown(branch);
            }
            browser.ShowLeafs(true);
            foreach (string item in browser)
            {
                items.Add(new OpcItem
                {
                    ItemId = browser.GetItemID(item),
                    Name = item
                });
            }
            return items;
        }

        public IPollingReader CreatePollingReader(string[] itemIds, ReadConfig config, int intervalMs = 1000)
        {
            EnsureConnected();
            var reader = new PollingReader(_opcServer, itemIds, config, intervalMs);
            _activePollingReaders.Add(reader);
            return reader;
        }

        public OpcServerStatus GetServerStatus()
        {
            EnsureConnected();
            return new OpcServerStatus
            {
                StartTime = _opcServer.StartTime,
                CurrentTime = _opcServer.CurrentTime,
                LastUpdateTime = _opcServer.LastUpdateTime,
                State = (OpcServerState)_opcServer.ServerState,
                VendorInfo = _opcServer.VendorInfo
            };
        }

        /// <summary>
        /// 检查 OPC 服务器是否实际可访问（Connect 可能抛异常但连接已建立）
        /// </summary>
        private bool CheckServerAlive()
        {
            try
            {
                // 尝试访问服务器属性，能访问说明连接是通的
                var state = _opcServer.ServerState;
                return true;
            }
            catch { }

            try
            {
                var name = _opcServer.ServerName;
                return !string.IsNullOrEmpty(name);
            }
            catch { }

            try
            {
                _opcServer.CreateBrowser();
                return true;
            }
            catch { }

            return false;
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
                throw new InvalidOperationException("OPC 未连接");
        }

        private void Log(string msg)
        {
            ConnectLog?.Invoke(msg);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Disconnect();
                    if (_opcServer != null)
                    {
                        Marshal.FinalReleaseComObject(_opcServer);
                        _opcServer = null;
                    }
                }
                _disposed = true;
            }
        }
    }
}
