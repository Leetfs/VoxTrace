using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Fast pagefile scanner: reads extents, finds [u32 32][u32 0][32-byte key]
// candidates with enough byte diversity, writes "offset hexkey" lines.
// Built with: csc /unsafe /optimize+ /out:FastScan.exe FastScan.cs
class FastScan
{
    const uint GENERIC_READ = 0x80000000;
    const uint FILE_SHARE_READ = 1;
    const uint FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool ReadFile(IntPtr handle, IntPtr buffer, uint count,
        out uint read, IntPtr overlapped);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool SetFilePointerEx(IntPtr handle, long distance,
        out long newPosition, uint method);

    [DllImport("kernel32")]
    static extern bool CloseHandle(IntPtr handle);

    static StreamWriter output;
    static readonly object writeLock = new object();
    const int CHUNK = 64 * 1024 * 1024;

    static int Main(string[] args)
    {
        string drive = "C:";
        string jobsPath = null;
        string outPath = null;
        string filePath = null;
        bool stdinMode = false;
        int threads = 8;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--drive": drive = args[++i]; break;
                case "--jobs": jobsPath = args[++i]; break;
                case "--out": outPath = args[++i]; break;
                case "--threads": threads = int.Parse(args[++i]); break;
                case "--file": filePath = args[++i]; break;
                case "--stdin": stdinMode = true; break;
            }
        }
        if (outPath == null || (jobsPath == null && filePath == null && !stdinMode))
        {
            Console.Error.WriteLine("usage: FastScan --drive C: --jobs jobs.txt --out candidates.txt");
            Console.Error.WriteLine("       FastScan --file sample.bin --out candidates.txt");
            return 2;
        }
        if (stdinMode)
        {
            output = new StreamWriter(outPath, false, Encoding.ASCII);
            ScanStream(Console.OpenStandardInput());
            output.Close();
            return 0;
        }
        if (filePath != null)
        {
            output = new StreamWriter(outPath, false, Encoding.ASCII);
            ScanFile(filePath);
            output.Close();
            return 0;
        }
        var jobs = new List<long[]>();
        foreach (var line in File.ReadAllLines(jobsPath))
        {
            var parts = line.Split(' ');
            if (parts.Length == 2)
                jobs.Add(new long[] { long.Parse(parts[0]), long.Parse(parts[1]) });
        }
        output = new StreamWriter(outPath, false, Encoding.ASCII);
        threads = Math.Min(Math.Max(1, threads), Math.Max(1, jobs.Count));
        var queue = new Queue<long[]>(jobs);
        var workers = new List<Thread>();
        for (int t = 0; t < threads; t++)
        {
            var th = new Thread(delegate() { Worker(drive, queue); });
            th.IsBackground = true;
            th.Start();
            workers.Add(th);
        }
        foreach (var th in workers) th.Join();
        output.Close();
        return 0;
    }

    static void ScanStream(System.IO.Stream input)
    {
        byte[] buffer = new byte[CHUNK + 64];
        byte[] tail = new byte[64];
        byte[] junction = new byte[128];
        GCHandle pinBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        GCHandle pinJunction = GCHandle.Alloc(junction, GCHandleType.Pinned);
        IntPtr pBuffer = pinBuffer.AddrOfPinnedObject();
        IntPtr pJunction = pinJunction.AddrOfPinnedObject();
        int tailLen = 0;
        long offset = 0;
        long total = 0;
        try
        {
            int read;
            while ((read = input.Read(buffer, 0, CHUNK)) > 0)
            {
                if (tailLen > 0)
                {
                    int head = Math.Min(64, read);
                    Buffer.BlockCopy(tail, 0, junction, 0, tailLen);
                    Buffer.BlockCopy(buffer, 0, junction, tailLen, head);
                    Scan(pJunction, tailLen + head, offset - tailLen);
                }
                Scan(pBuffer, read, offset);
                tailLen = Math.Min(64, read);
                Buffer.BlockCopy(buffer, read - tailLen, tail, 0, tailLen);
                offset += read;
                total += read;
            }
        }
        finally
        {
            pinBuffer.Free();
            pinJunction.Free();
        }
        Console.WriteLine("scanned " + total + " bytes");
    }

    static void ScanFile(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            ScanStream(fs);
        }
    }

    static void Worker(string drive, Queue<long[]> queue)
    {
        IntPtr handle = CreateFileW(@"\\.\" + drive, GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            handle = CreateFileW(@"\\.\" + drive, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            Console.Error.WriteLine("sequential open failed: " + err + ", retry flags=0");
        }
        if (handle == new IntPtr(-1))
        {
            Console.Error.WriteLine("open " + drive + " failed: " + Marshal.GetLastWin32Error());
            return;
        }
        byte[] buffer = new byte[CHUNK + 64];
        GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        IntPtr ptr = pinned.AddrOfPinnedObject();
        try
        {
            while (true)
            {
                long[] job;
                lock (queue)
                {
                    if (queue.Count == 0) break;
                    job = queue.Dequeue();
                }
                long position = 0;
                while (position < job[1])
                {
                    int want = (int)Math.Min((long)(CHUNK + 64), job[1] - position);
                    want &= ~511;  // raw volume reads must be sector aligned
                    if (want <= 0) break;
                    long newPos;
                    SetFilePointerEx(handle, job[0] + position, out newPos, 0);
                    uint read;
                    if (!ReadFile(handle, ptr, (uint)want, out read, IntPtr.Zero))
                    {
                        Console.Error.WriteLine("read failed at " + (job[0] + position) + ": " + Marshal.GetLastWin32Error());
                        break;
                    }
                    Scan(ptr, (int)read, job[0] + position);
                    position += read;
                }
            }
        }
        finally
        {
            pinned.Free();
            CloseHandle(handle);
        }
    }

    static unsafe void Scan(IntPtr ptr, int length, long baseOffset)
    {
        ulong* words = (ulong*)ptr;
        int count = length / 8;
        for (int i = 0; i < count; i++)
        {
            if (words[i] != 0x20UL) continue;
            if ((i + 5) * 8 > length) break;
            byte* key = (byte*)ptr + i * 8 + 8;
            ulong seen0 = 0, seen1 = 0, seen2 = 0, seen3 = 0;
            int distinct = 0;
            for (int k = 0; k < 32; k++)
            {
                byte v = key[k];
                ulong bit = 1UL << (v & 63);
                if ((v & 192) == 0) { if ((seen0 & bit) != 0) continue; seen0 |= bit; }
                else if ((v & 192) == 64) { if ((seen1 & bit) != 0) continue; seen1 |= bit; }
                else if ((v & 192) == 128) { if ((seen2 & bit) != 0) continue; seen2 |= bit; }
                else { if ((seen3 & bit) != 0) continue; seen3 |= bit; }
                distinct++;
                if (distinct >= 20) break;
            }
            if (distinct < 20) continue;
            var sb = new StringBuilder(64);
            for (int k = 0; k < 32; k++) sb.Append(key[k].ToString("x2"));
            lock (writeLock)
                output.WriteLine((baseOffset + i * 8 + 8) + " " + sb);
        }
    }
}
