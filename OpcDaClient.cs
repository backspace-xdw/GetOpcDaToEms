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

        #region DCOM 安全设置

        [DllImport("ole32.dll")]
        private static extern int CoSetProxyBlanket(
            [MarshalAs(UnmanagedType.IUnknown)] object pProxy,
            uint dwAuthnSvc,
            uint dwAuthzSvc,
            IntPtr pServerPrincName,
            uint dwAuthnLevel,
            uint dwImpLevel,
            IntPtr pAuthInfo,
            uint dwCapabilities);

        private const uint RPC_C_AUTHN_WINNT = 10;
        private const uint RPC_C_AUTHZ_NONE = 0;
        private const uint RPC_C_AUTHN_LEVEL_NONE = 1;
        private const uint RPC_C_AUTHN_LEVEL_CONNECT = 2;
        private const uint RPC_C_IMP_LEVEL_IMPERSONATE = 3;
        private const uint EOAC_NONE = 0;

        /// <summary>
        /// 对 COM 对象设置 DCOM 代理安全（解决远程连接 RPC 不可用）
        /// </summary>
        private static void SetProxySecurity(object comObject)
        {
            // 先尝试 AUTHN_LEVEL_NONE（最宽松）
            int hr = CoSetProxyBlanket(
                comObject,
                RPC_C_AUTHN_WINNT,
                RPC_C_AUTHZ_NONE,
                IntPtr.Zero,
                RPC_C_AUTHN_LEVEL_NONE,
                RPC_C_IMP_LEVEL_IMPERSONATE,
                IntPtr.Zero,
                EOAC_NONE);

            if (hr != 0)
            {
                // 回退到 AUTHN_LEVEL_CONNECT
                CoSetProxyBlanket(
                    comObject,
                    RPC_C_AUTHN_WINNT,
                    RPC_C_AUTHZ_NONE,
                    IntPtr.Zero,
                    RPC_C_AUTHN_LEVEL_CONNECT,
                    RPC_C_IMP_LEVEL_IMPERSONATE,
                    IntPtr.Zero,
                    EOAC_NONE);
            }
        }

        #endregion

        public OpcDaClient()
        {
            _activePollingReaders = new List<PollingReader>();
        }

        public void Connect(string serverProgId, string host = "localhost")
        {
            Connect(serverProgId, host, 3, 3000);
        }

        public void Connect(string serverProgId, string host, int retryCount, int retryDelayMs)
        {
            ServerProgId = serverProgId;
            Host = host;

            Exception lastEx = null;

            for (int attempt = 1; attempt <= retryCount; attempt++)
            {
                try
                {
                    // 每次重试都完全释放旧对象
                    if (_opcServer != null)
                    {
                        try { _opcServer.Disconnect(); } catch { }
                        try { Marshal.FinalReleaseComObject(_opcServer); } catch { }
                        _opcServer = null;
                    }

                    // 创建新的 COM 对象
                    _opcServer = new OPCServer();

                    // 关键：设置 DCOM 代理安全（必须在 Connect 之前）
                    SetProxySecurity(_opcServer);

                    // 连接
                    _opcServer.Connect(serverProgId, host);
                    IsConnected = true;
                    return;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (attempt < retryCount)
                    {
                        Thread.Sleep(retryDelayMs);
                    }
                }
            }

            throw new Exception("连接 OPC 服务器失败 (重试 " + retryCount + " 次): " + lastEx.Message, lastEx);
        }

        public void Reconnect(int retryCount = 3, int retryDelayMs = 3000)
        {
            IsConnected = false;

            // 清理旧连接
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
            {
                branches.Add(branch);
            }
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

        private void EnsureConnected()
        {
            if (!IsConnected)
                throw new InvalidOperationException("OPC 未连接");
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
