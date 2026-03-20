using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OpcDaClient
{
    /// <summary>
    /// 远程 OPC 服务器信息
    /// </summary>
    public class OpcServerInfo
    {
        public Guid Clsid { get; set; }
        public string ProgId { get; set; }
        public string Description { get; set; }

        public override string ToString()
        {
            return ProgId + " - " + Description;
        }
    }
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

        #region OpcEnum 接口（枚举远程 OPC 服务器）

        // IOPCServerList IID
        [ComImport, Guid("13486D50-4821-11D2-A494-3CB306C10000")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IOPCServerList
        {
            void EnumClassesOfCategories(
                uint cImplemented,
                [MarshalAs(UnmanagedType.LPArray)] Guid[] rgcatidImpl,
                uint cRequired,
                [MarshalAs(UnmanagedType.LPArray)] Guid[] rgcatidReq,
                [MarshalAs(UnmanagedType.Interface)] out object ppEnumClsid);

            void GetClassDetails(
                ref Guid clsid,
                [MarshalAs(UnmanagedType.LPWStr)] out string ppszProgID,
                [MarshalAs(UnmanagedType.LPWStr)] out string ppszUserType);

            void CLSIDFromProgID(
                [MarshalAs(UnmanagedType.LPWStr)] string szProgId,
                out Guid clsid);
        }

        // IEnumGUID IID
        [ComImport, Guid("0002E000-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumGUID
        {
            int Next(uint celt,
                [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] Guid[] rgelt,
                out uint pceltFetched);
            void Skip(uint celt);
            void Reset();
            void Clone([MarshalAs(UnmanagedType.Interface)] out IEnumGUID ppEnum);
        }

        // OPC DA 分类 GUID
        private static readonly Guid CATID_OPCDAServer10 = new Guid("63D5F430-CFE4-11D1-B2C8-0060083BA1FB");
        private static readonly Guid CATID_OPCDAServer20 = new Guid("63D5F432-CFE4-11D1-B2C8-0060083BA1FB");
        private static readonly Guid CATID_OPCDAServer30 = new Guid("CC603642-66D7-48F1-B69A-B625E73652D7");

        // OpcEnum CLSID
        private static readonly Guid CLSID_OpcEnum = new Guid("13486D51-4821-11D2-A494-3CB306C10000");

        #endregion

        /// <summary>
        /// 枚举远程机器上所有 OPC DA 服务器
        /// 等同于标准 OPC 测试工具的"浏览服务器"功能
        /// </summary>
        public static List<OpcServerInfo> EnumRemoteServers(string host, Action<string> log = null)
        {
            var result = new List<OpcServerInfo>();

            log?.Invoke("[枚举] 连接 OpcEnum@" + host + "...");

            // 通过 DCOM 创建远程 OpcEnum 实例
            object opcEnumObj;
            try
            {
                opcEnumObj = CreateRemoteInstanceByClsid(CLSID_OpcEnum, host);
            }
            catch (Exception ex)
            {
                log?.Invoke("[枚举] 连接 OpcEnum 失败: " + ex.Message);
                return result;
            }

            try
            {
                var serverList = (IOPCServerList)opcEnumObj;

                // 枚举 OPC DA 1.0/2.0/3.0 所有类别
                Guid[] categories = new Guid[] { CATID_OPCDAServer10, CATID_OPCDAServer20, CATID_OPCDAServer30 };
                var foundClsids = new HashSet<string>();

                foreach (var catId in categories)
                {
                    try
                    {
                        object enumObj;
                        serverList.EnumClassesOfCategories(
                            1, new Guid[] { catId },
                            0, null,
                            out enumObj);

                        if (enumObj == null) continue;
                        var enumGuid = (IEnumGUID)enumObj;

                        Guid[] buffer = new Guid[1];
                        uint fetched;

                        while (true)
                        {
                            int hr = enumGuid.Next(1, buffer, out fetched);
                            if (hr != 0 || fetched == 0) break;

                            string clsidStr = buffer[0].ToString();
                            if (foundClsids.Contains(clsidStr)) continue;
                            foundClsids.Add(clsidStr);

                            try
                            {
                                Guid clsid = buffer[0];
                                string progId, description;
                                serverList.GetClassDetails(ref clsid, out progId, out description);

                                result.Add(new OpcServerInfo
                                {
                                    Clsid = clsid,
                                    ProgId = progId ?? "",
                                    Description = description ?? ""
                                });
                            }
                            catch { }
                        }

                        Marshal.ReleaseComObject(enumObj);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("[枚举] 枚举过程出错: " + ex.Message);
            }
            finally
            {
                Marshal.FinalReleaseComObject(opcEnumObj);
            }

            log?.Invoke("[枚举] 发现 " + result.Count + " 个 OPC DA 服务器");
            return result;
        }

        /// <summary>
        /// 将远程 OPC 服务器的 ProgID → CLSID 注册到本地注册表
        /// 解决 OPCAutomation.Connect 内部 CLSIDFromProgID 查不到的问题
        /// </summary>
        public static bool RegisterProgIdLocally(string progId, Guid clsid, Action<string> log = null)
        {
            try
            {
                // 写入 HKCU\Software\Classes（不需要管理员权限）
                string keyPath = "Software\\Classes\\" + progId + "\\CLSID";
                string clsidStr = "{" + clsid.ToString().ToUpper() + "}";

                Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath);
                if (key != null)
                {
                    key.SetValue("", clsidStr);
                    key.Close();
                    log?.Invoke("[注册] 本地注册 " + progId + " → " + clsidStr);
                    return true;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("[注册] 写注册表失败: " + ex.Message);
            }
            return false;
        }

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
