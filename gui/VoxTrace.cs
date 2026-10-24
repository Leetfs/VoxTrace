using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VoxTrace
{
    public class TrackInfo
    {
        public int Actor;
        public string Name = "";
        public string Path;
        public string Key = "";
        public double Seconds;
        public bool Speaking;
        public long FileSize;
        public double Duration;
        public double Rms;
        public float[] Peaks = new float[0];
    }

    public static class Wav
    {
        public static bool Load(string path, out TrackInfo t, out string error)
        {
            t = new TrackInfo();
            t.Path = path;
            error = null;
            try
            {
                t.FileSize = new FileInfo(path).Length;
                using (var fs = File.OpenRead(path))
                using (var br = new BinaryReader(fs))
                {
                    if (new string(br.ReadChars(4)) != "RIFF") { error = "not RIFF"; return false; }
                    br.ReadInt32();
                    if (new string(br.ReadChars(4)) != "WAVE") { error = "not WAVE"; return false; }
                    int channels = 1, bits = 16, rate = 48000;
                    long dataStart = -1;
                    int dataLen = 0;
                    while (fs.Position + 8 <= fs.Length)
                    {
                        string id = new string(br.ReadChars(4));
                        int size = br.ReadInt32();
                        if (id == "fmt ")
                        {
                            br.ReadInt16();
                            channels = br.ReadInt16();
                            rate = br.ReadInt32();
                            br.ReadInt32();
                            br.ReadInt16();
                            bits = br.ReadInt16();
                            if (size > 16) fs.Position += size - 16;
                        }
                        else if (id == "data")
                        {
                            dataStart = fs.Position;
                            dataLen = size;
                            fs.Position += size;
                        }
                        else
                        {
                            fs.Position += size + (size & 1);
                        }
                    }
                    if (dataStart < 0) { error = "no data"; return false; }
                    int bytesPerSample = bits / 8;
                    t.Duration = (double)(dataLen / bytesPerSample / channels) / rate;
                    fs.Position = dataStart;
                    var raw = br.ReadBytes(dataLen);
                    double sum = 0; int cnt = 0;
                    int buckets = 800;
                    var peaks = new float[buckets];
                    int perBucket = Math.Max(1, (raw.Length / 2) / buckets);
                    int idx = 0;
                    for (int i = 0; i + 1 < raw.Length; i += 2)
                    {
                        short s = BitConverter.ToInt16(raw, i);
                        double v = Math.Abs((double)s);
                        sum += v * v; cnt++;
                        int b = Math.Min(buckets - 1, idx / perBucket);
                        if (v > peaks[b]) peaks[b] = (float)(v / 32768.0);
                        idx++;
                    }
                    t.Rms = cnt > 0 ? Math.Sqrt(sum / cnt) : 0;
                    t.Peaks = peaks;
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }

    public static class Mci
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciSendString(string command, StringBuilder buffer, int bufferSize, IntPtr hwnd);

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern bool mciGetErrorString(int error, StringBuilder buffer, int length);

        public static string ErrorText(int code)
        {
            try
            {
                var sb = new StringBuilder(256);
                if (mciGetErrorString(code, sb, sb.Capacity)) return sb.ToString();
            }
            catch { }
            return "MCI 错误 " + code;
        }

        public static int Send(string cmd)
        {
            return mciSendString(cmd, null, 0, IntPtr.Zero);
        }

        public static long Status(string alias, string what)
        {
            var sb = new StringBuilder(64);
            mciSendString(string.Format("status {0} {1}", alias, what), sb, 64, IntPtr.Zero);
            long v;
            long.TryParse(sb.ToString(), out v);
            return v;
        }
    }

    public class NameDialog : Form
    {
        private ComboBox combo;
        public string Value { get { return combo.Text.Trim(); } }

        public NameDialog(List<string> names, string current, int actor)
        {
            Text = "给 actor " + actor + " 指定玩家名";
            Width = 420; Height = 160; FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false;
            var lbl = new Label { Text = "名字（可从日志导入的列表中选择，也可手动输入）：", Left = 12, Top = 12, Width = 380 };
            combo = new ComboBox { Left = 12, Top = 36, Width = 380, DropDownStyle = ComboBoxStyle.DropDown };
            combo.Items.AddRange(names.ToArray());
            combo.Text = current ?? "";
            var ok = new Button { Text = "确定", Left = 220, Top = 76, Width = 80, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Left = 310, Top = 76, Width = 80, DialogResult = DialogResult.Cancel };
            Controls.Add(lbl); Controls.Add(combo); Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }
    }

    public class MainForm : Form
    {
        private DataGridView grid;
        private WavePanel wave;
        private Button btnPlay, btnPause, btnStop, btnLoad, btnImport, btnSave;
        private Button btnListen, btnStopListen, btnAuto, btnStopAuto, btnExport;
        private TrackBar seek, volume;
        private Label timeLabel, statusLabel, listenStatus;
        private Timer timer, pipelineTimer;
        private List<TrackInfo> tracks = new List<TrackInfo>();
        private List<string> importedNames = new List<string>();
        private string folder;
        private int currentIndex = -1;
        private bool opened;
        private const string Alias = "track";

        private Process relay;
        private string capturePath;
        private Process live;
        private string liveOutDir;
        private string sessionKey;
        private bool autoMode;
        private bool extracting;
        private DateTime lastExtract = DateTime.MinValue;
        private DateTime eligibleAt = DateTime.MinValue;
        private int extractAttempts;
        private int lastVrcPid;
        private long lastCaptureSize;
        private long lastStatsPackets;
        private DateTime lastPacketChange = DateTime.MinValue;
        private string logFile;
        private long logOffset;
        private DateTime loadedAt = DateTime.MinValue;
        private DateTime readySince = DateTime.MinValue;
        private DateTime lastNamesAt = DateTime.MinValue;
        private bool extractingNames;
        private readonly HashSet<int> manualActors = new HashSet<int>();
        private readonly string workspace;
        private readonly string relayPath;
        private readonly bool isAdmin;
        private readonly Dictionary<string, string> cfg = new Dictionary<string, string>();
        private readonly string capturesDir;
        private readonly string pagefileDrive;

        public MainForm()
        {
            workspace = Path.GetFullPath(Path.Combine(Application.StartupPath, ".."));
            LoadConfig();
            var relayRel = Cfg("relay_exe", @"bin\VrcWintunPoc.exe");
            relayPath = Path.IsPathRooted(relayRel) ? relayRel : Path.Combine(workspace, relayRel);
            var capRel = Cfg("captures_dir", "captures");
            capturesDir = Path.IsPathRooted(capRel) ? capRel : Path.Combine(workspace, capRel);
            pagefileDrive = Cfg("pagefile_drive", "C:");
            isAdmin = CheckAdmin();

            Text = "VoxTrace - VRChat 语音分轨";
            Width = 1220; Height = 780;
            StartPosition = FormStartPosition.CenterScreen;

            var top = new Panel { Dock = DockStyle.Top, Height = 108 };
            btnLoad = MakeButton("加载会话文件夹", 8, 8);
            btnImport = MakeButton("导入日志名字", 148, 8);
            btnSave = MakeButton("保存绑定", 288, 8);
            btnListen = MakeButton("开始监听", 428, 8);
            btnStopListen = MakeButton("停止监听", 568, 8);
            btnAuto = MakeButton("一键开始", 8, 44, 200);
            btnAuto.BackColor = Color.FromArgb(0, 140, 200);
            btnAuto.ForeColor = Color.White;
            btnStopAuto = MakeButton("停止分轨", 218, 44, 130);
            btnExport = MakeButton("导出音轨", 498, 44, 130);
            btnExport.Click += (s, e) => ExportTracks();
            btnLoad.Click += (s, e) => LoadFolder();
            btnImport.Click += (s, e) => ImportNames();
            btnSave.Click += (s, e) => SaveNames();
            btnListen.Click += (s, e) => StartListening();
            btnStopListen.Click += (s, e) => StopListening();
            btnAuto.Click += (s, e) => StartAuto();
            btnStopAuto.Click += (s, e) => StopAuto();
            listenStatus = new Label { Left = 640, Top = 50, Width = 560, Height = 24, Text = "", ForeColor = Color.DimGray };
            top.Controls.Add(btnLoad); top.Controls.Add(btnImport); top.Controls.Add(btnSave);
            top.Controls.Add(btnListen); top.Controls.Add(btnStopListen);
            top.Controls.Add(btnAuto); top.Controls.Add(btnStopAuto); top.Controls.Add(btnExport); top.Controls.Add(listenStatus);

            grid = new DataGridView
            {
                Dock = DockStyle.Left,
                Width = 520,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                RowHeadersVisible = false
            };
            grid.Columns.Add("actor", "actor");
            grid.Columns.Add("name", "玩家名");
            grid.Columns.Add("dur", "时长");
            grid.Columns.Add("rms", "RMS");
            grid.Columns["actor"].ReadOnly = true;
            grid.Columns["dur"].ReadOnly = true;
            grid.Columns["rms"].ReadOnly = true;
            grid.SelectionChanged += (s, e) => { if (grid.SelectedRows.Count > 0) SelectTrack(grid.SelectedRows[0].Index); };
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) AssignName(e.RowIndex); };

            wave = new WavePanel { Dock = DockStyle.Fill };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 92 };
            btnPlay = MakeButton("播放", 8, 8);
            btnPause = MakeButton("暂停", 148, 8);
            btnStop = MakeButton("停止", 288, 8);
            seek = new TrackBar { Left = 8, Top = 44, Width = 700, Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None };
            timeLabel = new Label { Left = 720, Top = 48, Width = 160, Text = "00:00 / 00:00" };
            volume = new TrackBar { Left = 890, Top = 44, Width = 150, Minimum = 0, Maximum = 1000, Value = 700, TickStyle = TickStyle.None };
            btnPlay.Click += (s, e) => Play();
            btnPause.Click += (s, e) => { if (opened) Mci.Send("pause " + Alias); };
            btnStop.Click += (s, e) => StopPlayback();
            seek.Scroll += (s, e) => { if (opened) Mci.Send(string.Format("seek {0} to {1}", Alias, seek.Value)); };
            volume.Scroll += (s, e) => { if (opened) Mci.Send(string.Format("setaudio {0} volume to {1}", Alias, volume.Value)); };
            bottom.Controls.Add(btnPlay); bottom.Controls.Add(btnPause); bottom.Controls.Add(btnStop);
            bottom.Controls.Add(seek); bottom.Controls.Add(timeLabel); bottom.Controls.Add(volume);

            statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                Text = isAdmin ? "就绪（管理员）" : "未以管理员运行：一键功能不可用",
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(wave);
            Controls.Add(grid);
            Controls.Add(bottom);
            Controls.Add(top);
            Controls.Add(statusLabel);

            timer = new Timer { Interval = 500 };
            timer.Tick += (s, e) => OnTick();
            timer.Start();

            pipelineTimer = new Timer { Interval = 2000 };
            pipelineTimer.Tick += (s, e) => OnPipelineTick();
            pipelineTimer.Start();

            FormClosing += OnClosing;
            UpdateListenStatus();
        }

        private void LoadConfig()
        {
            try
            {
                var path = Path.Combine(workspace, "config.json");
                if (!File.Exists(path)) return;
                var text = File.ReadAllText(path, Encoding.UTF8);
                foreach (Match m in Regex.Matches(text, "\"([a-z_]+)\"\\s*:\\s*(?:\"([^\"]*)\"|([0-9.]+))"))
                    cfg[m.Groups[1].Value] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            }
            catch { }
        }

        private string RelayProcessName()
        {
            try { return Path.GetFileNameWithoutExtension(relayPath); }
            catch { return "VrcWintunPoc"; }
        }

        private string Cfg(string key, string fallback)
        {
            string v;
            return cfg.TryGetValue(key, out v) && v.Length > 0 ? v : fallback;
        }

        private static bool CheckAdmin()
        {
            try
            {
                return new WindowsPrincipal(WindowsIdentity.GetCurrent())
                    .IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private Button MakeButton(string text, int x, int y, int w = 130)
        {
            return new Button { Text = text, Left = x, Top = y, Width = w, Height = 28 };
        }

        // ================= one-click pipeline =================

        private void StartAuto()
        {
            if (!isAdmin)
            {
                var r = MessageBox.Show(this,
                    "一键功能需要管理员权限（Wintun 虚拟网卡 + 读取 pagefile）。\r\n\r\n是否以管理员身份重新启动本程序？",
                    "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes) RestartElevated();
                return;
            }
            autoMode = true;
            sessionKey = null;
            extracting = false;
            lastExtract = DateTime.MinValue;
            extractAttempts = 0;
            lastVrcPid = 0;
            lastCaptureSize = 0;
            lastStatsPackets = 0;
            lastPacketChange = DateTime.Now;
            readySince = DateTime.MinValue;
            InitLogWatch(true);
            eligibleAt = DateTime.Now.AddSeconds(2);
            PipelineLog("一键开始：等待 VRChat 日志出现 Finished entering world");
            if (relay == null || relay.HasExited) StartListening();
            if (relay == null || relay.HasExited) { autoMode = false; return; }
            statusLabel.Text = "一键流程已启动：进入世界后会自动提取密钥并开始分轨";
            StartLiveWatch();
        }

        private void StopAuto()
        {
            autoMode = false;
            StopLive();
            statusLabel.Text = "分轨已停止（监听保持）";
        }

        private static void PipelineLog(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(Application.StartupPath, "_pipeline.log"),
                    DateTime.Now.ToString("HH:mm:ss ") + msg + Environment.NewLine);
            }
            catch { }
        }

        private void StartLiveWatch()
        {
            if (live != null && !live.HasExited) return;
            var d = capturesDir;
            if (!Directory.Exists(d)) Directory.CreateDirectory(d);
            liveOutDir = Path.Combine(d, "live_" + DateTime.Now.ToString("HHmmss"));
            Directory.CreateDirectory(liveOutDir);
            folder = liveOutDir;
            tracks.Clear();
            grid.Rows.Clear();
            statusLabel.Text = "分轨输出目录：" + liveOutDir + "（等待密钥）";
        }

        private void StartLive()
        {
            if (sessionKey == null || liveOutDir == null || capturePath == null) return;
            if (live != null && !live.HasExited) return;
            var py = FindPython();
            if (py == null) { statusLabel.Text = "找不到 python，无法启动分轨"; return; }
            var log = Path.Combine(Application.StartupPath, "_live_gui.log");
            var psi = new ProcessStartInfo
            {
                FileName = py,
                Arguments = string.Format("-u -m voxtrace live --capture \"{0}\" --key {1} --out \"{2}\"",
                    capturePath, sessionKey, liveOutDir),
                WorkingDirectory = workspace,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            try
            {
                live = new Process { StartInfo = psi, EnableRaisingEvents = true };
                live.OutputDataReceived += (s, e) => { if (e.Data != null) AppendLog(log, e.Data); };
                live.ErrorDataReceived += (s, e) => { if (e.Data != null) AppendLog(log, e.Data); };
                live.Start();
                live.BeginOutputReadLine();
                live.BeginErrorReadLine();
                statusLabel.Text = "分轨已启动，密钥：" + sessionKey.Substring(0, 12) + "...";
                ExtractNamesAsync();
            }
            catch (Exception ex)
            {
                statusLabel.Text = "启动分轨失败：" + ex.Message;
            }
        }

        private void StopLive()
        {
            if (live != null && !live.HasExited)
            {
                try { live.Kill(); live.WaitForExit(3000); } catch { }
            }
            live = null;
        }

        private string FindPython()
        {
            var configured = Cfg("python", "");
            if (configured.Length > 0)
            {
                var p = Path.IsPathRooted(configured) ? configured : Path.Combine(workspace, configured);
                if (File.Exists(p)) return p;
            }
            var paths = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var part in paths.Split(';'))
            {
                if (part.Trim().Length == 0) continue;
                try
                {
                    var c = Path.Combine(part.Trim(), "python.exe");
                    if (File.Exists(c)) return c;
                }
                catch { }
            }
            return "python";
        }

        private string VrchatLogDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @"AppData\LocalLow\VRChat\VRChat");
        }

        private string NewestLog()
        {
            try
            {
                var dir = VrchatLogDir();
                if (!Directory.Exists(dir)) return null;
                string best = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (var f in Directory.GetFiles(dir, "output_log_*.txt"))
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > bestTime) { bestTime = t; best = f; }
                }
                return best;
            }
            catch { return null; }
        }

        private static bool IsWorldLoadedLine(string line)
        {
            return line.IndexOf("Finished entering world", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("after world loaded", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static DateTime ParseLogTime(string line)
        {
            try
            {
                if (line.Length >= 19)
                {
                    DateTime t;
                    if (DateTime.TryParseExact(line.Substring(0, 19), "yyyy.MM.dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                        return t;
                }
            }
            catch { }
            return DateTime.MinValue;
        }

        private void InitLogWatch(bool scanRecent)
        {
            logFile = NewestLog();
            logOffset = 0;
            loadedAt = DateTime.MinValue;
            if (logFile == null) return;
            try
            {
                long len = new FileInfo(logFile).Length;
                if (scanRecent && len > 0)
                {
                    long begin = Math.Max(0, len - 512 * 1024);
                    using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        fs.Seek(begin, SeekOrigin.Begin);
                        if (begin > 0) sr.ReadLine();
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            if (!IsWorldLoadedLine(line))
                                continue;
                            var t = ParseLogTime(line);
                            if (t == DateTime.MinValue) t = DateTime.Now;
                            if ((DateTime.Now - t).TotalSeconds < 300) loadedAt = t;
                        }
                    }
                }
                logOffset = len;
            }
            catch { logOffset = 0; }
            PipelineLog("日志监听：" + (logFile ?? "未找到 output_log") +
                        (loadedAt == DateTime.MinValue ? "" : "（近期已加载过世界）"));
        }

        private void PollWorldLoaded()
        {
            var newest = NewestLog();
            if (newest != null && !string.Equals(newest, logFile, StringComparison.OrdinalIgnoreCase))
            {
                InitLogWatch(true);
            }
            if (logFile == null || !File.Exists(logFile))
            {
                InitLogWatch(true);
                return;
            }
            try
            {
                long len = new FileInfo(logFile).Length;
                if (len < logOffset) logOffset = 0;
                if (len == logOffset) return;
                using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    fs.Seek(logOffset, SeekOrigin.Begin);
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (!IsWorldLoadedLine(line))
                            continue;
                        var t = ParseLogTime(line);
                        loadedAt = t == DateTime.MinValue ? DateTime.Now : t;
                        PipelineLog("检测到世界加载完成：" + line.Trim());
                    }
                    logOffset = fs.Position;
                }
            }
            catch { }
        }

        private void OnPipelineTick()
        {
            if (!autoMode) return;
            if (relay == null || relay.HasExited) { return; }
            if (capturePath == null || !File.Exists(capturePath)) return;

            int currentPid = VrcPid();
            if (currentPid != 0 && lastVrcPid != 0 && currentPid != lastVrcPid)
            {
                RestartCaptureForNewPid(currentPid);
                return;
            }
            if (currentPid != 0 && lastVrcPid == 0) lastVrcPid = currentPid;

            if (sessionKey == null)
            {
                if (extracting) return;
                if (DateTime.Now < eligibleAt) return;
                if (extractAttempts >= 30)
                {
                    statusLabel.Text = "自动取密钥多次未命中，请检查是否已进入世界";
                    PipelineLog("自动取密钥达到重试上限");
                    eligibleAt = DateTime.Now.AddSeconds(60);
                    extractAttempts = 0;
                    return;
                }
                if (VrcPid() == 0)
                {
                    statusLabel.Text = "VRChat 未运行，等待启动";
                    return;
                }
                long size = 0;
                try { size = new FileInfo(capturePath).Length; } catch { }
                if (size < 1024) return;
                PollWorldLoaded();
                if (loadedAt == DateTime.MinValue)
                {
                    if (readySince == DateTime.MinValue) readySince = DateTime.Now;
                    if (size > 512 * 1024 && (DateTime.Now - readySince).TotalSeconds > 15)
                    {
                        loadedAt = DateTime.Now;
                        PipelineLog("日志未捕获 Finished entering world，按流量兜底");
                    }
                    else
                    {
                        statusLabel.Text = "等待 VRChat 进入世界（监听日志）";
                        return;
                    }
                }
                statusLabel.Text = "世界加载完成，开始取密钥…";
                ExtractKeyAsync();
            }
            else if (live == null || live.HasExited)
            {
                StartLive();
            }
            else
            {
                RefreshTracksIfNeeded();
                if (!extractingNames && (DateTime.Now - lastNamesAt).TotalSeconds > 5)
                    ExtractNamesAsync();
                CheckKeyHealth();
            }
        }

        private int VrcPid()
        {
            var procs = Process.GetProcessesByName("VRChat");
            return procs.Length > 0 ? procs[0].Id : 0;
        }

        private long StatsPackets()
        {
            if (liveOutDir == null) return 0;
            var path = Path.Combine(liveOutDir, "live_stats.json");
            if (!File.Exists(path)) return 0;
            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                var global = Regex.Match(text, "\"datagrams\"\\s*:\\s*(\\d+)");
                if (global.Success) return long.Parse(global.Groups[1].Value);
                long total = 0;
                foreach (Match m in Regex.Matches(text, "\"packets\"\\s*:\\s*(\\d+)"))
                    total += long.Parse(m.Groups[1].Value);
                return total;
            }
            catch { return 0; }
        }

        private void CheckKeyHealth()
        {
            if (capturePath == null) return;
            long size = 0;
            try { size = new FileInfo(capturePath).Length; } catch { return; }
            long pid = VrcPid();
            if (pid != 0 && lastVrcPid != 0 && pid != lastVrcPid)
            {
                RestartCaptureForNewPid((int)pid);
                return;
            }
            long packets = StatsPackets();
            if (packets > lastStatsPackets)
            {
                lastStatsPackets = packets;
                lastCaptureSize = size;
                lastPacketChange = DateTime.Now;
                return;
            }
            if (size - lastCaptureSize > 300 * 1024 &&
                (DateTime.Now - lastPacketChange).TotalSeconds > 25)
            {
                ExpireKey("抓包持续增长但解密停止（密钥失效）");
            }
        }

        private void RestartCaptureForNewPid(int pid)
        {
            PipelineLog("VRChat 进程变化，重建抓包文件 pid=" + pid);
            try
            {
                if (relay != null && !relay.HasExited)
                {
                    relay.Kill();
                    relay.WaitForExit(3000);
                }
            }
            catch { }
            relay = null;
            sessionKey = null;
            extracting = false;
            StopLive();
            capturePath = null;
            lastVrcPid = pid;
            lastStatsPackets = 0;
            lastPacketChange = DateTime.Now;
            lastCaptureSize = 0;
            loadedAt = DateTime.MinValue;
            readySince = DateTime.MinValue;
            extractAttempts = 0;
            eligibleAt = DateTime.Now.AddSeconds(2);
            autoMode = true;
            InitLogWatch(true);
            statusLabel.Text = "VRChat 已重启，正在重建抓包…";
            StartListening();
        }

        private void ExpireKey(string reason)
        {
            PipelineLog("密钥失效：" + reason);
            sessionKey = null;
            extracting = false;
            StopLive();
            StartLiveWatch();
            extractAttempts = 0;
            eligibleAt = DateTime.Now.AddSeconds(2);
            lastVrcPid = 0;
            lastStatsPackets = 0;
            lastPacketChange = DateTime.Now;
            InitLogWatch(true);
            readySince = DateTime.MinValue;
            try { lastCaptureSize = new FileInfo(capturePath).Length; } catch { lastCaptureSize = 0; }
            statusLabel.Text = "检测到重连/密钥失效，正在自动重新提取密钥…";
        }

        private void ExtractKeyAsync()
        {
            extracting = true;
            extractAttempts++;
            lastExtract = DateTime.Now;
            var cap = capturePath;
            var py = FindPython();
            statusLabel.Text = string.Format("正在提取密钥（第 {0} 次）...", extractAttempts);
            long capBytes = 0;
            try { capBytes = new FileInfo(cap).Length; } catch { }
            PipelineLog(string.Format("开始取密钥 attempt={0} capture={1} bytes={2} python={3}",
                extractAttempts, cap, capBytes, py));
            Task.Factory.StartNew(() =>
            {
                string output = "";
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = py,
                        Arguments = string.Format("-u -m voxtrace key --capture \"{0}\" --drive {1}",
                            cap, pagefileDrive),
                        WorkingDirectory = workspace,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                        p.WaitForExit(240000);
                    }
                }
                catch (Exception ex) { output = ex.Message; }
                try { File.AppendAllText(Path.Combine(Application.StartupPath, "_pipeline.log"),
                    DateTime.Now.ToString("HH:mm:ss ") + "output tail: " + output.Substring(Math.Max(0, output.Length - 4000)) + Environment.NewLine); } catch { }

                Invoke((Action)(() =>
                {
                    extracting = false;
                    var m = Regex.Match(output, @"([0-9a-f]{64})");
                    if (m.Success)
                    {
                        sessionKey = m.Groups[1].Value;
                        lastVrcPid = VrcPid();
                        lastStatsPackets = 0;
                        lastPacketChange = DateTime.Now;
                        try { lastCaptureSize = new FileInfo(cap).Length; } catch { lastCaptureSize = 0; }
                        statusLabel.Text = "密钥已获取：" + sessionKey.Substring(0, 16) + "...";
                        StartLive();
                    }
                    else
                    {
                        statusLabel.Text = "本次未命中密钥，5 秒后自动重试";
                        eligibleAt = DateTime.Now.AddSeconds(5);
                        PipelineLog("未命中密钥，5 秒后重试");
                    }
                }));
            });
        }

        // ================= listening =================

        private void StartListening()
        {
            if (!isAdmin)
            {
                var r = MessageBox.Show(this,
                    "开始监听需要管理员权限（Wintun 虚拟网卡）。\r\n\r\n是否以管理员身份重新启动本程序？",
                    "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes) RestartElevated();
                return;
            }
            if (relay != null && !relay.HasExited)
            {
                MessageBox.Show(this, "监听已经在运行。", "提示");
                return;
            }
            if (Process.GetProcessesByName(RelayProcessName()).Length > 0)
            {
                MessageBox.Show(this,
                    "检测到已有 VrcWintunPoc 中继进程在运行（可能是外部启动）。\r\n请先停止它，或在程序内停止后再开始。",
                    "提示");
                return;
            }
            if (!File.Exists(relayPath))
            {
                MessageBox.Show(this, "找不到中继程序：\r\n" + relayPath, "错误");
                return;
            }
            var dir = capturesDir;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            capturePath = Path.Combine(dir, "voice_" + DateTime.Now.ToString("HHmmss") + ".txt");

            var psi = new ProcessStartInfo
            {
                FileName = relayPath,
                Arguments = string.Format("--prefix {0} --port {1} --dump \"{2}\"",
                    Cfg("relay_prefix", "82.117.224.0/24"), Cfg("relay_port", "27002"), capturePath),
                WorkingDirectory = Path.GetDirectoryName(relayPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            try
            {
                var log = Path.Combine(Application.StartupPath, "_relay_gui.log");
                relay = new Process { StartInfo = psi, EnableRaisingEvents = true };
                relay.OutputDataReceived += (s, e) => { if (e.Data != null) AppendLog(log, e.Data); };
                relay.ErrorDataReceived += (s, e) => { if (e.Data != null) AppendLog(log, e.Data); };
                relay.Start();
                relay.BeginOutputReadLine();
                relay.BeginErrorReadLine();
                System.Threading.Thread.Sleep(400);
                if (relay.HasExited)
                {
                    string tail = "";
                    try
                    {
                        var lp = Path.Combine(Application.StartupPath, "_relay_gui.log");
                        if (File.Exists(lp))
                        {
                            var lines = File.ReadAllLines(lp);
                            tail = string.Join("\r\n", lines, Math.Max(0, lines.Length - 6), Math.Min(6, lines.Length));
                        }
                    }
                    catch { }
                    MessageBox.Show(this, "监听进程启动后立即退出：\r\n\r\n" + tail, "监听启动失败");
                    relay = null;
                    statusLabel.Text = "监听启动失败";
                    UpdateListenStatus();
                    return;
                }
                statusLabel.Text = "监听已启动";
                UpdateListenStatus();
            }
            catch (Exception ex)
            {
                relay = null;
                MessageBox.Show(this, "启动监听失败：\r\n" + ex.Message, "错误");
            }
        }

        private void StopListening()
        {
            if (relay == null || relay.HasExited)
            {
                relay = null;
                capturePath = null;
                UpdateListenStatus();
                statusLabel.Text = "监听未运行";
                return;
            }
            try
            {
                relay.Kill();
                relay.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "停止监听失败：\r\n" + ex.Message, "错误");
            }
            relay = null;
            statusLabel.Text = "监听已停止（VRChat 会重新直连）";
            UpdateListenStatus();
        }

        private static void AppendLog(string path, string line)
        {
            try
            {
                using (var sw = new StreamWriter(path, true, Encoding.UTF8))
                    sw.WriteLine(DateTime.Now.ToString("HH:mm:ss ") + line);
            }
            catch { }
        }

        private void UpdateListenStatus()
        {
            bool running = relay != null && !relay.HasExited;
            btnListen.Enabled = !running;
            btnStopListen.Enabled = running;
            if (running)
            {
                long len = 0;
                try { if (capturePath != null && File.Exists(capturePath)) len = new FileInfo(capturePath).Length; } catch { }
                listenStatus.Text = string.Format(
                    "监听中 pid={0}  抓包={1} ({2:F1} MB)  密钥={3}  分轨={4}",
                    relay.Id, Path.GetFileName(capturePath ?? ""), len / 1048576.0,
                    sessionKey == null ? "等待" : sessionKey.Substring(0, 8) + "…",
                    live != null && !live.HasExited ? "运行中" : "未运行");
                listenStatus.ForeColor = Color.DarkGreen;
            }
            else
            {
                var others = Process.GetProcessesByName(RelayProcessName());
                listenStatus.Text = others.Length > 0 ? "检测到外部中继进程（不由本程序管理）" : "监听未运行";
                listenStatus.ForeColor = others.Length > 0 ? Color.DarkOrange : Color.DimGray;
            }
        }

        private void RestartElevated()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    UseShellExecute = true,
                    Verb = "runas"
                });
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "以管理员重启失败：\r\n" + ex.Message, "错误");
            }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (relay != null && !relay.HasExited)
            {
                var r = MessageBox.Show(this,
                    "网络监听仍在运行，VRChat 的连接依赖它。\r\n\r\n是：停止监听并退出\r\n否：保持监听并退出\r\n取消：返回",
                    "退出确认", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                if (r == DialogResult.Yes) { StopLive(); StopListening(); }
            }
            else
            {
                StopLive();
            }
        }

        private void OnTick()
        {
            UpdateListenStatus();
            if (opened)
            {
                var pos = Mci.Status(Alias, "position");
                var len = Mci.Status(Alias, "length");
                if (len > 0)
                {
                    seek.Value = (int)Math.Min(1000, pos * 1000 / len);
                    timeLabel.Text = FormatTime(pos / 1000.0) + " / " + FormatTime(len / 1000.0);
                    wave.Progress = (float)pos / len;
                    wave.Invalidate();
                }
            }
        }

        // ================= tracks =================

        private void ExtractNamesAsync()
        {
            if (liveOutDir == null || sessionKey == null || capturePath == null) return;
            if (extractingNames) return;
            extractingNames = true;
            lastNamesAt = DateTime.Now;
            var cap = capturePath;
            var key = sessionKey;
            var ini = Path.Combine(liveOutDir, "names.json");
            var py = FindPython();
            Task.Factory.StartNew(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = py,
                        Arguments = string.Format("-u -m voxtrace names --capture \"{0}\" --key {1} --out \"{2}\"",
                            cap, key, ini),
                        WorkingDirectory = workspace,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        p.StandardOutput.ReadToEnd();
                        p.StandardError.ReadToEnd();
                        p.WaitForExit(120000);
                    }
                }
                catch { }
                Invoke((Action)(() =>
                {
                    extractingNames = false;
                    ApplyAutoNames(ini);
                }));
            });
        }

        private void ApplyAutoNames(string jsonPath)
        {
            if (!File.Exists(jsonPath) || tracks.Count == 0) return;
            string txt;
            try { txt = File.ReadAllText(jsonPath, Encoding.UTF8); } catch { return; }
            var re = new Regex("\"(\\d+)\"\\s*:\\s*\\{\\s*\"usr\"\\s*:\\s*\"([^\"]*)\"\\s*,\\s*\"name\"\\s*:\\s*\"([^\"]*)\"");
            var map = new Dictionary<int, string>();
            foreach (Match m in re.Matches(txt))
            {
                int actor;
                if (int.TryParse(m.Groups[1].Value, out actor) && m.Groups[3].Value.Length > 0)
                    map[actor] = m.Groups[3].Value;
            }
            bool changed = false;
            int applied = 0;
            foreach (var t in tracks)
            {
                if (manualActors.Contains(t.Actor)) continue;
                string name;
                if (map.TryGetValue(t.Actor, out name) && t.Name != name)
                {
                    t.Name = name;
                    changed = true;
                    applied++;
                }
            }
            if (changed) FillGrid();
            PipelineLog(string.Format("自动名字映射：{0} 条（应用 {1}）", map.Count, applied));
            if (applied > 0) statusLabel.Text = string.Format("自动映射 {0} 个玩家名", map.Count);
        }

        private void RefreshTracksIfNeeded()
        {
            if (liveOutDir == null) return;
            var stats = Path.Combine(liveOutDir, "live_stats.json");
            if (!File.Exists(stats)) return;
            string txt;
            try { txt = File.ReadAllText(stats, Encoding.UTF8); } catch { return; }
            bool needNames = false;
            var re = new Regex("\\{\\s*\"key\"\\s*:\\s*\"([^\"]*)\"[\\s\\S]*?\"actor\"\\s*:\\s*(-?\\d+)[\\s\\S]*?\"name\"\\s*:\\s*\"([^\"]*)\"[\\s\\S]*?\"file\"\\s*:\\s*\"([^\"]*)\"[\\s\\S]*?\"seconds\"\\s*:\\s*([0-9.]+)[\\s\\S]*?\"speaking\"\\s*:\\s*(true|false)");
            bool added = false;
            int selActor = currentIndex >= 0 && currentIndex < tracks.Count ? tracks[currentIndex].Actor : -1;
            foreach (Match m in re.Matches(txt))
            {
                string key = m.Groups[1].Value;
                int actor;
                if (!int.TryParse(m.Groups[2].Value, out actor)) continue;
                string name = m.Groups[3].Value;
                string file = m.Groups[4].Value;
                double sec;
                double.TryParse(m.Groups[5].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out sec);
                bool speaking = m.Groups[6].Value == "true";
                var t = tracks.Find(x => x.Key == key);
                if (t == null)
                {
                    t = new TrackInfo
                    {
                        Key = key,
                        Actor = actor,
                        Path = Path.Combine(liveOutDir, file),
                        Seconds = sec,
                        Speaking = speaking
                    };
                    tracks.Add(t);
                    added = true;
                    needNames = true;
                }
                else
                {
                    t.Actor = actor;
                    t.Path = Path.Combine(liveOutDir, file);
                    t.Seconds = sec;
                    t.Speaking = speaking;
                }
                if (!manualActors.Contains(actor) && name.Length > 0) t.Name = name;
            }
            if (tracks.Count == 0) return;
            tracks.Sort((a, b) => a.Actor.CompareTo(b.Actor));
            if (needNames && !extractingNames) ExtractNamesAsync();
            if (added)
            {
                FillGrid();
                if (selActor >= 0)
                    for (int i = 0; i < tracks.Count; i++)
                        if (tracks[i].Actor == selActor) { grid.Rows[i].Selected = true; break; }
            }
            else
            {
                UpdateRowValues();
            }
        }

        private void UpdateRowValues()
        {
            for (int i = 0; i < tracks.Count && i < grid.Rows.Count; i++)
            {
                var t = tracks[i];
                grid.Rows[i].Cells["dur"].Value = FormatTime(t.Seconds > 0 ? t.Seconds : t.Duration);
                grid.Rows[i].DefaultCellStyle.BackColor = t.Speaking ? Color.FromArgb(220, 255, 220) : Color.White;
            }
        }

        private void ExportTracks()
        {
            if (liveOutDir == null || !Directory.Exists(liveOutDir))
            {
                statusLabel.Text = "还没有可导出的音轨";
                return;
            }
            Directory.CreateDirectory(capturesDir);
            var zip = Path.Combine(capturesDir, "export_" + DateTime.Now.ToString("HHmmss") + ".zip");
            try
            {
                if (File.Exists(zip)) File.Delete(zip);
                ZipFile.CreateFromDirectory(liveOutDir, zip);
                statusLabel.Text = "已导出：" + zip;
                PipelineLog("导出音轨 " + zip);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "导出失败：\r\n" + ex.Message, "错误");
            }
        }

        private void LoadFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择包含 player_<actor>.wav 的会话文件夹";
                if (dlg.ShowDialog() != DialogResult.OK) return;
                folder = dlg.SelectedPath;
            }
            autoMode = false;
            StopLive();
            LoadTracks();
        }

        private void LoadTracks()
        {
            tracks.Clear();
            importedNames.Clear();
            if (folder == null) return;
            foreach (var f in Directory.GetFiles(folder, "player_*.wav"))
            {
                var m = Regex.Match(Path.GetFileNameWithoutExtension(f), @"player_(.+?)(?:_s\d+)?$");
                if (!m.Success) continue;
                TrackInfo t;
                string err;
                if (!Wav.Load(f, out t, out err)) continue;
                int actor;
                if (!int.TryParse(m.Groups[1].Value, out actor)) actor = 0;
                t.Actor = actor;
                t.Key = m.Groups[1].Value;
                tracks.Add(t);
            }
            tracks.Sort((a, b) => a.Actor.CompareTo(b.Actor));
            LoadNames();
            FillGrid();
            statusLabel.Text = string.Format("已加载 {0} 条音轨，来自 {1}", tracks.Count, folder);
        }

        private void FillGrid()
        {
            grid.Rows.Clear();
            foreach (var t in tracks)
            {
                int idx = grid.Rows.Add(t.Actor, t.Name,
                    FormatTime(t.Seconds > 0 ? t.Seconds : t.Duration), ((int)t.Rms).ToString());
                grid.Rows[idx].DefaultCellStyle.BackColor = t.Speaking ? Color.FromArgb(220, 255, 220) : Color.White;
            }
        }

        private void AssignName(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= tracks.Count) return;
            var t = tracks[rowIndex];
            using (var dlg = new NameDialog(importedNames, t.Name, t.Actor))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    t.Name = dlg.Value;
                    manualActors.Add(t.Actor);
                    grid.Rows[rowIndex].Cells["name"].Value = t.Name;
                    if (folder != null) SaveNamesSilent();
                }
            }
        }

        private void ImportNames()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "VRChat 日志 (output_log*.txt)|output_log*.txt|所有文件 (*.*)|*.*";
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    @"AppData\LocalLow\VRChat\VRChat");
                if (Directory.Exists(dir)) dlg.InitialDirectory = dir;
                if (dlg.ShowDialog() != DialogResult.OK) return;
                var names = new List<string>();
                foreach (var line in File.ReadAllLines(dlg.FileName, Encoding.UTF8))
                {
                    var m = Regex.Match(line, @"OnPlayerJoined (.+?) \(usr_[0-9a-f-]+\)");
                    if (m.Success) names.Add(m.Groups[1].Value);
                }
                importedNames = names;
                statusLabel.Text = string.Format("从日志导入 {0} 个玩家名；双击左侧行进行绑定", names.Count);
            }
        }

        private void LoadNames()
        {
            if (folder == null) return;
            var path = Path.Combine(folder, "names.ini");
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                int actor;
                if (!int.TryParse(line.Substring(0, i), out actor)) continue;
                var t = tracks.Find(x => x.Actor == actor);
                if (t != null)
                {
                    t.Name = line.Substring(i + 1);
                    manualActors.Add(actor);
                }
            }
        }

        private void SaveNames()
        {
            if (folder == null) return;
            SaveNamesSilent();
            statusLabel.Text = "绑定已保存到 " + Path.Combine(folder, "names.ini");
        }

        private void SaveNamesSilent()
        {
            if (folder == null) return;
            var lines = new List<string>();
            foreach (var t in tracks)
                if (!string.IsNullOrEmpty(t.Name)) lines.Add(t.Actor + "=" + t.Name);
            File.WriteAllLines(Path.Combine(folder, "names.ini"), lines.ToArray(), Encoding.UTF8);
        }

        private void SelectTrack(int index)
        {
            if (index < 0 || index >= tracks.Count) return;
            StopPlayback();
            currentIndex = index;
            var t = tracks[index];
            if (t.Peaks.Length == 0 && !string.IsNullOrEmpty(t.Path) && File.Exists(t.Path))
            {
                TrackInfo loaded;
                string err;
                if (Wav.Load(t.Path, out loaded, out err))
                {
                    t.Peaks = loaded.Peaks;
                    t.Duration = loaded.Duration;
                    t.Rms = loaded.Rms;
                }
            }
            wave.SetTrack(t);
            seek.Value = 0;
            timeLabel.Text = "00:00 / " + FormatTime(tracks[index].Duration);
        }

        private string playPath;

        private void Play()
        {
            if (currentIndex < 0) return;
            if (!opened)
            {
                var track = tracks[currentIndex];
                if (string.IsNullOrEmpty(track.Path) || !File.Exists(track.Path))
                {
                    statusLabel.Text = "该玩家还没有音频文件（可能尚未说话）";
                    return;
                }
                playPath = track.Path;
                var err = Mci.Send(string.Format("open \"{0}\" type waveaudio alias {1}", playPath, Alias));
                if (err != 0)
                {
                    var text = Mci.ErrorText(err);
                    try
                    {
                        var tmp = Path.Combine(Path.GetTempPath(), "voxtrace_play.wav");
                        File.Copy(track.Path, tmp, true);
                        var err2 = Mci.Send(string.Format("open \"{0}\" type waveaudio alias {1}", tmp, Alias));
                        if (err2 == 0)
                        {
                            playPath = tmp;
                            err = 0;
                        }
                        else
                        {
                            text += " / 复制后仍失败：" + Mci.ErrorText(err2);
                        }
                    }
                    catch (Exception ex)
                    {
                        text += " / 复制失败：" + ex.Message;
                    }
                    if (err != 0)
                    {
                        statusLabel.Text = "无法打开音频：" + text;
                        return;
                    }
                }
                opened = true;
                Mci.Send(string.Format("setaudio {0} volume to {1}", Alias, volume.Value));
            }
            Mci.Send("play " + Alias);
        }

        private void StopPlayback()
        {
            if (opened)
            {
                Mci.Send("stop " + Alias);
                Mci.Send("close " + Alias);
                opened = false;
            }
            seek.Value = 0;
        }

        private static string FormatTime(double sec)
        {
            var s = (int)sec;
            return string.Format("{0:00}:{1:00}", s / 60, s % 60);
        }
    }

    public class WavePanel : Panel
    {
        private TrackInfo track;
        public float Progress;

        public WavePanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(24, 26, 30);
        }

        public void SetTrack(TrackInfo t) { track = t; Progress = 0; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(BackColor);
            if (track == null || track.Peaks.Length == 0)
            {
                using (var f = new Font("Microsoft YaHei", 11))
                using (var b = new SolidBrush(Color.Gray))
                    g.DrawString("选择左侧音轨", f, b, 20, 20);
                return;
            }
            int w = Width - 20, h = Height - 20;
            float mid = 10 + h / 2f;
            using (var pen = new Pen(Color.FromArgb(0, 200, 255), 1))
            {
                for (int i = 0; i < track.Peaks.Length; i++)
                {
                    float x = 10 + (float)i * w / track.Peaks.Length;
                    float amp = track.Peaks[i] * (h / 2f);
                    g.DrawLine(pen, x, mid - amp, x, mid + amp);
                }
            }
            if (Progress > 0)
            {
                float x = 10 + Progress * w;
                using (var pen = new Pen(Color.Orange, 2))
                    g.DrawLine(pen, x, 8, x, Height - 8);
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VoxTrace_error.txt"), ex.ToString());
                }
                catch { }
                MessageBox.Show(ex.ToString(), "VoxTrace 启动失败");
            }
        }
    }
}
