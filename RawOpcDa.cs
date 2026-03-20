using System;
using System.Runtime.InteropServices;

namespace OpcDaClient
{
    /// <summary>
    /// 原生 OPC DA COM 接口定义（不依赖 OPCAutomation）
    /// 与标准 OPC 测试工具使用完全相同的接口
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
    /// 原生 OPC DA 客户端助手
    /// 不依赖 OPCAutomation，直接使用 COM 接口
    /// </summary>
    internal static class RawOpcHelper
    {
        private static readonly Guid IID_IOPCGroupStateMgt =
            new Guid("39c13a50-011e-11d0-9675-0020afd8adb3");

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
        /// 向组中添加数据项
        /// </summary>
        public static int[] AddItems(object groupObj, string[] itemIds)
        {
            var itemMgt = (IOPCItemMgt)groupObj;
            int count = itemIds.Length;

            // 构建 OPCITEMDEF 数组
            int defSize = Marshal.SizeOf(typeof(OPCITEMDEF));
            IntPtr pItems = Marshal.AllocCoTaskMem(defSize * count);

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

                IntPtr ppResults, ppErrors;
                itemMgt.AddItems(count, pItems, out ppResults, out ppErrors);

                // 解析 ServerHandle
                int[] serverHandles = new int[count];
                int resultSize = Marshal.SizeOf(typeof(OPCITEMRESULT));

                for (int i = 0; i < count; i++)
                {
                    var result = (OPCITEMRESULT)Marshal.PtrToStructure(
                        ppResults + resultSize * i, typeof(OPCITEMRESULT));
                    serverHandles[i] = result.hServer;

                    // 释放 Blob
                    if (result.pBlob != IntPtr.Zero)
                        Marshal.FreeCoTaskMem(result.pBlob);
                }

                // 检查错误
                for (int i = 0; i < count; i++)
                {
                    int error = Marshal.ReadInt32(ppErrors + 4 * i);
                    if (error != 0)
                        throw new Exception("添加项 '" + itemIds[i] + "' 失败: 0x" + error.ToString("X8"));
                }

                Marshal.FreeCoTaskMem(ppResults);
                Marshal.FreeCoTaskMem(ppErrors);

                return serverHandles;
            }
            finally
            {
                // 清理 OPCITEMDEF 中的字符串
                for (int i = 0; i < count; i++)
                {
                    Marshal.DestroyStructure(pItems + defSize * i, typeof(OPCITEMDEF));
                }
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
