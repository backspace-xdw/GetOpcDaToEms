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

            bool isRemote = !string.IsNullOrEmpty(host) &&
                            host != "localhost" && host != "127.0.0.1";

            if (isRemote)
            {
                Log("远程连接: " + host + " - 预热 DCOM 通道...");
                WarmUpDcom(host, retryCount, retryDelayMs);
            }

            // 连接目标 OPC 服务器
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
                    _opcServer.Connect(serverProgId, host);

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
        /// 多策略预热远程 DCOM 通道
        /// </summary>
        private void WarmUpDcom(string host, int retryCount, int retryDelayMs)
        {
            // 策略1: 通过 OpcEnum ProgID
            Log("[预热] 策略1: OPC.ServerList...");
            if (TryWarmUp(() =>
            {
                Type t = Type.GetTypeFromProgID("OPC.ServerList.1", host, false);
                if (t == null) t = Type.GetTypeFromProgID("OPC.ServerList", host, false);
                if (t != null)
                {
                    object obj = Activator.CreateInstance(t);
                    SetProxySecurity(obj);
                    Marshal.FinalReleaseComObject(obj);
                    return true;
                }
                return false;
            }, retryCount, retryDelayMs))
            {
                Log("[预热] OpcEnum 通道已建立");
                return;
            }

            // 策略2: 通过 OpcEnum CLSID (不依赖本地注册)
            Log("[预热] 策略2: OpcEnum CLSID...");
            if (TryWarmUp(() =>
            {
                Guid opcEnumClsid = new Guid("13486D51-4821-11D2-A494-3CB306C10000");
                Type t = Type.GetTypeFromCLSID(opcEnumClsid, host, false);
                if (t != null)
                {
                    object obj = Activator.CreateInstance(t);
                    SetProxySecurity(obj);
                    Marshal.FinalReleaseComObject(obj);
                    return true;
                }
                return false;
            }, retryCount, retryDelayMs))
            {
                Log("[预热] OpcEnum CLSID 通道已建立");
                return;
            }

            // 策略3: 直接尝试创建目标 OPC 服务器的远程实例
            Log("[预热] 策略3: 直接创建远程 " + ServerProgId + "...");
            if (TryWarmUp(() =>
            {
                Type t = Type.GetTypeFromProgID(ServerProgId, host, false);
                if (t != null)
                {
                    object obj = Activator.CreateInstance(t);
                    SetProxySecurity(obj);
                    Marshal.FinalReleaseComObject(obj);
                    return true;
                }
                return false;
            }, retryCount, retryDelayMs))
            {
                Log("[预热] 远程实例通道已建立");
                return;
            }

            Log("[预热] 所有策略均未成功，继续尝试直接连接...");
        }

        private bool TryWarmUp(Func<bool> action, int retryCount, int retryDelayMs)
        {
            for (int i = 0; i < retryCount; i++)
            {
                try
                {
                    if (action())
                        return true;
                    Log("[预热]   Type 为 null，跳过");
                    return false; // Type 为 null 说明不支持此策略
                }
                catch (Exception ex)
                {
                    Log("[预热]   第 " + (i + 1) + " 次: " + ex.Message);
                }

                if (i < retryCount - 1)
                    Thread.Sleep(retryDelayMs);
            }
            return false;
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
