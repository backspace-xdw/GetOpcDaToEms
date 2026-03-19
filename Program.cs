using System;
using System.IO;
using System.Threading;

namespace OpcDaClient
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("OPC DA → EMS 数据转发");
            Console.WriteLine("======================\n");

            // 配置文件路径
            string configFile = ConfigLoader.DefaultConfigFile;
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i].ToLower() == "-config" || args[i].ToLower() == "/config") && i + 1 < args.Length)
                    configFile = args[++i];
            }
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configFile);

            // 没有配置文件 → 生成默认配置
            if (!File.Exists(configPath))
            {
                Console.WriteLine("未找到配置文件，生成默认配置: " + configFile);
                ConfigLoader.CreateDefaultConfig(configPath);
                Console.WriteLine("请编辑 " + configFile + " 填写 OPC 服务器信息后重新运行");
                Console.WriteLine("\n按任意键退出...");
                Console.ReadKey();
                return;
            }

            // 加载配置
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

            // 启动转发
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
