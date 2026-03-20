using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace OpcDaClient
{
    class Program
    {
        private static string _logFile;
        private static readonly object _logLock = new object();
        private const long MAX_LOG_SIZE = 50 * 1024; // 50KB

        static void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + " " + msg;
            Console.WriteLine(line);
            if (_logFile != null)
            {
                lock (_logLock)
                {
                    try
                    {
                        // 超过 50KB 轮转：当前 → .bak，重新开始
                        var fi = new FileInfo(_logFile);
                        if (fi.Exists && fi.Length > MAX_LOG_SIZE)
                        {
                            string bakFile = _logFile + ".bak";
                            if (File.Exists(bakFile)) File.Delete(bakFile);
                            File.Move(_logFile, bakFile);
                        }
                        File.AppendAllText(_logFile, line + "\r\n", Encoding.UTF8);
                    }
                    catch { }
                }
            }
        }

        #region COM 安全初始化（关键：解决远程 DCOM 连接 RPC 不可用问题）

        [DllImport("ole32.dll")]
        private static extern int CoInitializeSecurity(
            IntPtr pVoid, int cAuthSvc, IntPtr asAuthSvc,
            IntPtr pReserved1, int dwAuthnLevel, int dwImpLevel,
            IntPtr pAuthList, int dwCapabilities, IntPtr pReserved3);

        private const int RPC_C_AUTHN_LEVEL_NONE = 1;
        private const int RPC_C_IMP_LEVEL_IMPERSONATE = 3;

        private static void InitComSecurity()
        {
            int hr = CoInitializeSecurity(
                IntPtr.Zero, -1, IntPtr.Zero, IntPtr.Zero,
                RPC_C_AUTHN_LEVEL_NONE,
                RPC_C_IMP_LEVEL_IMPERSONATE,
                IntPtr.Zero, 0, IntPtr.Zero);

            // 0 = S_OK, 0x80010119 = RPC_E_TOO_LATE（已初始化过，忽略）
            if (hr != 0 && hr != unchecked((int)0x80010119))
            {
                Console.WriteLine("[警告] CoInitializeSecurity 返回: 0x" + hr.ToString("X8"));
            }
        }

        #endregion

        [STAThread]
        static void Main(string[] args)
        {
            InitComSecurity();

            // 日志同时输出到控制台和文件
            _logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "opc_log.txt");
            try { File.WriteAllText(_logFile, ""); } catch { _logFile = null; }

            Log("OPC DA → EMS 数据转发");
            Log("======================");
            if (_logFile != null) Log("日志文件: " + _logFile);

            string configFile = ConfigLoader.DefaultConfigFile;
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i].ToLower() == "-config" || args[i].ToLower() == "/config") && i + 1 < args.Length)
                    configFile = args[++i];
            }
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configFile);

            if (!File.Exists(configPath))
            {
                Log("未找到配置文件，生成默认配置: " + configFile);
                ConfigLoader.CreateDefaultConfig(configPath);
                Log("请编辑 " + configFile + " 填写 OPC 服务器信息后重新运行");
                Console.WriteLine("\n按任意键退出...");
                Console.ReadKey();
                return;
            }

            ForwarderConfig config;
            try
            {
                config = ConfigLoader.Load(configPath);
            }
            catch (Exception ex)
            {
                Log("配置加载失败: " + ex.Message);
                Console.WriteLine("\n按任意键退出...");
                Console.ReadKey();
                return;
            }

            Log("OPC: " + config.ServerProgId + "@" + config.Host);
            Log("轮询: " + config.PollingIntervalMs + "ms");
            if (config.Points.Count > 0)
                Log("点位: 配置文件指定 " + config.Points.Count + " 个");
            else
                Log("点位: 自动发现（浏览 OPC 服务器全部点位）");

            using (var forwarder = new DataForwarder(config))
            {
                forwarder.Log += (s, e) => Log(e.Message);

                try
                {
                    forwarder.Start();

                    Console.WriteLine("\n按 Enter 停止...\n");

                    while (forwarder.IsRunning)
                    {
                        if (Console.KeyAvailable)
                        {
                            Console.ReadLine();
                            break;
                        }
                        Thread.Sleep(100);
                    }

                    forwarder.Stop();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("\n错误: " + ex.Message);
                }
            }

            Console.WriteLine("\n按任意键退出...");
            Console.ReadKey();
        }
    }
}
