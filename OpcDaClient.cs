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

            // 第一步：通过 OpcEnum 预热远程 DCOM 通道
            // 通用 OPC 客户端都这么做，这一步建立 DCOM 连接
            WarmUpDcom(host, retryCount, retryDelayMs);

            // 第二步：连接目标 OPC 服务器
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

                    _opcServer = new OPCServer();
                    SetProxySecurity(_opcServer);
                    _opcServer.Connect(serverProgId, host);
                    IsConnected = true;
                    return;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (attempt < retryCount)
                        Thread.Sleep(retryDelayMs);
                }
            }

            throw new Exception("连接 OPC 服务器失败 (重试 " + retryCount + " 次): " + lastEx.Message, lastEx);
        }

        /// <summary>
        /// 通过 OpcEnum 预热远程机器的 DCOM 通道
        /// 通用 OPC 客户端在连接前都先做这一步
        /// 失败不抛异常——预热是尽力而为
        /// </summary>
        private void WarmUpDcom(string host, int retryCount, int retryDelayMs)
        {
            if (host == "localhost" || host == "127.0.0.1" || host == "")
                return;

            // 尝试通过 OpcEnum（OPC.ServerList）建立 DCOM 通道
            for (int i = 0; i < retryCount; i++)
            {
                try
                {
                    Type enumType = Type.GetTypeFromProgID("OPC.ServerList.1", host, false);
                    if (enumType == null)
                        enumType = Type.GetTypeFromProgID("OPC.ServerList", host, false);

                    if (enumType != null)
                    {
                        object enumObj = Activator.CreateInstance(enumType);
                        if (enumObj != null)
                        {
                            SetProxySecurity(enumObj);
                            Marshal.FinalReleaseComObject(enumObj);
                        }
                        return; // DCOM 通道已建立
                    }
                }
                catch
                {
                    // RPC 不可用——等待后重试，DCOM 服务可能还在启动
                }

                if (i < retryCount - 1)
                    Thread.Sleep(retryDelayMs);
            }

            // 预热失败也继续，后面 Connect 自己还会重试
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
