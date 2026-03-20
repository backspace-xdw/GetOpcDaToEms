using System;
using System.Collections.Generic;
using System.IO;

namespace OpcDaClient
{
    public enum EmsDataType
    {
        Ax,
        Dx,
        Cx
    }

    public class PointMapping
    {
        public string OpcItemId { get; set; }
        public string EmsTagName { get; set; }
        public EmsDataType DataType { get; set; }
    }

    public class ForwarderConfig
    {
        public string ServerProgId { get; set; } = "Hollysys.HOLLiASiComm.1";
        public string Host { get; set; } = "192.168.1.100";

        public int PollingIntervalMs { get; set; } = 1000;
        public ReadMode ReadMode { get; set; } = ReadMode.Sync;
        public OpcDataSource DataSource { get; set; } = OpcDataSource.Cache;
        public int AsyncTimeoutMs { get; set; } = 5000;

        // 连接重试
        public int RetryCount { get; set; } = 5;
        public int RetryDelayMs { get; set; } = 3000;

        public List<PointMapping> Points { get; set; } = new List<PointMapping>();

        public string[] GetOpcItemIds()
        {
            var ids = new string[Points.Count];
            for (int i = 0; i < Points.Count; i++)
                ids[i] = Points[i].OpcItemId;
            return ids;
        }

        public ReadConfig GetReadConfig()
        {
            return new ReadConfig
            {
                Mode = ReadMode,
                DataSource = DataSource,
                AsyncTimeoutMs = AsyncTimeoutMs
            };
        }
    }

    public static class ConfigLoader
    {
        public const string DefaultConfigFile = "OpcEmsConfig.ini";

        public static ForwarderConfig Load(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("配置文件不存在: " + filePath);

            var config = new ForwarderConfig();
            string currentSection = "";
            var lines = File.ReadAllLines(filePath);
            int lineNum = 0;

            foreach (var rawLine in lines)
            {
                lineNum++;
                var line = rawLine.Trim();

                if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";"))
                    continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line.Substring(1, line.Length - 2).Trim().ToLower();
                    continue;
                }

                // 兼容无段标记的旧格式
                if (string.IsNullOrEmpty(currentSection) && line.Contains("="))
                {
                    ParseKV(config, line);
                    continue;
                }

                switch (currentSection)
                {
                    case "server":
                        ParseKV(config, line);
                        break;
                    case "polling":
                        ParseKV(config, line);
                        break;
                    case "points":
                        var point = ParsePointLine(line, lineNum);
                        if (point != null)
                            config.Points.Add(point);
                        break;
                }
            }

            return config;
        }

        private static void ParseKV(ForwarderConfig config, string line)
        {
            var parts = line.Split(new[] { '=' }, 2);
            if (parts.Length != 2) return;

            var key = parts[0].Trim().ToLower();
            var value = parts[1].Trim();

            switch (key)
            {
                case "progid":
                    config.ServerProgId = value;
                    break;
                case "host":
                case "hostname":
                    config.Host = value;
                    break;
                case "intervalms":
                    int interval;
                    if (int.TryParse(value, out interval))
                        config.PollingIntervalMs = interval;
                    break;
                case "readmode":
                    switch (value.ToLower())
                    {
                        case "synccache":
                            config.ReadMode = ReadMode.Sync;
                            config.DataSource = OpcDataSource.Cache;
                            break;
                        case "syncdevice":
                            config.ReadMode = ReadMode.Sync;
                            config.DataSource = OpcDataSource.Device;
                            break;
                        case "async":
                        case "asyncdevice":
                            config.ReadMode = ReadMode.Async;
                            config.DataSource = OpcDataSource.Device;
                            break;
                        case "asynccache":
                            config.ReadMode = ReadMode.Async;
                            config.DataSource = OpcDataSource.Cache;
                            break;
                        default:
                            config.ReadMode = ReadMode.Sync;
                            config.DataSource = OpcDataSource.Cache;
                            break;
                    }
                    break;
                case "datasource":
                    config.DataSource = value.ToLower() == "device"
                        ? OpcDataSource.Device : OpcDataSource.Cache;
                    break;
                case "asynctimeoutms":
                    int at;
                    if (int.TryParse(value, out at))
                        config.AsyncTimeoutMs = at;
                    break;
                case "retrycount":
                    int rc;
                    if (int.TryParse(value, out rc))
                        config.RetryCount = rc;
                    break;
                case "retrydelayms":
                    int rd;
                    if (int.TryParse(value, out rd))
                        config.RetryDelayMs = rd;
                    break;
            }
        }

        private static PointMapping ParsePointLine(string line, int lineNum)
        {
            // 支持逗号或竖线分隔
            char separator = line.Contains(",") ? ',' : '|';
            var parts = line.Split(separator);
            if (parts.Length < 3) return null;

            var dataTypeStr = parts[2].Trim().ToLower();
            EmsDataType dataType;
            switch (dataTypeStr)
            {
                case "dx": dataType = EmsDataType.Dx; break;
                case "cx": dataType = EmsDataType.Cx; break;
                default: dataType = EmsDataType.Ax; break;
            }

            return new PointMapping
            {
                OpcItemId = parts[0].Trim(),
                EmsTagName = parts[1].Trim(),
                DataType = dataType
            };
        }

        /// <summary>
        /// 生成默认配置文件
        /// </summary>
        public static void CreateDefaultConfig(string filePath)
        {
            File.WriteAllText(filePath,
@"# OPC → EMS 数据转发配置
# 只需配置服务器信息，点位自动发现

[Server]
ProgId=Hollysys.HOLLiASiComm.1
HostName=192.168.1.100

[Polling]
IntervalMs=1000
# 读取模式:
#   SyncCache   = 同步读缓存（最快）
#   SyncDevice  = 同步读设备（保证最新）
#   AsyncDevice = 异步读设备（非阻塞）
#   AsyncCache  = 异步读缓存
ReadMode=SyncCache
# 异步模式超时（毫秒）
AsyncTimeoutMs=5000
# 连接失败重试
RetryCount=5
RetryDelayMs=3000

# [Points] 留空 = 自动浏览OPC服务器所有点位
# 如需指定部分点位: OPC点位ID, EMS点名, 类型(Ax/Dx/Cx)
[Points]
");
        }
    }
}
