using System;
using System.Collections.Generic;

namespace OpcDaClient
{
    public enum ReadMode
    {
        Sync,
        Async
    }

    public enum OpcDataSource
    {
        Cache = 1,
        Device = 2
    }

    public class ReadConfig
    {
        public ReadMode Mode { get; set; } = ReadMode.Sync;
        public OpcDataSource DataSource { get; set; } = OpcDataSource.Cache;
        public int AsyncTimeoutMs { get; set; } = 5000;

        public static ReadConfig SyncCache { get { return new ReadConfig { Mode = ReadMode.Sync, DataSource = OpcDataSource.Cache }; } }
        public static ReadConfig SyncDevice { get { return new ReadConfig { Mode = ReadMode.Sync, DataSource = OpcDataSource.Device }; } }
        public static ReadConfig AsyncDevice { get { return new ReadConfig { Mode = ReadMode.Async, DataSource = OpcDataSource.Device }; } }
    }

    public interface IOpcDaClient : IDisposable
    {
        bool IsConnected { get; }

        void Connect(string serverProgId, string host = "localhost");
        void Disconnect();

        List<string> BrowseServer();
        List<OpcItem> BrowseItems(string branch = "");

        IPollingReader CreatePollingReader(string[] itemIds, ReadConfig config, int intervalMs = 1000);

        OpcServerStatus GetServerStatus();
    }

    public class OpcItem
    {
        public string ItemId { get; set; }
        public string Name { get; set; }
    }

    public class OpcServerStatus
    {
        public DateTime StartTime { get; set; }
        public DateTime CurrentTime { get; set; }
        public DateTime LastUpdateTime { get; set; }
        public OpcServerState State { get; set; }
        public string VendorInfo { get; set; }
    }

    public enum OpcServerState
    {
        Running = 1,
        Failed = 2,
        NoConfig = 3,
        Suspended = 4,
        Test = 5,
        CommFault = 6
    }

    public class OpcItemValue
    {
        public object Value { get; set; }
        public OpcQuality Quality { get; set; }
        public DateTime Timestamp { get; set; }

        public override string ToString()
        {
            return Value + " (" + Quality + ", " + Timestamp.ToString("HH:mm:ss.fff") + ")";
        }
    }

    public enum OpcQuality
    {
        Bad = 0,
        Uncertain = 64,
        Good = 192,
        GoodLocalOverride = 216
    }
}
