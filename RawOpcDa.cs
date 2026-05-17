using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OpcDaClient
{
    public enum ReadMode { Sync, Async }

    public enum OpcDataSource { Cache = 1, Device = 2 }

    public class ReadConfig
    {
        public ReadMode Mode { get; set; } = ReadMode.Sync;
        public OpcDataSource DataSource { get; set; } = OpcDataSource.Cache;
        public int AsyncTimeoutMs { get; set; } = 5000;
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

    /// <summary>
    /// 原生 OPC DA COM 接口定义（不依赖 OPCAutomation）
    /// </summary>

    // IOPCServer {39c13a4d-011e-11d0-9675-0020afd8adb3}
    [ComImport, Guid("39c13a4d-011e-11d0-9675-0020afd8adb3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOPCServer
    {
        void AddGroup(
            [MarshalAs(UnmanagedType.LPWStr)] string szName,
            [MarshalAs(UnmanagedType.Bool)] bool bActive,
            int dwRequestedUpdateRate,
            int hClientGroup,
            IntPtr pTimeBias,
            IntPtr pPercentDeadband,
            int dwLCID,
            out int phServerGroup,
            out int pRevisedUpdateRate,
            ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppUnk);

        void GetErrorString(
            int dwError, int dwLocale,
            [MarshalAs(UnmanagedType.LPWStr)] out string ppString);

        void GetGroupByName(
            [MarshalAs(UnmanagedType.LPWStr)] string szName,
            ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppUnk);

        void GetStatus(out IntPtr ppServerStatus);

        void RemoveGroup(int hServerGroup, [MarshalAs(UnmanagedType.Bool)] bool bForce);

        void CreateGroupEnumerator(
            int dwScope, ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppUnk);
    }

    // IOPCGroupStateMgt {39c13a50-011e-11d0-9675-0020afd8adb3}
    [ComImport, Guid("39c13a50-011e-11d0-9675-0020afd8adb3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOPCGroupStateMgt
    {
        void GetState(
            out int pUpdateRate,
            [MarshalAs(UnmanagedType.Bool)] out bool pActive,
            [MarshalAs(UnmanagedType.LPWStr)] out string ppName,
            out int pTimeBias,
            out float pPercentDeadband,
            out int pLCID,
            out int phClientGroup,
            out int phServerGroup);

        void SetState(
            IntPtr pRequestedUpdateRate,
            out int pRevisedUpdateRate,
            IntPtr pActive,
            IntPtr pTimeBias,
            IntPtr pPercentDeadband,
            IntPtr pLCID,
            IntPtr phClientGroup);

        void SetName([MarshalAs(UnmanagedType.LPWStr)] string szName);

        void CloneGroup(
            [MarshalAs(UnmanagedType.LPWStr)] string szName,
            ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppUnk);
    }

    // IOPCItemMgt {39c13a54-011e-11d0-9675-0020afd8adb3}
    [ComImport, Guid("39c13a54-011e-11d0-9675-0020afd8adb3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOPCItemMgt
    {
        void AddItems(
            int dwCount,
            IntPtr pItemArray,
            out IntPtr ppAddResults,
            out IntPtr ppErrors);

        void ValidateItems(
            int dwCount, IntPtr pItemArray,
            [MarshalAs(UnmanagedType.Bool)] bool bBlobUpdate,
            out IntPtr ppValidationResults, out IntPtr ppErrors);

        void RemoveItems(int dwCount, IntPtr phServer, out IntPtr ppErrors);

        void SetActiveState(
            int dwCount, IntPtr phServer,
            [MarshalAs(UnmanagedType.Bool)] bool bActive, out IntPtr ppErrors);

        void SetClientHandles(int dwCount, IntPtr phServer, IntPtr phClient, out IntPtr ppErrors);

        void SetDatatypes(int dwCount, IntPtr phServer, IntPtr pRequestedDatatypes, out IntPtr ppErrors);

        void CreateEnumerator(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppUnk);
    }

    // IOPCSyncIO {39c13a52-011e-11d0-9675-0020afd8adb3}
    [ComImport, Guid("39c13a52-011e-11d0-9675-0020afd8adb3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOPCSyncIO
    {
        void Read(
            int dwSource,
            int dwCount,
            [MarshalAs(UnmanagedType.LPArray)] int[] phServer,
            out IntPtr ppItemValues,
            out IntPtr ppErrors);

        void Write(
            int dwCount,
            [MarshalAs(UnmanagedType.LPArray)] int[] phServer,
            [MarshalAs(UnmanagedType.LPArray)] object[] pItemValues,
            out IntPtr ppErrors);
    }

    // OPCITEMDEF 结构
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OPCITEMDEF
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string szAccessPath;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string szItemID;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bActive;
        public int hClient;
        public int dwBlobSize;
        public IntPtr pBlob;
        public short vtRequestedDataType;
        public short wReserved;
    }

    // OPCITEMRESULT 结构
    [StructLayout(LayoutKind.Sequential)]
    internal struct OPCITEMRESULT
    {
        public int hServer;
        public short vtCanonicalDataType;
        public short wReserved;
        public int dwAccessRights;
        public int dwBlobSize;
        public IntPtr pBlob;
    }

    // OPCITEMSTATE 结构（SyncRead 返回）
    [StructLayout(LayoutKind.Sequential)]
    internal struct OPCITEMSTATE
    {
        public int hClient;
        public long ftTimeStamp;  // FILETIME
        public short wQuality;
        public short wReserved;
        public IntPtr vDataValue; // VARIANT (需要手动解析)
    }

    /// <summary>
    /// AddItems 结果：成功项的 ServerHandle 与每个项的错误码并列返回，
    /// 调用方按 ErrorCodes[i] 是否为 0 判断该项是否添加成功。
    /// </summary>
    public class AddItemsResult
    {
        public int[] ServerHandles { get; set; }
        public int[] ErrorCodes { get; set; }

        public int SuccessCount
        {
            get
            {
                int n = 0;
                if (ErrorCodes == null) return 0;
                for (int i = 0; i < ErrorCodes.Length; i++)
                    if (ErrorCodes[i] == 0) n++;
                return n;
            }
        }
    }

    /// <summary>
    /// 原生 OPC DA 客户端助手
    /// 不依赖 OPCAutomation，直接使用 COM 接口
    /// </summary>
    internal static class RawOpcHelper
    {
        private static readonly Guid IID_IOPCGroupStateMgt =
            new Guid("39c13a50-011e-11d0-9675-0020afd8adb3");

        /// <summary>
        /// 把 OPC 错误码翻译成可读字符串（包含常见标准错误与 DeltaV/Emerson 扩展）
        /// </summary>
        public static string FormatOpcError(int code)
        {
            string hex = "0x" + ((uint)code).ToString("X8");
            string name;
            switch ((uint)code)
            {
                case 0xC0040004: name = "OPC_E_BADTYPE (数据类型不匹配)"; break;
                case 0xC0040006: name = "OPC_E_BADRIGHTS (无访问权限)"; break;
                case 0xC0040007: name = "OPC_E_UNKNOWNITEMID (项不存在于地址空间)"; break;
                case 0xC0040008: name = "OPC_E_INVALIDITEMID (项 ID 语法错误)"; break;
                case 0xC004000B: name = "OPC_E_RANGE (值超出范围)"; break;
                case 0xC004000C: name = "OPC_E_DUPLICATENAME (重名)"; break;
                case 0xC004080C: name = "项暂时不可用 (Emerson 扩展, 地址空间未就绪/模块未下装)"; break;
                default: name = "未知 OPC 错误"; break;
            }
            return hex + " " + name;
        }

        /// <summary>
        /// 在远程 OPC 服务器上创建组
        /// </summary>
        public static void AddGroup(object serverObj, string name, bool active, int updateRate,
            out int serverGroupHandle, out object groupObj)
        {
            var server = (IOPCServer)serverObj;
            int revisedRate;
            Guid iid = IID_IOPCGroupStateMgt;

            server.AddGroup(name, active, updateRate, 0,
                IntPtr.Zero, IntPtr.Zero, 0,
                out serverGroupHandle, out revisedRate,
                ref iid, out groupObj);
        }

        /// <summary>
        /// 向组中添加数据项。
        /// 注意：AddItems 是 OPC 标准的"部分成功"接口 —— 整批 COM 调用成功后，
        /// 仍可能有个别项失败。本方法不再对单项失败抛异常，而是返回每项的错误码，
        /// 由调用方决定如何处理（典型做法：成功项立即投入轮询，失败项后台重试）。
        /// 只有当 AddItems COM 调用本身失败（连接断、组无效等）时才会抛 COMException。
        /// </summary>
        public static AddItemsResult AddItems(object groupObj, string[] itemIds)
        {
            var itemMgt = (IOPCItemMgt)groupObj;
            int count = itemIds.Length;

            int defSize = Marshal.SizeOf(typeof(OPCITEMDEF));
            IntPtr pItems = Marshal.AllocCoTaskMem(defSize * count);
            IntPtr ppResults = IntPtr.Zero;
            IntPtr ppErrors = IntPtr.Zero;

            try
            {
                for (int i = 0; i < count; i++)
                {
                    var itemDef = new OPCITEMDEF
                    {
                        szAccessPath = "",
                        szItemID = itemIds[i],
                        bActive = true,
                        hClient = i + 1,
                        dwBlobSize = 0,
                        pBlob = IntPtr.Zero,
                        vtRequestedDataType = 0, // VT_EMPTY = 服务器自动选择
                        wReserved = 0
                    };
                    Marshal.StructureToPtr(itemDef, pItems + defSize * i, false);
                }

                itemMgt.AddItems(count, pItems, out ppResults, out ppErrors);

                int[] serverHandles = new int[count];
                int[] errorCodes = new int[count];
                int resultSize = Marshal.SizeOf(typeof(OPCITEMRESULT));

                for (int i = 0; i < count; i++)
                {
                    errorCodes[i] = Marshal.ReadInt32(ppErrors + 4 * i);

                    if (errorCodes[i] == 0)
                    {
                        var result = (OPCITEMRESULT)Marshal.PtrToStructure(
                            ppResults + resultSize * i, typeof(OPCITEMRESULT));
                        serverHandles[i] = result.hServer;

                        if (result.pBlob != IntPtr.Zero)
                            Marshal.FreeCoTaskMem(result.pBlob);
                    }
                    else
                    {
                        serverHandles[i] = 0;
                    }
                }

                return new AddItemsResult
                {
                    ServerHandles = serverHandles,
                    ErrorCodes = errorCodes
                };
            }
            finally
            {
                if (ppResults != IntPtr.Zero) Marshal.FreeCoTaskMem(ppResults);
                if (ppErrors != IntPtr.Zero) Marshal.FreeCoTaskMem(ppErrors);

                for (int i = 0; i < count; i++)
                    Marshal.DestroyStructure(pItems + defSize * i, typeof(OPCITEMDEF));
                Marshal.FreeCoTaskMem(pItems);
            }
        }

        /// <summary>
        /// 同步读取
        /// </summary>
        public static Dictionary<string, OpcItemValue> SyncRead(
            object groupObj, int[] serverHandles, string[] itemIds, int dataSource)
        {
            var syncIO = (IOPCSyncIO)groupObj;
            int count = serverHandles.Length;

            IntPtr ppItemValues, ppErrors;
            syncIO.Read(dataSource, count, serverHandles, out ppItemValues, out ppErrors);

            var results = new Dictionary<string, OpcItemValue>();

            try
            {
                for (int i = 0; i < count; i++)
                {
                    int error = Marshal.ReadInt32(ppErrors + 4 * i);

                    if (error == 0)
                    {
                        // 解析 OPCITEMSTATE
                        // 结构: hClient(4) + ftTimeStamp(8) + wQuality(2) + wReserved(2) + VARIANT(16)
                        IntPtr pState = ppItemValues + i * 32; // 近似大小

                        // 用 VARIANT 解析值
                        IntPtr pVariant = pState + 16; // 偏移到 vDataValue
                        object value = Marshal.GetObjectForNativeVariant(pVariant);

                        short quality = Marshal.ReadInt16(pState + 12); // wQuality
                        long fileTime = Marshal.ReadInt64(pState + 4);  // ftTimeStamp
                        DateTime timestamp = DateTime.Now;
                        try { timestamp = DateTime.FromFileTime(fileTime); } catch { }

                        results[itemIds[i]] = new OpcItemValue
                        {
                            Value = value,
                            Quality = (quality & 0xC0) == 0xC0 ? OpcQuality.Good : OpcQuality.Bad,
                            Timestamp = timestamp
                        };

                        // 清理 VARIANT
                        VariantClear(pVariant);
                    }
                    else
                    {
                        results[itemIds[i]] = new OpcItemValue
                        {
                            Value = null,
                            Quality = OpcQuality.Bad,
                            Timestamp = DateTime.Now
                        };
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(ppItemValues);
                Marshal.FreeCoTaskMem(ppErrors);
            }

            return results;
        }

        /// <summary>
        /// 删除组
        /// </summary>
        public static void RemoveGroup(object serverObj, int serverGroupHandle)
        {
            try
            {
                var server = (IOPCServer)serverObj;
                server.RemoveGroup(serverGroupHandle, true);
            }
            catch { }
        }

        [DllImport("oleaut32.dll")]
        private static extern int VariantClear(IntPtr pvarg);
    }
}
