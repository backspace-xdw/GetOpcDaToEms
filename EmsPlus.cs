using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OpcDaClient
{
    /// <summary>
    /// EMS Plus (EosDapi.dll) P/Invoke 封装
    /// </summary>
    public static class EmsPlus
    {
        #region P/Invoke

        [DllImport(@"C:\Windows\System32\EosDapi.dll", EntryPoint = "GetAxIDVS")]
        public extern static Int32 GetAxIDVS(Int32 dwSrvNo, String szTagName);

        [DllImport(@"C:\Windows\System32\EosDapi.dll", EntryPoint = "GetDxIDVS")]
        public extern static Int32 GetDxIDVS(Int32 dwSrvNo, String szTagName);

        [DllImport(@"C:\Windows\System32\EosDapi.dll", EntryPoint = "GetCxIDVS")]
        public extern static Int32 GetCxIDVS(Int32 dwSrvNo, String szTagName);

        [DllImport(@"C:\Windows\System32\EosDapi.dll", EntryPoint = "SetAxVS", CharSet = CharSet.Ansi)]
        public extern static int SetAxVS(Int32 dwAxID, float fValue, Int32 dwStatus);

        [DllImport(@"C:\Windows\System32\EosDapi.dll", EntryPoint = "SetDxVS")]
        public extern static int SetDxVS(Int32 dwDxID, Boolean bValue, Int32 dwStatus);

        [DllImport(@"C:\Windows\System32\EosDapi.dll", EntryPoint = "SetCxVS", CharSet = CharSet.Ansi)]
        public extern static int SetCxVS(Int32 dwCxID, Int32 dwDBID, string cValue, Int32 dwStatus);

        #endregion

        #region ID 缓存

        private static readonly Dictionary<string, int> _axIdCache = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _dxIdCache = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _cxIdCache = new Dictionary<string, int>();

        public static int GetAxId(string tagName, int srvNo = 0)
        {
            string key = srvNo + ":" + tagName;
            int id;
            if (!_axIdCache.TryGetValue(key, out id))
            {
                id = GetAxIDVS(srvNo, tagName);
                _axIdCache[key] = id;
            }
            return id;
        }

        public static int GetDxId(string tagName, int srvNo = 0)
        {
            string key = srvNo + ":" + tagName;
            int id;
            if (!_dxIdCache.TryGetValue(key, out id))
            {
                id = GetDxIDVS(srvNo, tagName);
                _dxIdCache[key] = id;
            }
            return id;
        }

        public static int GetCxId(string tagName, int srvNo = 0)
        {
            string key = srvNo + ":" + tagName;
            int id;
            if (!_cxIdCache.TryGetValue(key, out id))
            {
                id = GetCxIDVS(srvNo, tagName);
                _cxIdCache[key] = id;
            }
            return id;
        }

        public static void ClearCache()
        {
            _axIdCache.Clear();
            _dxIdCache.Clear();
            _cxIdCache.Clear();
        }

        #endregion

        #region 写入

        public static int WriteAnalog(string tagName, float value, int srvNo = 0)
        {
            return SetAxVS(GetAxId(tagName, srvNo), value, 0x80);
        }

        public static int WriteDigital(string tagName, bool value, int srvNo = 0)
        {
            return SetDxVS(GetDxId(tagName, srvNo), value, 0x80);
        }

        public static int WriteString(string tagName, string value, int dbId = 0, int srvNo = 0)
        {
            return SetCxVS(GetCxId(tagName, srvNo), dbId, value, 0x80);
        }

        #endregion
    }
}
