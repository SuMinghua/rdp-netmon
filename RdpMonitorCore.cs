// ============================================================================
// RDP 网络监视器 - 核心数据采集层
// 数据源:
//   服务端模式: RemoteFX Network 性能对象 (协议级 RTT/带宽/丢包/重传/FEC)
//   客户端模式: mstsc 进程 I/O 计数器 + TCP estats + UDP 套接字探测
//   通用:      TCP/UDP 连接表 (iphlpapi), ICMP ping, WTS 客户端信息
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RdpNetMon
{
    public enum MonitorMode { None = 0, Client = 1, Server = 2 }

    // ---------------- 连接表条目 ----------------
    // 注: GetExtendedTcpTable/GetExtendedUdpTable 返回的端口字段为网络字节序
    //     存储在 32 位字段的低 16 位, 需做 16 位字节交换; 地址字段为网络字节序。
    public sealed class TcpRow
    {
        public int Pid;
        public int State;
        public uint LocalAddrRaw;   // 网络字节序
        public uint LocalPortRaw;   // 网络字节序(低16位)
        public uint RemoteAddrRaw;
        public uint RemotePortRaw;
        public IPAddress LocalAddr { get { return new IPAddress((long)LocalAddrRaw); } }
        public IPAddress RemoteAddr { get { return new IPAddress((long)RemoteAddrRaw); } }
        public ushort LocalPort { get { return Swap16(LocalPortRaw); } }
        public ushort RemotePort { get { return Swap16(RemotePortRaw); } }
        public bool Established { get { return State == Native.MIB_TCP_STATE_ESTAB; } }
        private static ushort Swap16(uint raw)
        {
            uint v = raw & 0xFFFF;
            return (ushort)(((v >> 8) & 0xFF) | ((v << 8) & 0xFF00));
        }
    }

    public sealed class UdpRow
    {
        public int Pid;
        public uint LocalAddrRaw;
        public uint LocalPortRaw;
        public IPAddress LocalAddr { get { return new IPAddress((long)LocalAddrRaw); } }
        public ushort LocalPort { get { uint v = LocalPortRaw & 0xFFFF; return (ushort)(((v >> 8) & 0xFF) | ((v << 8) & 0xFF00)); } }
    }

    // ---------------- P/Invoke 与结构体 ----------------
    public static class Native
    {
        public const int AF_INET = 2;
        public const int TCP_TABLE_OWNER_PID_ALL = 5;
        public const int UDP_TABLE_OWNER_PID = 1;
        public const int MIB_TCP_STATE_ESTAB = 5;
        public const uint ERROR_INSUFFICIENT_BUFFER = 122;

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_UDPROW_OWNER_PID
        {
            public uint localAddr;
            public uint localPort;
            public uint owningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPROW
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TCP_ESTATS_PATH_ROD_v0
        {
            public uint FastRetran;
            public uint Timeouts;
            public uint SubsequentTimeouts;
            public uint CurTimeoutCount;
            public uint AbruptTimeouts;
            public uint PktsRetrans;
            public uint BytesRetrans;
            public uint DupAcksIn;
            public uint SacksRcvd;
            public uint SackBlocksRcvd;
            public uint CongSignals;
            public uint PreCongSumCwnd;
            public uint PreCongSumRtt;
            public uint PostCongSumRtt;
            public uint PostCongCountRtt;
            public uint EcnSignals;
            public uint EceRcvd;
            public uint SendStall;
            public uint QuenchRcvd;
            public uint RetranThresh;
            public uint SndDupAckEpisodes;
            public uint SumBytesReordered;
            public uint NonRecovDa;
            public uint NonRecovDaEpisodes;
            public uint AckAfterFr;
            public uint DsackDups;
            public uint SampleRtt;
            public uint SmoothedRtt;
            public uint RttVar;
            public uint MaxRtt;
            public uint MinRtt;
            public uint SumRtt;
            public uint CountRtt;
            public uint CurRto;
            public uint MaxRto;
            public uint MinRto;
            public uint CurMss;
            public uint MaxMss;
            public uint MinMss;
            public uint SpuriousRtoDetections;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetPerTcpConnectionEStats(IntPtr row, int estatsType, IntPtr rw, uint rwVersion, uint rwSize, IntPtr ros, uint rosVersion, uint rosSize, IntPtr rod, uint rodVersion, uint rodSize);

        // 原生 Set 只有 6 个参数: Row, EstatsType, Rw, RwVersion, RwSize, Offset
        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint SetPerTcpConnectionEStats(IntPtr row, int estatsType, IntPtr rw, uint rwVersion, uint rwSize, uint offset);

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool WTSEnumerateSessions(IntPtr hServer, int Reserved, int Version, out IntPtr ppSessionInfo, out int pCount);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern void WTSFreeMemory(IntPtr pMemory);

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool WTSQuerySessionInformation(IntPtr hServer, int SessionId, int WTSInfoClass, out IntPtr ppBuffer, out int pBytesReturned);
    }

    // ---------------- 连接表读取 ----------------
    public static class NetTables
    {
        public static List<TcpRow> GetTcpRows()
        {
            var rows = new List<TcpRow>();
            int size = 0;
            uint ret = Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, Native.AF_INET, Native.TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret != Native.ERROR_INSUFFICIENT_BUFFER || size <= 0) return rows;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                ret = Native.GetExtendedTcpTable(buf, ref size, false, Native.AF_INET, Native.TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret != 0) return rows;
                int count = Marshal.ReadInt32(buf);
                int rowSize = Marshal.SizeOf(typeof(Native.MIB_TCPROW_OWNER_PID));
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = new IntPtr(buf.ToInt64() + 4 + (long)i * rowSize);
                    Native.MIB_TCPROW_OWNER_PID r = (Native.MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(p, typeof(Native.MIB_TCPROW_OWNER_PID));
                    rows.Add(new TcpRow
                    {
                        Pid = (int)r.owningPid,
                        State = (int)r.state,
                        LocalAddrRaw = r.localAddr,
                        LocalPortRaw = r.localPort,
                        RemoteAddrRaw = r.remoteAddr,
                        RemotePortRaw = r.remotePort
                    });
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return rows;
        }

        public static List<UdpRow> GetUdpRows()
        {
            var rows = new List<UdpRow>();
            int size = 0;
            uint ret = Native.GetExtendedUdpTable(IntPtr.Zero, ref size, false, Native.AF_INET, Native.UDP_TABLE_OWNER_PID, 0);
            if (ret != Native.ERROR_INSUFFICIENT_BUFFER || size <= 0) return rows;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                ret = Native.GetExtendedUdpTable(buf, ref size, false, Native.AF_INET, Native.UDP_TABLE_OWNER_PID, 0);
                if (ret != 0) return rows;
                int count = Marshal.ReadInt32(buf);
                int rowSize = Marshal.SizeOf(typeof(Native.MIB_UDPROW_OWNER_PID));
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = new IntPtr(buf.ToInt64() + 4 + (long)i * rowSize);
                    Native.MIB_UDPROW_OWNER_PID r = (Native.MIB_UDPROW_OWNER_PID)Marshal.PtrToStructure(p, typeof(Native.MIB_UDPROW_OWNER_PID));
                    rows.Add(new UdpRow { Pid = (int)r.owningPid, LocalAddrRaw = r.localAddr, LocalPortRaw = r.localPort });
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return rows;
        }
    }

    // ---------------- TCP estats (连接级统计) ----------------
    public static class TcpEstats
    {
        public const int TcpConnectionEstatsData = 1;
        public const int TcpConnectionEstatsPath = 3;

        private static Native.MIB_TCPROW BuildRow(TcpRow row)
        {
            Native.MIB_TCPROW mib = new Native.MIB_TCPROW();
            mib.state = (uint)row.State;
            mib.localAddr = row.LocalAddrRaw;
            mib.localPort = row.LocalPortRaw;
            mib.remoteAddr = row.RemoteAddrRaw;
            mib.remotePort = row.RemotePortRaw;
            return mib;
        }

        // 返回 0 = 成功; 其他为 Win32 错误码. collectionEnabled 指示采集是否已启用
        // (每种统计类型的启用是独立的: 只启用 DATA 而 PATH 未启用时, PATH ROD 仍是未初始化垃圾数据,
        //  实测验证必须用 PATH 类型自身的 EnableCollection 判断)
        public static uint TryGetPathStats(TcpRow row, out Native.TCP_ESTATS_PATH_ROD_v0 stats, out bool collectionEnabled)
        {
            stats = new Native.TCP_ESTATS_PATH_ROD_v0();
            collectionEnabled = false;
            Native.MIB_TCPROW mib = BuildRow(row);
            GCHandle h = GCHandle.Alloc(mib, GCHandleType.Pinned);
            try
            {
                // 查 PATH 类型自身的 EnableCollection (RW 结构必须恰好 1 字节, 否则返回 1784)
                byte[] rw = new byte[1];
                GCHandle hrw = GCHandle.Alloc(rw, GCHandleType.Pinned);
                uint retRw = Native.GetPerTcpConnectionEStats(h.AddrOfPinnedObject(), TcpConnectionEstatsPath,
                    hrw.AddrOfPinnedObject(), 0, 1, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0);
                hrw.Free();
                collectionEnabled = (retRw == 0 && rw[0] == 1);

                int rodSize = Marshal.SizeOf(typeof(Native.TCP_ESTATS_PATH_ROD_v0));
                IntPtr rod = Marshal.AllocHGlobal(rodSize);
                try
                {
                    uint ret = Native.GetPerTcpConnectionEStats(h.AddrOfPinnedObject(), TcpConnectionEstatsPath,
                        IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, rod, 0, (uint)rodSize);
                    if (ret == 0)
                        stats = (Native.TCP_ESTATS_PATH_ROD_v0)Marshal.PtrToStructure(rod, typeof(Native.TCP_ESTATS_PATH_ROD_v0));
                    return ret;
                }
                finally { Marshal.FreeHGlobal(rod); }
            }
            finally { h.Free(); }
        }

        // 尝试启用采集 (需管理员权限). 每种统计类型独立启用, 需分别启用 DATA 与 PATH;
        // 各 RW 结构均为单字节 BOOLEAN, 缓冲区必须恰好 1 字节 (实测 4 字节会返回 1784 ERROR_INVALID_USER_BUFFER)
        public static uint TryEnable(TcpRow row)
        {
            Native.MIB_TCPROW mib = BuildRow(row);
            byte[] rw = new byte[1];
            rw[0] = 1;
            GCHandle h1 = GCHandle.Alloc(mib, GCHandleType.Pinned);
            GCHandle h2 = GCHandle.Alloc(rw, GCHandleType.Pinned);
            try
            {
                uint rData = Native.SetPerTcpConnectionEStats(h1.AddrOfPinnedObject(), TcpConnectionEstatsData,
                    h2.AddrOfPinnedObject(), 0, 1, 0);
                uint rPath = Native.SetPerTcpConnectionEStats(h1.AddrOfPinnedObject(), TcpConnectionEstatsPath,
                    h2.AddrOfPinnedObject(), 0, 1, 0);
                return rData != 0 ? rData : rPath;
            }
            finally { h1.Free(); h2.Free(); }
        }

        public static string ErrorName(uint code)
        {
            if (code == 0) return "成功";
            if (code == 5) return "拒绝访问(需管理员权限)";
            if (code == 87) return "参数错误";
            if (code == 1168) return "连接未找到";
            if (code == 1784) return "无效用户缓冲区";
            return "错误码 " + code;
        }
    }

    // ---------------- WTS 会话信息 ----------------
    public sealed class WtsClientInfo
    {
        public int SessionId;
        public string WinStation = "";
        public string ClientName = "";
        public string Domain = "";
        public string UserName = "";
        public int ClientBuild;
    }

    public static class Wts
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct WTS_SESSION_INFO
        {
            public int SessionId;
            public IntPtr pWinStationName;
            public int State;
        }

        private const int WTSActive = 0;
        private const int WTSClientNameClass = 10;
        private const int WTSClientBuildNumberClass = 9;

        public static WtsClientInfo FindActiveRdpSession()
        {
            try
            {
                IntPtr buf;
                int count;
                if (!Native.WTSEnumerateSessions(IntPtr.Zero, 0, 1, out buf, out count)) return null;
                try
                {
                    int sz = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr p = new IntPtr(buf.ToInt64() + (long)i * sz);
                        WTS_SESSION_INFO si = (WTS_SESSION_INFO)Marshal.PtrToStructure(p, typeof(WTS_SESSION_INFO));
                        string ws = si.pWinStationName != IntPtr.Zero ? Marshal.PtrToStringUni(si.pWinStationName) : "";
                        if (si.State == WTSActive && si.SessionId != 0 && ws != null && ws.IndexOf("RDP", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var info = new WtsClientInfo { SessionId = si.SessionId, WinStation = ws };
                            FillClientInfo(info);
                            return info;
                        }
                    }
                }
                finally { Native.WTSFreeMemory(buf); }
            }
            catch { }
            return null;
        }

        private static void FillClientInfo(WtsClientInfo info)
        {
            // 客户端计算机名: WTSClientName (LPWSTR)
            try
            {
                IntPtr buf;
                int bytes;
                if (Native.WTSQuerySessionInformation(IntPtr.Zero, info.SessionId, WTSClientNameClass, out buf, out bytes))
                {
                    try { info.ClientName = buf != IntPtr.Zero ? Marshal.PtrToStringUni(buf) : ""; }
                    finally { Native.WTSFreeMemory(buf); }
                }
            }
            catch { }
            // 客户端版本号: WTSClientBuildNumber (ULONG)
            try
            {
                IntPtr buf;
                int bytes;
                if (Native.WTSQuerySessionInformation(IntPtr.Zero, info.SessionId, WTSClientBuildNumberClass, out buf, out bytes))
                {
                    try { info.ClientBuild = buf != IntPtr.Zero ? Marshal.ReadInt32(buf, 0) : 0; }
                    finally { Native.WTSFreeMemory(buf); }
                }
            }
            catch { }
        }
    }

    // ---------------- RemoteFX Network 性能计数器 ----------------
    public sealed class RemoteFxData
    {
        public double TotalRecvBytes = double.NaN;
        public double TotalSentBytes = double.NaN;
        public double CurTcpRtt = double.NaN;
        public double CurUdpRtt = double.NaN;
        public double BaseTcpRtt = double.NaN;
        public double BaseUdpRtt = double.NaN;
        public double CurTcpBw = double.NaN;     // bits/s
        public double CurUdpBw = double.NaN;
        public double UdpPktsRecv = double.NaN;  // packets/sec
        public double UdpPktsSent = double.NaN;
        public double TcpRecvRate = double.NaN;
        public double TcpSentRate = double.NaN;
        public double UdpRecvRate = double.NaN;
        public double UdpSentRate = double.NaN;
        public double LossRate = double.NaN;
        public double RetranRate = double.NaN;
        public double FecRate = double.NaN;
    }

    public sealed class RemoteFxSet : IDisposable
    {
        public readonly string Instance;
        private readonly PerformanceCounter[] _c = new PerformanceCounter[18];

        private RemoteFxSet(string inst) { Instance = inst; }

        public static RemoteFxSet TryCreate()
        {
            try
            {
                if (!PerformanceCounterCategory.Exists("RemoteFX Network")) return null;
                var cat = new PerformanceCounterCategory("RemoteFX Network");
                string inst = null;
                foreach (var n in cat.GetInstanceNames())
                {
                    if (n.IndexOf("RDP-tcp", StringComparison.OrdinalIgnoreCase) >= 0) { inst = n; break; }
                }
                if (inst == null) return null;
                var s = new RemoteFxSet(inst);
                s._c[0] = New("Total Received Bytes", inst);
                s._c[1] = New("Total Sent Bytes", inst);
                s._c[2] = New("Current TCP RTT", inst);
                s._c[3] = New("Current UDP RTT", inst);
                s._c[4] = New("Base TCP RTT", inst);
                s._c[5] = New("Base UDP RTT", inst);
                s._c[6] = New("Current TCP Bandwidth", inst);
                s._c[7] = New("Current UDP Bandwidth", inst);
                s._c[8] = New("UDP Packets Received/sec", inst);
                s._c[9] = New("UDP Packets Sent/sec", inst);
                s._c[10] = New("TCP Received Rate", inst);
                s._c[11] = New("TCP Sent Rate", inst);
                s._c[12] = New("UDP Received Rate", inst);
                s._c[13] = New("UDP Sent Rate", inst);
                s._c[14] = New("Loss Rate", inst);
                s._c[15] = New("Retransmission Rate", inst);
                s._c[16] = New("FEC Rate", inst);
                s._c[17] = New("Total Received Rate", inst);
                return s;
            }
            catch { return null; }
        }

        private static PerformanceCounter New(string name, string inst)
        {
            return new PerformanceCounter("RemoteFX Network", name, inst, true);
        }

        public RemoteFxData Read()
        {
            var d = new RemoteFxData();
            d.TotalRecvBytes = ReadAt(0);
            d.TotalSentBytes = ReadAt(1);
            d.CurTcpRtt = ReadAt(2);
            d.CurUdpRtt = ReadAt(3);
            d.BaseTcpRtt = ReadAt(4);
            d.BaseUdpRtt = ReadAt(5);
            d.CurTcpBw = ReadAt(6);
            d.CurUdpBw = ReadAt(7);
            d.UdpPktsRecv = ReadAt(8);
            d.UdpPktsSent = ReadAt(9);
            d.TcpRecvRate = ReadAt(10);
            d.TcpSentRate = ReadAt(11);
            d.UdpRecvRate = ReadAt(12);
            d.UdpSentRate = ReadAt(13);
            d.LossRate = ReadAt(14);
            d.RetranRate = ReadAt(15);
            d.FecRate = ReadAt(16);
            return d;
        }

        private double ReadAt(int i)
        {
            try { return _c[i] != null ? _c[i].NextValue() : double.NaN; }
            catch { return double.NaN; }
        }

        public void Dispose()
        {
            foreach (var c in _c) { try { if (c != null) c.Dispose(); } catch { } }
        }
    }

    // ---------------- 进程 I/O 计数器 (客户端模式) ----------------
    public sealed class ProcIoSet : IDisposable
    {
        private readonly List<PerformanceCounter> _read = new List<PerformanceCounter>();
        private readonly List<PerformanceCounter> _write = new List<PerformanceCounter>();
        public readonly List<string> Instances = new List<string>();

        // namePrefix: "mstsc" (客户端) 或 "svchost" (服务端兜底)
        public static ProcIoSet CreateFor(IEnumerable<int> pids, string namePrefix)
        {
            var set = new ProcIoSet();
            try
            {
                var cat = new PerformanceCounterCategory("Process");
                var pidSet = new HashSet<int>(pids);
                foreach (var inst in cat.GetInstanceNames())
                {
                    if (!inst.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    int pid;
                    if (!TryGetPid(inst, out pid)) continue;
                    if (!pidSet.Contains(pid)) continue;
                    set._read.Add(new PerformanceCounter("Process", "IO Read Bytes/sec", inst, true));
                    set._write.Add(new PerformanceCounter("Process", "IO Write Bytes/sec", inst, true));
                    set.Instances.Add(inst);
                }
                return set.Instances.Count > 0 ? set : null;
            }
            catch
            {
                set.Dispose();
                return null;
            }
        }

        private static bool TryGetPid(string inst, out int pid)
        {
            pid = 0;
            try
            {
                using (var c = new PerformanceCounter("Process", "ID Process", inst, true))
                {
                    pid = (int)c.NextValue();
                    return pid > 0;
                }
            }
            catch { return false; }
        }

        // 返回 read/sec, write/sec (第一次调用可能为 0)
        public void Read(out double readBps, out double writeBps)
        {
            readBps = 0; writeBps = 0;
            foreach (var c in _read) { try { readBps += c.NextValue(); } catch { } }
            foreach (var c in _write) { try { writeBps += c.NextValue(); } catch { } }
        }

        public void Dispose()
        {
            foreach (var c in _read) { try { c.Dispose(); } catch { } }
            foreach (var c in _write) { try { c.Dispose(); } catch { } }
        }
    }

    // ---------------- 采样结果 ----------------
    public sealed class Sample
    {
        public DateTime Time;
        public MonitorMode Mode;
        public bool Connected;
        public string RemoteIp = "";
        public bool UdpNegotiated;
        public bool UdpActive;
        public double RxBps;
        public double TxBps;
        public double RxTotalBytes = -1;
        public double TxTotalBytes = -1;
        public double PeakRxBps;
        public double PeakTxBps;
        public double TcpRttMs = -1;
        public double UdpRttMs = -1;
        public double BaseTcpRttMs = -1;
        public double BaseUdpRttMs = -1;
        public double EffTcpRttMs = -1;   // 有效 RTT (estats > RemoteFX(校验后) > ICMP)
        public bool RfxRttStale;          // RemoteFX RTT 与 ICMP 严重不符时标记存疑
        public double TcpBwEst = -1;
        public double UdpBwEst = -1;
        public int IcmpRttMs = -1;
        public double LossRatePct = -1;
        public double RetranRatePct = -1;
        public double FecRate = -1;
        public double UdpSharePct = -1;
        public uint EstatsRetrans;
        public uint EstatsTimeouts;
        public uint EstatsCongSignals;
        public uint EstatsSampleRttMs;
        public uint EstatsRttVarMs;
        public bool EstatsOk;
        public string SessionName = "";
        public string RfxInstance = "";
        public string ClientName = "";
        public int ClientBuild;
        public TimeSpan Duration;
        public string Note = "";
    }

    // ---------------- 日志写入 ----------------
    public sealed class LogWriter : IDisposable
    {
        public readonly string LogDir;
        public readonly string CsvPath;
        public readonly string EventPath;
        private readonly StreamWriter _csv;
        private readonly StreamWriter _evt;
        private readonly object _lock = new object();

        public LogWriter(string dir)
        {
            LogDir = dir;
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            CsvPath = Path.Combine(dir, "rdpnet_" + stamp + ".csv");
            EventPath = Path.Combine(dir, "events_" + stamp + ".log");
            _csv = new StreamWriter(new FileStream(CsvPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(true));
            _evt = new StreamWriter(new FileStream(EventPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            _evt.AutoFlush = true;
            _csv.WriteLine("time,mode,connected,remote_ip,udp_negotiated,udp_active,tcp_rtt_ms,udp_rtt_ms,icmp_rtt_ms,rx_bps,tx_bps,rx_total_bytes,tx_total_bytes,tcp_bw_est_bps,udp_bw_est_bps,loss_pct,retransmission_pct,fec_rate,udp_share_pct,estats_sample_rtt_ms,estats_rtt_var_ms,estats_retrans,estats_timeouts,estats_cong_signals,session,rfx_instance,client_name,client_build,note");
            _csv.Flush();
        }

        public void WriteSample(Sample s)
        {
            try
            {
                lock (_lock)
                {
                    _csv.Write(s.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)); _csv.Write(",");
                    _csv.Write(s.Mode); _csv.Write(",");
                    _csv.Write(s.Connected ? "1" : "0"); _csv.Write(",");
                    _csv.Write(Csv(s.RemoteIp)); _csv.Write(",");
                    _csv.Write(s.UdpNegotiated ? "1" : "0"); _csv.Write(",");
                    _csv.Write(s.UdpActive ? "1" : "0"); _csv.Write(",");
                    _csv.Write(Num(s.TcpRttMs)); _csv.Write(",");
                    _csv.Write(Num(s.UdpRttMs)); _csv.Write(",");
                    _csv.Write(s.IcmpRttMs >= 0 ? s.IcmpRttMs.ToString(CultureInfo.InvariantCulture) : ""); _csv.Write(",");
                    _csv.Write(s.RxBps.ToString("0.##", CultureInfo.InvariantCulture)); _csv.Write(",");
                    _csv.Write(s.TxBps.ToString("0.##", CultureInfo.InvariantCulture)); _csv.Write(",");
                    _csv.Write(Num(s.RxTotalBytes)); _csv.Write(",");
                    _csv.Write(Num(s.TxTotalBytes)); _csv.Write(",");
                    _csv.Write(Num(s.TcpBwEst)); _csv.Write(",");
                    _csv.Write(Num(s.UdpBwEst)); _csv.Write(",");
                    _csv.Write(Num(s.LossRatePct)); _csv.Write(",");
                    _csv.Write(Num(s.RetranRatePct)); _csv.Write(",");
                    _csv.Write(Num(s.FecRate)); _csv.Write(",");
                    _csv.Write(Num(s.UdpSharePct)); _csv.Write(",");
                    _csv.Write(s.EstatsOk ? s.EstatsSampleRttMs.ToString(CultureInfo.InvariantCulture) : ""); _csv.Write(",");
                    _csv.Write(s.EstatsOk ? s.EstatsRttVarMs.ToString(CultureInfo.InvariantCulture) : ""); _csv.Write(",");
                    _csv.Write(s.EstatsOk ? s.EstatsRetrans.ToString(CultureInfo.InvariantCulture) : ""); _csv.Write(",");
                    _csv.Write(s.EstatsOk ? s.EstatsTimeouts.ToString(CultureInfo.InvariantCulture) : ""); _csv.Write(",");
                    _csv.Write(s.EstatsOk ? s.EstatsCongSignals.ToString(CultureInfo.InvariantCulture) : ""); _csv.Write(",");
                    _csv.Write(Csv(s.SessionName)); _csv.Write(",");
                    _csv.Write(Csv(s.RfxInstance)); _csv.Write(",");
                    _csv.Write(Csv(s.ClientName)); _csv.Write(",");
                    _csv.Write(s.ClientBuild > 0 ? s.ClientBuild.ToString(CultureInfo.InvariantCulture) : ""); _csv.Write(",");
                    _csv.Write(Csv(s.Note));
                    _csv.WriteLine();
                    _csv.Flush();
                }
            }
            catch { }
        }

        public void Event(string level, string msg)
        {
            try
            {
                lock (_lock)
                {
                    _evt.WriteLine("[{0}] [{1}] {2}", DateTime.Now.ToString("HH:mm:ss.fff"), level, msg);
                }
            }
            catch { }
        }

        private static string Csv(string v) { return (v ?? "").Replace(",", ";").Replace("\r", " ").Replace("\n", " "); }
        private static string Num(double v) { return v < 0 || double.IsNaN(v) ? "" : v.ToString("0.##", CultureInfo.InvariantCulture); }

        public void Dispose()
        {
            lock (_lock)
            {
                try { _csv.Flush(); _csv.Dispose(); } catch { }
                try { _evt.Dispose(); } catch { }
            }
        }
    }

    // ---------------- 监视器 ----------------
    public sealed class Monitor : IDisposable
    {
        public readonly string BaseDir;
        public readonly LogWriter Log;
        public Action<Sample> OnSample;
        public Action<string, string> OnEvent;

        private Thread _thread;
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        private readonly object _lock = new object();
        private Sample _latest = new Sample { Time = DateTime.MinValue };
        private readonly List<Sample> _ring = new List<Sample>();
        private const int RingCap = 900;

        private bool _wasConnected;
        private DateTime _sessionStart;
        private bool _lastUdp;
        private MonitorMode _lastMode = MonitorMode.None;
        private DateTime _lastPing = DateTime.MinValue;
        private int _lastPingResult = -1;
        private string _pingTarget = "";
        private Ping _ping;

        private RemoteFxSet _rfx;
        private ProcIoSet _procIo;
        private int _procIoOwnerPid = -1;   // 服务端兜底: TermService 所在 svchost 的 pid
        private DateTime _procIoLastTry = DateTime.MinValue;
        private uint _estatsError;
        private bool _estatsErrorLogged;
        private bool _estatsEnableTried;
        private double _lastRfxRecv = -1;
        private double _lastRfxSent = -1;
        private double _peakRx;
        private double _peakTx;
        private bool _disposed;

        public Monitor(string baseDir)
        {
            BaseDir = baseDir;
            Log = new LogWriter(Path.Combine(baseDir, "logs"));
        }

        public Sample Latest { get { lock (_lock) { return _latest; } } }
        public List<Sample> SnapshotRing() { lock (_lock) { return new List<Sample>(_ring); } }

        public void Start()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "RdpNetMon" };
            _thread.Start();
        }

        public void Stop()
        {
            _stop.Set();
            if (_thread != null) { _thread.Join(2500); _thread = null; }
        }

        private void Event(string level, string msg)
        {
            Log.Event(level, msg);
            try { var cb = OnEvent; if (cb != null) cb(level, msg); } catch { }
        }

        private void Loop()
        {
            Event("INFO", "监视器启动, 日志目录: " + Log.LogDir);
            while (!_stop.IsSet)
            {
                try { Tick(); }
                catch (Exception ex) { Event("ERR", "采样异常: " + ex.Message); }
                _stop.Wait(1000);
            }
            Event("INFO", "监视器停止");
        }

        private void Tick()
        {
            var tcp = NetTables.GetTcpRows();
            var udp = NetTables.GetUdpRows();
            List<int> mstscPids = new List<int>();
            try { mstscPids = Process.GetProcessesByName("mstsc").Select(p => p.Id).ToList(); } catch { }

            MonitorMode mode = MonitorMode.None;
            TcpRow rdpTcp = null;
            if (mstscPids.Count > 0)
            {
                mode = MonitorMode.Client;
                rdpTcp = tcp.FirstOrDefault(r => r.Established && mstscPids.Contains(r.Pid) && (r.RemotePort == 3389 || r.LocalPort == 3389));
            }
            if (mode == MonitorMode.None)
            {
                var srv = tcp.FirstOrDefault(r => r.Established && r.LocalPort == 3389 && !IPAddress.IsLoopback(r.RemoteAddr));
                if (srv != null) { mode = MonitorMode.Server; rdpTcp = srv; }
            }

            bool connected = rdpTcp != null;
            string remoteIp = connected ? rdpTcp.RemoteAddr.ToString() : "";

            // ---- 模式/连接 事件 ----
            if (mode != _lastMode)
            {
                string name = mode == MonitorMode.Server ? "服务端(有人连入本机)" : mode == MonitorMode.Client ? "客户端(本机 mstsc 连出)" : "无活动连接";
                Event("INFO", "模式切换: " + ModeName(_lastMode) + " -> " + name);
                _lastMode = mode;
            }
            if (connected && !_wasConnected)
            {
                _sessionStart = DateTime.Now;
                _peakRx = 0; _peakTx = 0;
                _lastRfxRecv = -1; _lastRfxSent = -1;
                _estatsErrorLogged = false; _estatsEnableTried = false;
                Event("CONN", "检测到 RDP 连接: 远程 " + remoteIp + " (模式: " + (mode == MonitorMode.Server ? "服务端" : "客户端") + ")");
            }
            if (!connected && _wasConnected)
            {
                var d = DateTime.Now - _sessionStart;
                Event("CONN", "RDP 连接已断开 (持续 " + FmtDuration(d) + ")");
            }
            _wasConnected = connected;

            // ---- 计数器集维护 ----
            if (mode == MonitorMode.Client)
            {
                if (_procIo == null || _procIoOwnerPid == -1)
                {
                    if (_procIo != null) { _procIo.Dispose(); _procIo = null; }
                    _procIo = ProcIoSet.CreateFor(mstscPids, "mstsc");
                    _procIoOwnerPid = _procIo != null ? 0 : -1;
                    if (_procIo == null) Event("WARN", "无法创建 mstsc 进程 I/O 计数器");
                }
            }
            else if (mode == MonitorMode.Server)
            {
                if (_procIo == null && (DateTime.Now - _procIoLastTry).TotalSeconds >= 10)
                {
                    _procIoLastTry = DateTime.Now;
                    var p = ProcIoSet.CreateFor(new[] { rdpTcp != null ? rdpTcp.Pid : -1 }, "svchost");
                    if (p != null) { _procIo = p; _procIoOwnerPid = rdpTcp != null ? rdpTcp.Pid : -1; }
                    else if (connected) Event("WARN", "无法定位 RDP 服务所在进程 (svchost) 的 I/O 计数器");
                }
                else if (_procIo != null && connected && rdpTcp != null && rdpTcp.Pid != _procIoOwnerPid)
                {
                    _procIo.Dispose(); _procIo = null;
                    _procIoOwnerPid = -1;
                }
            }
            else
            {
                if (_procIo != null) { _procIo.Dispose(); _procIo = null; _procIoOwnerPid = -1; }
            }

            if (_rfx == null) _rfx = RemoteFxSet.TryCreate();

            // ---- 采样 ----
            var s = new Sample();
            s.Time = DateTime.Now;
            s.Mode = mode;
            s.Connected = connected;
            s.RemoteIp = remoteIp;
            s.PeakRxBps = _peakRx;
            s.PeakTxBps = _peakTx;
            s.Duration = connected ? (DateTime.Now - _sessionStart) : TimeSpan.Zero;

            RemoteFxData rfx = _rfx != null ? _rfx.Read() : null;

            // UDP 状态
            bool udpNeg = false, udpAct = false;
            if (mode == MonitorMode.Client)
            {
                udpNeg = udp.Any(r => mstscPids.Contains(r.Pid));
            }
            if (rfx != null)
            {
                if (!double.IsNaN(rfx.CurUdpRtt) && rfx.CurUdpRtt > 0) udpNeg = true;
                if (!double.IsNaN(rfx.BaseUdpRtt) && rfx.BaseUdpRtt > 0) udpNeg = true;   // 基准 UDP RTT > 0 说明 UDP 已协商
                if (!double.IsNaN(rfx.CurUdpBw) && rfx.CurUdpBw > 0) udpNeg = true;
                double pkts = (double.IsNaN(rfx.UdpPktsRecv) ? 0 : rfx.UdpPktsRecv) + (double.IsNaN(rfx.UdpPktsSent) ? 0 : rfx.UdpPktsSent);
                if (pkts > 0.5) udpAct = true;
            }
            s.UdpNegotiated = udpNeg;
            s.UdpActive = udpAct;
            if (connected && udpNeg != _lastUdp)
            {
                Event("INFO", "UDP 状态变化: " + (udpNeg ? "已协商/启用" : "未启用(纯 TCP)"));
                _lastUdp = udpNeg;
            }

            // RTT / 带宽 / 丢包 (RemoteFX 优先)
            if (rfx != null)
            {
                s.TcpRttMs = rfx.CurTcpRtt;
                s.UdpRttMs = rfx.CurUdpRtt;
                s.BaseTcpRttMs = rfx.BaseTcpRtt;
                s.BaseUdpRttMs = rfx.BaseUdpRtt;
                s.TcpBwEst = rfx.CurTcpBw;
                s.UdpBwEst = rfx.CurUdpBw;
                s.LossRatePct = rfx.LossRate;
                s.RetranRatePct = rfx.RetranRate;
                s.FecRate = rfx.FecRate;
                s.RfxInstance = _rfx.Instance;
                double totalRate = (double.IsNaN(rfx.TcpRecvRate) ? 0 : rfx.TcpRecvRate) + (double.IsNaN(rfx.TcpSentRate) ? 0 : rfx.TcpSentRate)
                                 + (double.IsNaN(rfx.UdpRecvRate) ? 0 : rfx.UdpRecvRate) + (double.IsNaN(rfx.UdpSentRate) ? 0 : rfx.UdpSentRate);
                double udpRate = (double.IsNaN(rfx.UdpRecvRate) ? 0 : rfx.UdpRecvRate) + (double.IsNaN(rfx.UdpSentRate) ? 0 : rfx.UdpSentRate);
                if (totalRate > 0) s.UdpSharePct = udpRate * 100.0 / totalRate;
            }

            // 吞吐量
            if (mode == MonitorMode.Server && rfx != null && !double.IsNaN(rfx.TotalRecvBytes))
            {
                s.RxTotalBytes = rfx.TotalRecvBytes;
                s.TxTotalBytes = rfx.TotalSentBytes;
                if (_lastRfxRecv >= 0)
                {
                    double d = rfx.TotalRecvBytes - _lastRfxRecv;
                    if (d < 0) d += 4294967296.0;   // 32 位计数器回绕
                    s.RxBps = d;
                }
                if (_lastRfxSent >= 0)
                {
                    double d = rfx.TotalSentBytes - _lastRfxSent;
                    if (d < 0) d += 4294967296.0;
                    s.TxBps = d;
                }
                _lastRfxRecv = rfx.TotalRecvBytes;
                _lastRfxSent = rfx.TotalSentBytes;
                if (connected && s.RxBps > _peakRx) _peakRx = s.RxBps;
                if (connected && s.TxBps > _peakTx) _peakTx = s.TxBps;
                s.PeakRxBps = _peakRx;
                s.PeakTxBps = _peakTx;
            }
            else if (_procIo != null)
            {
                double r, w;
                _procIo.Read(out r, out w);
                s.RxBps = r;
                s.TxBps = w;
                if (connected && s.RxBps > _peakRx) _peakRx = s.RxBps;
                if (connected && s.TxBps > _peakTx) _peakTx = s.TxBps;
                s.PeakRxBps = _peakRx;
                s.PeakTxBps = _peakTx;
            }

            // TCP estats (连接级统计: 重传/超时/拥塞)
            Native.TCP_ESTATS_PATH_ROD_v0 estStats = new Native.TCP_ESTATS_PATH_ROD_v0();
            bool estRead = false;
            bool estEnabled = false;
            uint estErr = 99;
            if (connected && rdpTcp != null)
            {
                estErr = TcpEstats.TryGetPathStats(rdpTcp, out estStats, out estEnabled);
                estRead = true;
                if (estErr != 0 && !_estatsErrorLogged)
                {
                    _estatsErrorLogged = true;
                    _estatsError = estErr;
                    Event("WARN", "TCP 连接级统计不可用: " + TcpEstats.ErrorName(estErr) + " (已回退到其他指标)");
                }
            }

            // ICMP RTT (链路级, 每 2 秒)
            if (connected && !string.IsNullOrEmpty(remoteIp))
            {
                if (_ping == null) _ping = new Ping();
                if (_pingTarget != remoteIp) { _pingTarget = remoteIp; _lastPing = DateTime.MinValue; _lastPingResult = -1; }
                if ((DateTime.Now - _lastPing).TotalSeconds >= 2)
                {
                    _lastPing = DateTime.Now;
                    try
                    {
                        var reply = _ping.Send(remoteIp, 400);
                        _lastPingResult = reply != null && reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
                    }
                    catch { _lastPingResult = -1; }
                }
                s.IcmpRttMs = _lastPingResult;
            }
            else
            {
                _pingTarget = "";
                _lastPingResult = -1;
                s.IcmpRttMs = -1;
            }

            // WTS 会话信息 (服务端)
            if (mode == MonitorMode.Server)
            {
                try
                {
                    var w = Wts.FindActiveRdpSession();
                    if (w != null)
                    {
                        s.SessionName = w.WinStation + " (ID " + w.SessionId + ")";
                        s.ClientName = w.ClientName;
                        s.ClientBuild = w.ClientBuild;
                    }
                }
                catch { }
            }

            // estats 数据处理: 采集启用时数据有效(仍做轻量合理性校验);
            // 未启用时内核返回未初始化数据, 尝试启用一次(需管理员权限)
            if (estRead && estErr == 0)
            {
                if (estEnabled)
                {
                    bool plausible = PlausibleEstats(estStats)
                        && (s.IcmpRttMs < 0 || estStats.SampleRtt <= 5.0 * s.IcmpRttMs + 50);
                    if (plausible)
                    {
                        s.EstatsOk = true;
                        s.EstatsSampleRttMs = estStats.SampleRtt;
                        s.EstatsRttVarMs = estStats.RttVar;
                        s.EstatsRetrans = estStats.PktsRetrans;
                        s.EstatsTimeouts = estStats.Timeouts;
                        s.EstatsCongSignals = estStats.CongSignals;
                    }
                }
                else if (!_estatsEnableTried)
                {
                    _estatsEnableTried = true;
                    uint e2 = rdpTcp != null ? TcpEstats.TryEnable(rdpTcp) : 87;
                    Event("WARN", "TCP estats 采集未启用, 尝试启用: " + TcpEstats.ErrorName(e2)
                        + (e2 == 0 ? " (后续采样生效)" : ""));
                }
            }

            // ---- 有效 RTT 计算: estats > RemoteFX(校验后) > ICMP ----
            if (s.EstatsOk)
            {
                s.EffTcpRttMs = s.EstatsSampleRttMs;
            }
            else if (s.TcpRttMs > 0)
            {
                bool stale = s.IcmpRttMs >= 0 && s.TcpRttMs > 3.0 * (s.IcmpRttMs + 5);
                s.RfxRttStale = stale;
                if (!stale) s.EffTcpRttMs = s.TcpRttMs;
            }
            if (s.EffTcpRttMs < 0) s.EffTcpRttMs = s.IcmpRttMs >= 0 ? s.IcmpRttMs : -1;

            // ---- 提交 ----
            lock (_lock)
            {
                _latest = s;
                _ring.Add(s);
                if (_ring.Count > RingCap) _ring.RemoveRange(0, _ring.Count - RingCap);
            }
            Log.WriteSample(s);
            try { var cb = OnSample; if (cb != null) cb(s); } catch { }
        }

        private static bool PlausibleEstats(Native.TCP_ESTATS_PATH_ROD_v0 st)
        {
            if (st.SampleRtt == 0 || st.SampleRtt > 5000) return false;
            if (st.RttVar > 5000) return false;
            if (st.PktsRetrans > 50000000u) return false;
            if (st.Timeouts > 50000000u) return false;
            if (st.CongSignals > 50000000u) return false;
            return true;
        }

        private static string ModeName(MonitorMode m)
        {
            return m == MonitorMode.Server ? "服务端" : m == MonitorMode.Client ? "客户端" : "无连接";
        }

        public static string FmtDuration(TimeSpan d)
        {
            return d.TotalHours >= 1 ? string.Format("{0:0}小时{1:00}分{2:00}秒", (int)d.TotalHours, d.Minutes, d.Seconds)
                                     : string.Format("{0:00}分{1:00}秒", (int)d.TotalMinutes, d.Seconds);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            if (_rfx != null) { _rfx.Dispose(); _rfx = null; }
            if (_procIo != null) { _procIo.Dispose(); _procIo = null; }
            if (_ping != null) { try { _ping.Dispose(); } catch { } }
            Log.Dispose();
        }
    }
}
