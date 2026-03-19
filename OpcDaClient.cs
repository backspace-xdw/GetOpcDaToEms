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

        /// <summary>连接的 ProgID（重连用）</summary>
        public string ServerProgId { get; private set; }

        /// <summary>连接的主机（重连用）</summary>
        public string Host { get; private set; }

        public OpcDaClient()
        {
            _activePollingReaders = new List<PollingReader>();
        }

        public void Connect(string serverProgId, string host = "localhost")
        {
            Connect(serverProgId, host, 3, 3000);
        }

        /// <summary>
        /// 连接 OPC 服务器（带重试）
        /// </summary>
        public void Connect(string serverProgId, string host, int retryCount, int retryDelayMs)
        {
            ServerProgId = serverProgId;
            Host = host;

            Exception lastEx = null;

            for (int attempt = 1; attempt <= retryCount; attempt++)
            {
                try
                {
                    if (_opcServer != null)
                    {
                        try { Marshal.ReleaseComObject(_opcServer); } catch { }
                    }

                    _opcServer = new OPCServer();
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

        /// <summary>
        /// 重新连接（断线重连用）
        /// </summary>
        public void Reconnect(int retryCount = 3, int retryDelayMs = 3000)
        {
            try { Disconnect(); } catch { }
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
                        Marshal.ReleaseComObject(_opcServer);
                        _opcServer = null;
                    }
                }
                _disposed = true;
            }
        }
    }
}
