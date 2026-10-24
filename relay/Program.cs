using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace VrcWintunPoc
{
    // Minimal Wintun-based UDP relay for VRChat/Photon (default port 27002).
    // Creates a virtual adapter, routes only <server>/32 through it, reads the IP
    // packets the game sends, forwards payloads from a normal UDP socket (bound to
    // the physical interface via IP_UNICAST_IF), and writes replies back.
    // No decryption: validates redirect + relay only.
    internal static class Program
    {
        private const string AdapterName = "VRCProxy";
        private const string AdapterIp = "10.9.0.1";
        private const int IpUnicastIf = 31;

        [DllImport("wintun.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr WintunCreateAdapter(string name, string tunnelType, IntPtr requestedGuid);

        [DllImport("wintun.dll", SetLastError = true)]
        private static extern IntPtr WintunStartSession(IntPtr adapter, uint capacity);

        [DllImport("wintun.dll", SetLastError = true)]
        private static extern IntPtr WintunReceivePacket(IntPtr session, out uint packetSize);

        [DllImport("wintun.dll", SetLastError = true)]
        private static extern void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);

        [DllImport("wintun.dll", SetLastError = true)]
        private static extern IntPtr WintunAllocateSendPacket(IntPtr session, uint packetSize);

        [DllImport("wintun.dll", SetLastError = true)]
        private static extern void WintunSendPacket(IntPtr session, IntPtr packet);

        [DllImport("wintun.dll", SetLastError = true)]
        private static extern void WintunCloseAdapter(IntPtr adapter);

        [DllImport("iphlpapi.dll")]
        private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

        private static long _outPackets, _inPackets, _outBytes, _inBytes;
        private static int _gamePort;
        private static IPAddress _gameIp;
        private static IPAddress _server;
        private static readonly HashSet<string> _dstSeen = new HashSet<string>();
        private static readonly List<string> _prefixes = new List<string>();
        private static readonly List<byte[]> _prefixNets = new List<byte[]>();
        private static readonly List<int> _prefixBits = new List<int>();
        private static int _tunIf;
        private static System.IO.StreamWriter _dump;
        private static readonly object _dumpLock = new object();
        private static int _dumpCount;

        private static void LogPacket(string dir, byte[] data, int len)
        {
            if (_dump == null || _dumpCount >= 2000000) return;
            lock (_dumpLock)
            {
                if (_dumpCount >= 2000000) return;
                _dumpCount++;
                var sb = new System.Text.StringBuilder(len * 2 + 8);
                sb.Append(dir).Append(',').Append(len).Append(',');
                for (int i = 0; i < len; i++) sb.Append(data[i].ToString("x2"));
                _dump.WriteLine(sb.ToString());
                _dump.Flush();
            }
        }

        private static int Main(string[] args)
        {
            IPAddress server = null;
            int port = 27002;
            string dumpPath = null;
            var prefixArgs = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--server" && i + 1 < args.Length) server = IPAddress.Parse(args[++i]);
                else if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
                else if (args[i] == "--dump" && i + 1 < args.Length) dumpPath = args[++i];
                else if ((args[i] == "--prefix" || args[i] == "--prefixes") && i + 1 < args.Length)
                    prefixArgs.AddRange(args[++i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            }
            if (server == null && prefixArgs.Count == 0)
            {
                Console.Error.WriteLine("usage: VrcWintunPoc --prefix <cidr[,cidr...]> [--port 27002]   (or --server <ip>)");
                return 2;
            }
            _server = server;
            if (prefixArgs.Count == 0) prefixArgs.Add(server + "/32");
            foreach (var item in prefixArgs)
            {
                byte[] net; int bits;
                ParsePrefix(item, out net, out bits);
                _prefixes.Add(item);
                _prefixNets.Add(net);
                _prefixBits.Add(bits);
            }
            Console.WriteLine("prefixes: " + string.Join(", ", _prefixes));
            if (dumpPath != null)
            {
                _dump = new System.IO.StreamWriter(dumpPath, false);
                Console.WriteLine($"dumping first 400 packets to {dumpPath}");
            }
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Cleanup();

            uint physIf = 0;
            var physProbe = server ?? IPAddress.Parse("8.8.8.8");
            int rc = GetBestInterface(ToNetOrder(physProbe), out physIf);
            Console.WriteLine($"physical ifIndex={physIf} (GetBestInterface rc={rc})");

            IntPtr adapter = WintunCreateAdapter(AdapterName, "VRCProxy", IntPtr.Zero);
            if (adapter == IntPtr.Zero)
            {
                Console.Error.WriteLine($"WintunCreateAdapter failed: {Marshal.GetLastWin32Error()}");
                return 1;
            }

            try
            {
                Run("netsh", $"interface ipv4 set address name=\"{AdapterName}\" static {AdapterIp} 255.255.255.0");
                Run("netsh", $"interface ipv4 set subinterface \"{AdapterName}\" mtu=1500 store=active");
                string idxText = Run("powershell", $"-NoProfile -Command \"(Get-NetAdapter -Name '{AdapterName}').ifIndex\"");
                int tunIf = int.Parse(idxText.Trim());
                _tunIf = tunIf;
                Console.WriteLine($"tunnel ifIndex={tunIf}");
                foreach (var item in _prefixes)
                {
                    Run("powershell", $"-NoProfile -Command \"Remove-NetRoute -DestinationPrefix '{item}' -InterfaceIndex {tunIf} -Confirm:$false -ErrorAction SilentlyContinue\"");
                    Run("powershell", $"-NoProfile -Command \"New-NetRoute -DestinationPrefix '{item}' -InterfaceIndex {tunIf} -NextHop 0.0.0.0 -RouteMetric 1 -ErrorAction Stop\"");
                }
                foreach (var item in _prefixes)
                {
                    string check = Run("powershell", $"-NoProfile -Command \"(Get-NetRoute -DestinationPrefix '{item}' | Where-Object {{ $_.InterfaceIndex -eq {tunIf} }} | Select-Object -First 1 | ForEach-Object {{ $_.InterfaceIndex }})\"");
                    Console.WriteLine($"route check: {item} -> ifIndex {check.Trim()}");
                }

                IntPtr session = WintunStartSession(adapter, 0x400000);
                if (session == IntPtr.Zero)
                {
                    Console.Error.WriteLine($"WintunStartSession failed: {Marshal.GetLastWin32Error()}");
                    return 1;
                }

                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IpUnicastIf,
                    BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)physIf)));

                Console.WriteLine($"relay socket {socket.LocalEndPoint}, target {(server == null ? "dynamic" : server + ":" + port)}");
                Console.WriteLine("Intercepting... Ctrl+C to stop.");

                new Thread(() => ReceiveLoop(socket, session)) { IsBackground = true }.Start();
                new Thread(StatsLoop) { IsBackground = true }.Start();

                while (true)
                {
                    IntPtr pkt = WintunReceivePacket(session, out uint size);
                    if (pkt == IntPtr.Zero)
                    {
                        Thread.Sleep(1);
                        continue;
                    }
                    try
                    {
                        if (size < 28) continue;
                        var buf = new byte[size];
                        Marshal.Copy(pkt, buf, 0, (int)size);
                        if ((buf[0] >> 4) != 4) continue;
                        int ihl = (buf[0] & 0x0F) * 4;
                        if (size < ihl + 8) continue;
                        int udpLen = (buf[ihl + 4] << 8) | buf[ihl + 5];
                        int payloadLen = Math.Min(udpLen - 8, (int)size - ihl - 8);
                        if (payloadLen <= 0) continue;

                        var dstIp = new IPAddress(new[] { buf[16], buf[17], buf[18], buf[19] });
                        _gameIp = new IPAddress(new[] { buf[12], buf[13], buf[14], buf[15] });
                        _gamePort = (buf[ihl] << 8) | buf[ihl + 1];
                        int dport = (buf[ihl + 2] << 8) | buf[ihl + 3];

                        if (dport != port || !InPrefix(dstIp)) continue;
                        if (_dstSeen.Count < 16 && _dstSeen.Add(dstIp.ToString()))
                            Console.WriteLine("dst seen: " + dstIp);

                        var payload = new byte[payloadLen];
                        Buffer.BlockCopy(buf, ihl + 8, payload, 0, payloadLen);
                        socket.SendTo(payload, new IPEndPoint(dstIp, dport));
                        Interlocked.Increment(ref _outPackets);
                        Interlocked.Add(ref _outBytes, payloadLen);
                        LogPacket("C", payload, payloadLen);
                    }
                    catch (SocketException ex)
                    {
                        Console.Error.WriteLine($"forward failed: {ex.Message}");
                    }
                    finally
                    {
                        WintunReleaseReceivePacket(session, pkt);
                    }
                }
            }
            finally
            {
                Cleanup();
                WintunCloseAdapter(adapter);
            }
        }

        private static void ReceiveLoop(Socket socket, IntPtr session)
        {
            var buf = new byte[65535];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                int n;
                try { n = socket.ReceiveFrom(buf, ref remote); }
                catch (SocketException) { continue; }

                var ep = (IPEndPoint)remote;
                if (_gameIp == null) continue;

                var packet = BuildIpUdp(ep.Address, ep.Port, _gameIp, _gamePort, buf, n);
                IntPtr dst = WintunAllocateSendPacket(session, (uint)packet.Length);
                if (dst == IntPtr.Zero) continue;
                Marshal.Copy(packet, 0, dst, packet.Length);
                WintunSendPacket(session, dst);
                Interlocked.Increment(ref _inPackets);
                Interlocked.Add(ref _inBytes, n);
                LogPacket("S", buf, n);
            }
        }

        private static byte[] BuildIpUdp(IPAddress srcIp, int srcPort, IPAddress dstIp, int dstPort, byte[] payload, int payloadLen)
        {
            int total = 20 + 8 + payloadLen;
            var p = new byte[total];
            p[0] = 0x45;
            p[2] = (byte)(total >> 8);
            p[3] = (byte)total;
            p[8] = 64;
            p[9] = 17;
            Buffer.BlockCopy(srcIp.GetAddressBytes(), 0, p, 12, 4);
            Buffer.BlockCopy(dstIp.GetAddressBytes(), 0, p, 16, 4);
            p[20] = (byte)(srcPort >> 8);
            p[21] = (byte)srcPort;
            p[22] = (byte)(dstPort >> 8);
            p[23] = (byte)dstPort;
            int udpLen = 8 + payloadLen;
            p[24] = (byte)(udpLen >> 8);
            p[25] = (byte)udpLen;
            Buffer.BlockCopy(payload, 0, p, 28, payloadLen);
            // IPv4 header checksum
            int sum = 0;
            for (int i = 0; i < 20; i += 2) sum += (p[i] << 8) | p[i + 1];
            while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
            int csum = ~sum & 0xFFFF;
            p[10] = (byte)(csum >> 8);
            p[11] = (byte)csum;
            return p;
        }

        private static uint ToNetOrder(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        }

        private static void ParsePrefix(string s, out byte[] net, out int bits)
        {
            var parts = s.Split('/');
            net = IPAddress.Parse(parts[0]).GetAddressBytes();
            bits = parts.Length > 1 ? int.Parse(parts[1]) : 32;
        }

        private static bool InPrefix(IPAddress ip)
        {
            if (_prefixNets.Count == 0) return _server != null && ip.Equals(_server);
            var b = ip.GetAddressBytes();
            for (int p = 0; p < _prefixNets.Count; p++)
                if (InSinglePrefix(b, _prefixNets[p], _prefixBits[p])) return true;
            return false;
        }

        private static bool InSinglePrefix(byte[] b, byte[] net, int bits)
        {
            int full = bits / 8;
            int rem = bits % 8;
            for (int i = 0; i < full; i++)
                if (b[i] != net[i]) return false;
            if (rem > 0)
            {
                int mask = 0xFF << (8 - rem);
                if ((b[full] & mask) != (net[full] & mask)) return false;
            }
            return true;
        }

        private static void Cleanup()
        {
            foreach (var item in _prefixes)
            {
                try
                {
                    if (_tunIf > 0)
                        Run("powershell", $"-NoProfile -Command \"Remove-NetRoute -DestinationPrefix '{item}' -InterfaceIndex {_tunIf} -Confirm:$false -ErrorAction SilentlyContinue\"");
                    else
                        Run("powershell", $"-NoProfile -Command \"Remove-NetRoute -DestinationPrefix '{item}' -Confirm:$false -ErrorAction SilentlyContinue\"");
                }
                catch { }
            }
            try
            {
                Run("powershell", $"-NoProfile -Command \"Remove-NetIPAddress -InterfaceAlias '{AdapterName}' -Confirm:$false -ErrorAction SilentlyContinue\"");
            }
            catch { }
        }

        private static string Run(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return output;
        }

        private static void StatsLoop()
        {
            long lo = 0, li = 0;
            while (true)
            {
                Thread.Sleep(2000);
                long o = Interlocked.Read(ref _outPackets);
                long i = Interlocked.Read(ref _inPackets);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] out={o} (+{o - lo}) in={i} (+{i - li})");
                lo = o; li = i;
            }
        }
    }
}
