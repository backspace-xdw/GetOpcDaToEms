using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpcDaClient
{
    class Program
    {
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
            // 必须在创建任何 COM 对象之前调用
            InitComSecurity();

            Console.WriteLine("OPC DA → EMS 数据转发");
            Console.WriteLine("======================\n");

            string configFile = ConfigLoader.DefaultConfigFile;
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i].ToLower() == "-config" || args[i].ToLower() == "/config") && i + 1 < args.Length)
                    configFile = args[++i];
            }
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configFile);

            if (!File.Exists(configPath))
            {
                Console.WriteLine("未找到配置文件，生成默认配置: " + configFile);
                ConfigLoader.CreateDefaultConfig(configPath);
                Console.WriteLine("请编辑 " + configFile + " 填写 OPC 服务器信息后重新运行");
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
                Console.WriteLine("配置加载失败: " + ex.Message);
                Console.WriteLine("\n按任意键退出...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine("OPC: " + config.ServerProgId + "@" + config.Host);
            Console.WriteLine("轮询: " + config.PollingIntervalMs + "ms");
            if (config.Points.Count > 0)
                Console.WriteLine("点位: 配置文件指定 " + config.Points.Count + " 个");
            else
                Console.WriteLine("点位: 自动发现（浏览 OPC 服务器全部点位）");
            Console.WriteLine();

            using (var forwarder = new DataForwarder(config))
            {
                forwarder.Log += (s, e) =>
                {
                    Console.WriteLine(e.Time.ToString("HH:mm:ss") + " " + e.Message);
                };

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
