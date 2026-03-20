using System;
using System.Runtime.InteropServices;

namespace OpcDaClient
{
    /// <summary>
    /// 标准 DCOM 连接助手
    /// 使用 CoCreateInstanceEx + COSERVERINFO + COAUTHINFO
    /// 与标准 OPC 测试工具（OPCClient.exe 等）完全一致的连接方式
    /// </summary>
    public static class DcomHelper
    {
        #region COM P/Invoke 定义

        [StructLayout(LayoutKind.Sequential)]
        private struct COAUTHINFO
        {
            public uint dwAuthnSvc;
            public uint dwAuthzSvc;
            public IntPtr pwszServerPrincName;
            public uint dwAuthnLevel;
            public uint dwImpersonationLevel;
            public IntPtr pAuthIdentityData;
            public uint dwCapabilities;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COSERVERINFO
        {
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pwszName;
            public IntPtr pAuthInfo;
            public uint dwReserved2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MULTI_QI
        {
            public IntPtr pIID;
            [MarshalAs(UnmanagedType.IUnknown)]
            public object pItf;
            public int hr;
        }

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstanceEx(
            ref Guid rclsid,
            [MarshalAs(UnmanagedType.IUnknown)] object pUnkOuter,
            uint dwClsCtx,
            IntPtr pServerInfo,
            uint cmq,
            [In, Out] MULTI_QI[] pResults);

        [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
        private static extern int CLSIDFromProgID(string lpszProgID, out Guid lpclsid);

        // IUnknown IID
        private static readonly Guid IID_IUnknown = new Guid("00000000-0000-0000-C000-000000000046");

        private const uint CLSCTX_REMOTE_SERVER = 0x10;
        private const uint CLSCTX_LOCAL_SERVER = 0x4;
        private const uint CLSCTX_ALL = 0x17;

        // 认证常量
        private const uint RPC_C_AUTHN_WINNT = 10;
        private const uint RPC_C_AUTHN_DEFAULT = 0xFFFFFFFF;
        private const uint RPC_C_AUTHZ_NONE = 0;
        private const uint RPC_C_AUTHN_LEVEL_NONE = 1;
        private const uint RPC_C_AUTHN_LEVEL_CONNECT = 2;
        private const uint RPC_C_IMP_LEVEL_IMPERSONATE = 3;
        private const uint EOAC_NONE = 0;

        #endregion

        /// <summary>
        /// 使用标准 DCOM 方式创建远程 COM 实例
        /// 等同于标准 OPC 测试工具的连接方式：
        /// CLSIDFromProgID → CoCreateInstanceEx(COSERVERINFO + COAUTHINFO)
        /// </summary>
        public static object CreateRemoteInstance(string progId, string host)
        {
            // 1. ProgID → CLSID
            Guid clsid;
            int hr = CLSIDFromProgID(progId, out clsid);
            if (hr != 0)
                Marshal.ThrowExceptionForHR(hr);

            return CreateRemoteInstanceByClsid(clsid, host);
        }

        /// <summary>
        /// 通过 CLSID 创建远程 COM 实例
        /// </summary>
        public static object CreateRemoteInstanceByClsid(Guid clsid, string host)
        {
            // 构建 COAUTHINFO（认证设置 = 标准 OPC 客户端设置）
            var authInfo = new COAUTHINFO
            {
                dwAuthnSvc = RPC_C_AUTHN_WINNT,
                dwAuthzSvc = RPC_C_AUTHZ_NONE,
                pwszServerPrincName = IntPtr.Zero,
                dwAuthnLevel = RPC_C_AUTHN_LEVEL_CONNECT,
                dwImpersonationLevel = RPC_C_IMP_LEVEL_IMPERSONATE,
                pAuthIdentityData = IntPtr.Zero,
                dwCapabilities = EOAC_NONE
            };

            // 构建 COSERVERINFO（服务器信息）
            IntPtr pAuthInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(COAUTHINFO)));
            Marshal.StructureToPtr(authInfo, pAuthInfo, false);

            var serverInfo = new COSERVERINFO
            {
                dwReserved1 = 0,
                pwszName = host,
                pAuthInfo = pAuthInfo,
                dwReserved2 = 0
            };

            IntPtr pServerInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(COSERVERINFO)));
            Marshal.StructureToPtr(serverInfo, pServerInfo, false);

            // 构建 MULTI_QI（请求 IUnknown 接口）
            Guid iidUnknown = IID_IUnknown;
            IntPtr pIID = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(Guid)));
            Marshal.StructureToPtr(iidUnknown, pIID, false);

            var multiQi = new MULTI_QI[]
            {
                new MULTI_QI { pIID = pIID, pItf = null, hr = 0 }
            };

            try
            {
                // CoCreateInstanceEx — 与标准 OPC 测试工具完全一致
                int hr = CoCreateInstanceEx(
                    ref clsid,
                    null,
                    CLSCTX_REMOTE_SERVER,
                    pServerInfo,
                    1,
                    multiQi);

                if (hr != 0)
                    Marshal.ThrowExceptionForHR(hr);
                if (multiQi[0].hr != 0)
                    Marshal.ThrowExceptionForHR(multiQi[0].hr);

                return multiQi[0].pItf;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pIID);
                Marshal.FreeCoTaskMem(pServerInfo);
                Marshal.FreeCoTaskMem(pAuthInfo);
            }
        }

        /// <summary>
        /// 标准 DCOM 预热：使用 CoCreateInstanceEx 建立 DCOM 通道
        /// 与标准 OPC 客户端测试工具完全一致的流程
        /// </summary>
        public static bool WarmUp(string progId, string host, int retryCount, int retryDelayMs,
            Action<string> log = null)
        {
            log?.Invoke("[DCOM] CLSIDFromProgID: " + progId);

            Guid clsid;
            int hr = CLSIDFromProgID(progId, out clsid);
            if (hr != 0)
            {
                log?.Invoke("[DCOM] CLSIDFromProgID 失败: 0x" + hr.ToString("X8"));
                log?.Invoke("[DCOM] 尝试通过远程注册表查找...");

                // 回退：通过 Type.GetTypeFromProgID 查找远程 CLSID
                try
                {
                    Type t = Type.GetTypeFromProgID(progId, host, true);
                    clsid = t.GUID;
                    log?.Invoke("[DCOM] 远程 CLSID: " + clsid);
                }
                catch (Exception ex)
                {
                    log?.Invoke("[DCOM] 远程查找也失败: " + ex.Message);
                    return false;
                }
            }
            else
            {
                log?.Invoke("[DCOM] CLSID: " + clsid);
            }

            // 使用 CoCreateInstanceEx 建立 DCOM 通道（带重试）
            for (int i = 0; i < retryCount; i++)
            {
                try
                {
                    log?.Invoke("[DCOM] CoCreateInstanceEx 第 " + (i + 1) + " 次...");
                    object instance = CreateRemoteInstanceByClsid(clsid, host);

                    if (instance != null)
                    {
                        log?.Invoke("[DCOM] 远程实例创建成功，DCOM 通道已建立");
                        Marshal.FinalReleaseComObject(instance);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke("[DCOM] 第 " + (i + 1) + " 次失败: " + ex.Message);
                }

                if (i < retryCount - 1)
                    System.Threading.Thread.Sleep(retryDelayMs);
            }

            return false;
        }
    }
}
