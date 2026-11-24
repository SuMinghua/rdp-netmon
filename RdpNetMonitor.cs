// ============================================================================
// RDP 网络监视器 - 界面层 (WinForms)
// 特性: 普通窗口(默认不置顶)、实时图表、详细指标、运行日志面板、托盘、
//       可选置顶/鼠标穿透、一键复制诊断信息、CSV 实时落盘
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RdpNetMon
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length >= 2 && string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                int secs;
                if (!int.TryParse(args[1], out secs) || secs < 3) secs = 8;
                if (secs > 120) secs = 120;
                RunSelfTest(secs);
                return;
            }
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\RdpNetMonitor.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("RDP 网络监视器已经在运行。", "RDP 网络监视器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { Application.Run(new MainForm()); }
                catch (Exception ex)
                {
                    MessageBox.Show("程序发生异常: " + ex, "RDP 网络监视器", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ---------------- 自检模式 ----------------
        private static void RunSelfTest(int seconds)
        {
            Monitor mon = null;
            try
            {
                mon = new Monitor(AppDomain.CurrentDomain.BaseDirectory);
                var samples = new List<Sample>();
                var events = new List<string>();
                mon.OnSample += s => samples.Add(s);
                mon.OnEvent += (lv, msg) => events.Add(lv + "|" + msg);
                mon.Start();
                Thread.Sleep(seconds * 1000 + 800);
                mon.Stop();

                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"ok\": true,");
                sb.AppendLine("  \"seconds\": " + seconds + ",");
                sb.AppendLine("  \"samples\": " + samples.Count + ",");
                sb.AppendLine("  \"events_count\": " + events.Count + ",");
                sb.AppendLine("  \"csv\": " + Json(mon.Log.CsvPath) + ",");
                sb.AppendLine("  \"event_log\": " + Json(mon.Log.EventPath) + ",");
                sb.AppendLine("  \"samples_data\": [");
                for (int i = 0; i < samples.Count; i++)
                {
                    sb.Append("    " + SampleJson(samples[i]));
                    sb.AppendLine(i == samples.Count - 1 ? "" : ",");
                }
                sb.AppendLine("  ],");
                sb.AppendLine("  \"events\": [");
                for (int i = 0; i < events.Count; i++)
                {
                    sb.Append("    " + Json(events[i]));
                    sb.AppendLine(i == events.Count - 1 ? "" : ",");
                }
                sb.AppendLine("  ]");
                sb.AppendLine("}");
                File.WriteAllText(Path.Combine(mon.Log.LogDir, "selftest.json"), sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "selftest_error.txt"),
                        ex.ToString(), new UTF8Encoding(false));
                }
                catch { }
            }
            finally
            {
                if (mon != null) mon.Dispose();
            }
        }

        private static string SampleJson(Sample s)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{{\"t\":\"{0}\",\"mode\":\"{1}\",\"conn\":{2},\"remote\":{3},\"udpNeg\":{4},\"udpAct\":{5},\"tcpRtt\":{6},\"udpRtt\":{7},\"icmpRtt\":{8},\"effRtt\":{9},\"stale\":{10},\"rxBps\":{11},\"txBps\":{12},\"rxTot\":{13},\"txTot\":{14},\"tcpBw\":{15},\"udpBw\":{16},\"loss\":{17},\"retran\":{18},\"fec\":{19},\"udpShare\":{20},\"estats\":{21},\"eSampleRtt\":{22},\"eRetrans\":{23},\"eTimeout\":{24},\"eCong\":{25},\"session\":{26},\"rfxInst\":{27},\"client\":{28},\"clientBuild\":{29},\"note\":{30}}}",
                s.Time.ToString("HH:mm:ss"),
                s.Mode.ToString(),
                s.Connected ? "true" : "false",
                Json(s.RemoteIp),
                s.UdpNegotiated ? "true" : "false",
                s.UdpActive ? "true" : "false",
                R(s.TcpRttMs), R(s.UdpRttMs), s.IcmpRttMs >= 0 ? s.IcmpRttMs.ToString(CultureInfo.InvariantCulture) : "null",
                R(s.EffTcpRttMs), s.RfxRttStale ? "true" : "false",
                R(s.RxBps), R(s.TxBps), R(s.RxTotalBytes), R(s.TxTotalBytes),
                R(s.TcpBwEst), R(s.UdpBwEst), R(s.LossRatePct), R(s.RetranRatePct), R(s.FecRate), R(s.UdpSharePct),
                s.EstatsOk ? "true" : "false",
                s.EstatsOk ? s.EstatsSampleRttMs.ToString(CultureInfo.InvariantCulture) : "null",
                s.EstatsOk ? s.EstatsRetrans.ToString(CultureInfo.InvariantCulture) : "null",
                s.EstatsOk ? s.EstatsTimeouts.ToString(CultureInfo.InvariantCulture) : "null",
                s.EstatsOk ? s.EstatsCongSignals.ToString(CultureInfo.InvariantCulture) : "null",
                Json(s.SessionName), Json(s.RfxInstance), Json(s.ClientName),
                s.ClientBuild > 0 ? s.ClientBuild.ToString(CultureInfo.InvariantCulture) : "null",
                Json(s.Note));
        }

        private static string R(double v) { return double.IsNaN(v) || v < 0 ? "null" : v.ToString("0.##", CultureInfo.InvariantCulture); }

        private static string Json(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ") + "\"";
        }
    }

    // ---------------- 格式化辅助 ----------------
    public static class Fmt
    {
        public static string Bps(double v)
        {
            if (double.IsNaN(v) || v < 0) return "—";
            if (v >= 1000000) return (v / 1000000.0).ToString("0.00") + " Mbps";
            if (v >= 1000) return (v / 1000.0).ToString("0.0") + " KB/s";
            return v.ToString("0") + " B/s";
        }

        public static string Bytes(double v)
        {
            if (double.IsNaN(v) || v < 0) return "—";
            if (v >= 1073741824) return (v / 1073741824.0).ToString("0.00") + " GB";
            if (v >= 1048576) return (v / 1048576.0).ToString("0.00") + " MB";
            if (v >= 1024) return (v / 1024.0).ToString("0.0") + " KB";
            return v.ToString("0") + " B";
        }

        public static string Ms(double v)
        {
            if (double.IsNaN(v) || v < 0) return "—";
            return v.ToString("0.#") + " ms";
        }

        public static string Pct(double v)
        {
            if (double.IsNaN(v) || v < 0) return "—";
            return v.ToString("0.##") + "%";
        }

        public static string Mbps(double v)  // v 为 bits/s
        {
            if (double.IsNaN(v) || v < 0) return "—";
            return (v / 1000000.0).ToString("0.00") + " Mbps";
        }

        public static string Axis(double v)
        {
            if (v >= 1000000) return (v / 1000000.0).ToString("0.0") + "M";
            if (v >= 1000) return (v / 1000.0).ToString("0.0") + "k";
            return v.ToString("0");
        }

        public static string LocalOsString()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k == null) return Environment.OSVersion.VersionString;
                    string name = k.GetValue("ProductName", "Windows") as string;
                    string build = k.GetValue("CurrentBuildNumber", "?") as string;
                    string ubr = k.GetValue("UBR", "0") as string;
                    return (name ?? "Windows") + " x" + (Environment.Is64BitOperatingSystem ? "64" : "86") + " (10.0, 内部版本 " + build + "." + ubr + ")";
                }
            }
            catch { return Environment.OSVersion.VersionString; }
        }
    }

    // ---------------- 折线图控件 ----------------
    public sealed class SeriesChart : Control
    {
        private sealed class Series
        {
            public string Name;
            public Color Color;
            public List<double> Vals = new List<double>();
            public double Max;
        }

        private readonly List<Series> _series = new List<Series>();
        public string Title = "";
        public Func<double, string> Formatter = v => v.ToString("0.##");
        public int MaxPoints = 180;

        public SeriesChart()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(30, 30, 34);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void AddSeries(string name, Color color)
        {
            _series.Add(new Series { Name = name, Color = color });
        }

        public void Add(int idx, double v)
        {
            if (idx < 0 || idx >= _series.Count || double.IsNaN(v) || v < 0) return;
            Series s = _series[idx];
            s.Vals.Add(v);
            if (v > s.Max) s.Max = v;
            if (s.Vals.Count > MaxPoints)
            {
                s.Vals.RemoveAt(0);
                s.Max = 0;
                foreach (double x in s.Vals) if (x > s.Max) s.Max = x;
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            Rectangle r = ClientRectangle;
            if (r.Width < 80 || r.Height < 40) return;
            int padL = 10, padR = 62, padT = 46, padB = 14;
            Rectangle plot = new Rectangle(r.X + padL, r.Y + padT, r.Width - padL - padR, r.Height - padT - padB);
            if (plot.Width < 20 || plot.Height < 20) return;

            double max = 1;
            foreach (Series s in _series) if (s.Max > max) max = s.Max;
            max *= 1.12;

            using (Pen gridPen = new Pen(Color.FromArgb(55, 55, 62)))
            using (Font f = new Font("Microsoft YaHei UI", 8f))
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(140, 140, 150)))
            {
                for (int i = 0; i <= 4; i++)
                {
                    int y = plot.Bottom - (int)(plot.Height * i / 4.0);
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    g.DrawString(Formatter(max * i / 4.0), f, dim, plot.Right + 4, y - 7);
                }
                g.DrawString(Title, f, dim, plot.Left, 3);
            }

            // 标题独占第一行; 图例按实际文字宽度排布, 放不下自动换到第二行
            int legendRowY = 15;
            int legendX = plot.Left;
            using (Font f = new Font("Microsoft YaHei UI", 8f))
            {
                foreach (Series s in _series)
                {
                    int cnt = s.Vals.Count;
                    if (cnt >= 2)
                    {
                        using (Pen pen = new Pen(s.Color, 1.6f))
                        {
                            for (int i = 1; i < cnt; i++)
                            {
                                float x1 = plot.Left + plot.Width * (float)(i - 1) / Math.Max(1, cnt - 1);
                                float x2 = plot.Left + plot.Width * (float)i / Math.Max(1, cnt - 1);
                                float y1 = plot.Bottom - (float)(plot.Height * (s.Vals[i - 1] / max));
                                float y2 = plot.Bottom - (float)(plot.Height * (s.Vals[i] / max));
                                g.DrawLine(pen, x1, y1, x2, y2);
                            }
                        }
                    }
                    string legend = s.Name + (cnt > 0 ? " " + Formatter(s.Vals[cnt - 1]) : "");
                    SizeF sz = g.MeasureString(legend, f);
                    int itemW = (int)Math.Ceiling(sz.Width) + 18;
                    if (legendX + itemW > plot.Right && legendX > plot.Left)
                    {
                        legendX = plot.Left;
                        legendRowY += 14;
                    }
                    using (SolidBrush b = new SolidBrush(s.Color))
                    {
                        g.FillRectangle(b, legendX + 2, legendRowY + 4, 10, 3);
                        g.DrawString(legend, f, b, legendX + 16, legendRowY);
                    }
                    legendX += itemW + 12;
                }
            }
        }
    }

    // ---------------- 日志级别 ----------------
    public enum Lv { Info, Warn, Err, Conn }

    public sealed class LogItem
    {
        public DateTime Time;
        public Lv Level;
        public string Text;
        public override string ToString() { return Time.ToString("HH:mm:ss") + " " + Text; }
    }

    // ---------------- 主窗体 ----------------
    public sealed class MainForm : Form
    {
        private Monitor _mon;
        private SeriesChart _chRt, _chRtt;
        private ListView _lv;
        private SplitContainer _split;
        private ListBox _lstLog;
        private ComboBox _cmbLevel;
        private CheckBox _chkAuto;
        private Label _lblMode, _lblRemote, _lblProto, _lblRtt, _lblRx, _lblTx;
        private NotifyIcon _tray;
        private IntPtr _trayHicon = IntPtr.Zero;
        private ToolStripMenuItem _miThrough;
        private CheckBox _chkThrough;
        private System.Windows.Forms.Timer _timer;
        private DateTime _lastSampleTime = DateTime.MinValue;
        private bool _allowExit;
        private bool _balloonShown;
        private readonly List<LogItem> _logs = new List<LogItem>();
        private readonly List<string[]> _rows = new List<string[]>();
        private readonly Queue<string[]> _pendingEvents = new Queue<string[]>();
        private readonly object _evtLock = new object();
        private const int LogCap = 3000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_TRANSPARENT = 0x20;

        private static readonly Color CBg = Color.FromArgb(27, 27, 31);
        private static readonly Color CPanel = Color.FromArgb(35, 35, 40);
        private static readonly Color CText = Color.FromArgb(220, 220, 224);
        private static readonly Color CDim = Color.FromArgb(140, 140, 150);
        private static readonly Color CGreen = Color.FromArgb(96, 200, 130);
        private static readonly Color CBlue = Color.FromArgb(92, 150, 255);
        private static readonly Color COrange = Color.FromArgb(230, 170, 80);
        private static readonly Color CRed = Color.FromArgb(235, 95, 95);

        public MainForm()
        {
            Text = "RDP 网络监视器";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1020, 680);
            MinimumSize = new Size(820, 560);
            BackColor = CBg;
            ForeColor = CText;
            Font = new Font("Microsoft YaHei UI", 9f);
            BuildUi();
            BuildTray();
            Shown += OnFirstShown;

            _mon = new Monitor(AppDomain.CurrentDomain.BaseDirectory);
            _mon.OnEvent += OnMonEvent;

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += OnTick;
            _mon.Start();
            _timer.Start();
            AppendLog(Lv.Info, "界面就绪, 正在监视 RDP 连接... (日志目录: " + _mon.Log.LogDir + ")");
            AppendLog(Lv.Info, "日志面板可选中后用右键或 Ctrl+C 复制, Ctrl+A 全选");
        }

        private void OnFirstShown(object sender, EventArgs e)
        {
            try
            {
                _split.Panel1MinSize = 320;
                _split.Panel2MinSize = 340;
                int desired = 640;
                int max = _split.Width - _split.Panel2MinSize - _split.SplitterWidth - 2;
                _split.SplitterDistance = Math.Max(_split.Panel1MinSize, Math.Min(desired, max));
            }
            catch { }
        }

        // ---------------- UI 构建 ----------------
        private void BuildUi()
        {
            // 顶部状态栏: 第一行按钮组(右对齐独占一行), 第二行状态标签(自适应宽度, 互不遮挡)
            Panel top = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = CPanel };
            FlowLayoutPanel fLabels = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = CPanel,
                Padding = new Padding(10, 6, 4, 0),
                Margin = new Padding(0)
            };
            _lblMode = MakeTopLabel("模式: 监视中", CDim);
            _lblRemote = MakeTopLabel("远程: —", CDim);
            _lblProto = MakeTopLabel("协议: —", CDim);
            _lblRtt = MakeTopLabel("RTT: —", CDim);
            _lblRx = MakeTopLabel("↓ —", CGreen);
            _lblTx = MakeTopLabel("↑ —", CBlue);
            fLabels.Controls.Add(_lblMode);
            fLabels.Controls.Add(_lblRemote);
            fLabels.Controls.Add(_lblProto);
            fLabels.Controls.Add(_lblRtt);
            fLabels.Controls.Add(_lblRx);
            fLabels.Controls.Add(_lblTx);

            FlowLayoutPanel fBtns = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 34,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = CPanel,
                Padding = new Padding(4, 7, 10, 0)
            };
            CheckBox chkTop = MakeCheck("置顶");
            _chkThrough = MakeCheck("鼠标穿透");
            Button btnCopy = MakeButton("复制诊断");
            Button btnHide = MakeButton("隐藏到托盘");
            Button btnExit = MakeButton("退出");
            chkTop.CheckedChanged += (o, e) => { TopMost = chkTop.Checked; };
            _chkThrough.CheckedChanged += (o, e) => ToggleClickThrough(_chkThrough.Checked);
            btnCopy.Click += (o, e) => CopyDiagnostic();
            btnHide.Click += (o, e) => HideToTray();
            btnExit.Click += (o, e) => { _allowExit = true; Close(); };
            fBtns.Controls.Add(btnExit);
            fBtns.Controls.Add(btnHide);
            fBtns.Controls.Add(btnCopy);
            fBtns.Controls.Add(_chkThrough);
            fBtns.Controls.Add(chkTop);
            top.Controls.Add(fLabels);  // Fill 先添加
            top.Controls.Add(fBtns);    // Top 后添加, 先停靠

            // 底部日志面板
            Panel logPanel = new Panel { Dock = DockStyle.Bottom, Height = 180, BackColor = Color.FromArgb(31, 31, 35) };
            Panel logTool = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.FromArgb(38, 38, 44) };
            Label lblLog = new Label
            {
                Text = "运行日志",
                Dock = DockStyle.Left,
                AutoSize = true,
                ForeColor = CDim,
                Padding = new Padding(8, 6, 4, 0)
            };
            _cmbLevel = new ComboBox
            {
                Dock = DockStyle.Left,
                Width = 110,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(50, 50, 58),
                ForeColor = CText,
                FlatStyle = FlatStyle.Flat
            };
            _cmbLevel.Items.AddRange(new object[] { "全部", "信息", "警告", "错误", "连接事件" });
            _cmbLevel.SelectedIndex = 0;
            _cmbLevel.SelectedIndexChanged += (o, e) => RebuildLogView();
            _chkAuto = new CheckBox
            {
                Text = "自动滚动",
                Dock = DockStyle.Left,
                AutoSize = true,
                Checked = true,
                ForeColor = CDim,
                Padding = new Padding(8, 6, 0, 0)
            };
            Button btnExport = MakeButton("导出日志");
            Button btnOpenDir = MakeButton("打开日志目录");
            btnExport.Dock = DockStyle.Right;
            btnExport.Margin = new Padding(0, 3, 8, 0);
            btnOpenDir.Dock = DockStyle.Right;
            btnOpenDir.Margin = new Padding(0, 3, 4, 0);
            btnExport.Click += (o, e) => ExportLog();
            btnOpenDir.Click += (o, e) =>
            {
                try { Process.Start("explorer.exe", "\"" + _mon.Log.LogDir + "\""); }
                catch { }
            };
            logTool.Controls.Add(btnOpenDir);
            logTool.Controls.Add(btnExport);
            logTool.Controls.Add(_chkAuto);
            logTool.Controls.Add(_cmbLevel);
            logTool.Controls.Add(lblLog);

            _lstLog = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(24, 24, 28),
                ForeColor = CText,
                BorderStyle = BorderStyle.None,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 18,
                IntegralHeight = false
            };
            _lstLog.DrawItem += OnDrawLogItem;
            _lstLog.SelectionMode = SelectionMode.MultiExtended;
            ContextMenuStrip logMenu = new ContextMenuStrip();
            logMenu.Items.Add("复制选中日志", null, (o, e) => CopyLogs(false));
            logMenu.Items.Add("复制全部日志", null, (o, e) => CopyLogs(true));
            _lstLog.ContextMenuStrip = logMenu;
            _lstLog.KeyDown += OnLogKeyDown;
            logPanel.Controls.Add(_lstLog);
            logPanel.Controls.Add(logTool);

            // 中部: 图表 + 指标 (MinSize/SplitterDistance 在窗体 Shown 后按实际宽度设置, 避免初始化期越界)
            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.Panel2,
                BackColor = CBg,
                SplitterWidth = 4
            };
            TableLayoutPanel charts = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(6, 6, 0, 0)
            };
            charts.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            charts.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            _chRt = new SeriesChart
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 4, 4),
                Title = "吞吐量",
                Formatter = Fmt.Bps
            };
            _chRt.AddSeries("↓ 接收", CGreen);
            _chRt.AddSeries("↑ 发送", CBlue);
            _chRtt = new SeriesChart
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 4, 0),
                Title = "往返时间",
                Formatter = v => v.ToString("0.#") + " ms"
            };
            _chRtt.AddSeries("协议 RTT", CBlue);
            _chRtt.AddSeries("ICMP RTT", COrange);
            charts.Controls.Add(_chRt, 0, 0);
            charts.Controls.Add(_chRtt, 0, 1);
            _split.Panel1.Controls.Add(charts);

            _lv = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.None,
                BackColor = Color.FromArgb(33, 33, 38),
                ForeColor = CText,
                BorderStyle = BorderStyle.None,
                MultiSelect = false,
                HideSelection = false
            };
            _lv.Columns.Add("指标", 170);
            _lv.Columns.Add("数值", 300);
            _lv.Resize += OnLvResize;
            _split.Panel2.Controls.Add(_lv);

            Controls.Add(_split);
            Controls.Add(logPanel);
            Controls.Add(top);
        }

        private Label MakeTopLabel(string text, Color c)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = c,
                Margin = new Padding(0, 4, 8, 0),
                BackColor = Color.Transparent
            };
        }

        private CheckBox MakeCheck(string text)
        {
            return new CheckBox
            {
                Text = text,
                AutoSize = true,
                ForeColor = CText,
                Margin = new Padding(4, 5, 4, 0)
            };
        }

        private Button MakeButton(string text)
        {
            return new Button
            {
                Text = text,
                AutoSize = true,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 55, 62),
                ForeColor = CText,
                Margin = new Padding(4, 2, 4, 0)
            };
        }

        private void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = MakeTrayIcon();
            _tray.Text = "RDP 网络监视器";
            _tray.Visible = true;
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, (o, e) => ShowMain());
            _miThrough = new ToolStripMenuItem("取消鼠标穿透", null, (o, e) =>
            {
                if (_chkThrough != null) _chkThrough.Checked = false;
            });
            _miThrough.Visible = false;
            menu.Items.Add(_miThrough);
            menu.Items.Add("复制诊断", null, (o, e) => CopyDiagnostic());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (o, e) => { _allowExit = true; Close(); });
            _tray.ContextMenuStrip = menu;
            _tray.MouseDoubleClick += (o, e) => ShowMain();
        }

        private Icon MakeTrayIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(40, 120, 230)))
                        g.FillEllipse(b, 1, 1, 30, 30);
                    using (Pen pen = new Pen(Color.White, 3f))
                        g.DrawLines(pen, new Point[] { new Point(7, 21), new Point(13, 13), new Point(19, 19), new Point(25, 9) });
                }
                _trayHicon = bmp.GetHicon();
                return Icon.FromHandle(_trayHicon);
            }
        }

        private void ToggleClickThrough(bool on)
        {
            try
            {
                int style = GetWindowLong(Handle, GWL_EXSTYLE);
                if (on)
                {
                    Opacity = 0.88;
                    style |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
                    if (_miThrough != null) _miThrough.Visible = true;
                    AppendLog(Lv.Warn, "已开启鼠标穿透, 窗口点击将穿透到下层; 可通过托盘图标菜单\"取消鼠标穿透\"恢复");
                }
                else
                {
                    Opacity = 1.0;
                    style &= ~WS_EX_TRANSPARENT;
                    style &= ~WS_EX_LAYERED;
                    if (_miThrough != null) _miThrough.Visible = false;
                }
                SetWindowLong(Handle, GWL_EXSTYLE, style);
            }
            catch { }
        }

        private void ShowMain()
        {
            Show();
            WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            Activate();
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            if (!_balloonShown)
            {
                _balloonShown = true;
                _tray.ShowBalloonTip(2500, "RDP 网络监视器", "已最小化到托盘, 双击图标恢复窗口", ToolTipIcon.Info);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowExit)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _timer.Stop(); } catch { }
            try { _mon.OnEvent -= OnMonEvent; _mon.Dispose(); } catch { }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            if (_trayHicon != IntPtr.Zero) { try { DestroyIcon(_trayHicon); } catch { } }
            base.OnFormClosed(e);
        }

        // ---------------- 日志 ----------------
        private static Color LevelColor(Lv lv)
        {
            switch (lv)
            {
                case Lv.Conn: return CGreen;
                case Lv.Warn: return COrange;
                case Lv.Err: return CRed;
                default: return CDim;
            }
        }

        private void OnMonEvent(string level, string msg)
        {
            // 采样线程回调: 仅入队, 由 UI 定时器统一消费 (避免句柄未创建时 BeginInvoke 丢失事件)
            lock (_evtLock) { _pendingEvents.Enqueue(new string[] { level, msg }); }
        }

        private void DrainEvents()
        {
            lock (_evtLock)
            {
                while (_pendingEvents.Count > 0)
                {
                    string[] e = _pendingEvents.Dequeue();
                    Lv lv;
                    switch (e[0])
                    {
                        case "CONN": lv = Lv.Conn; break;
                        case "WARN": lv = Lv.Warn; break;
                        case "ERR": lv = Lv.Err; break;
                        default: lv = Lv.Info; break;
                    }
                    AppendLog(lv, e[1]);
                }
            }
        }

        private void AppendLog(Lv lv, string msg)
        {
            if (_logs.Count >= LogCap) _logs.RemoveAt(0);
            _logs.Add(new LogItem { Time = DateTime.Now, Level = lv, Text = msg });
            if (PassesFilter(lv)) AddLogItem(_logs[_logs.Count - 1]);
        }

        private void AddLogItem(LogItem item)
        {
            _lstLog.Items.Add(item);
            if (_chkAuto != null && _chkAuto.Checked && _lstLog.Items.Count > 0)
                _lstLog.TopIndex = _lstLog.Items.Count - 1;
        }

        private bool PassesFilter(Lv lv)
        {
            switch (_cmbLevel.SelectedIndex)
            {
                case 1: return lv == Lv.Info;
                case 2: return lv == Lv.Warn;
                case 3: return lv == Lv.Err;
                case 4: return lv == Lv.Conn;
                default: return true;
            }
        }

        private void RebuildLogView()
        {
            _lstLog.BeginUpdate();
            _lstLog.Items.Clear();
            foreach (LogItem item in _logs)
                if (PassesFilter(item.Level)) _lstLog.Items.Add(item);
            if (_chkAuto.Checked && _lstLog.Items.Count > 0)
                _lstLog.TopIndex = _lstLog.Items.Count - 1;
            _lstLog.EndUpdate();
        }

        private void OnDrawLogItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            LogItem item = (LogItem)_lstLog.Items[e.Index];
            e.DrawBackground();
            TextRenderer.DrawText(e.Graphics, item.ToString(), Font, e.Bounds, LevelColor(item.Level),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        // 复制日志: all=true 复制当前列表全部(含过滤), 否则复制选中项
        private void CopyLogs(bool all)
        {
            var sb = new StringBuilder();
            int count = 0;
            if (all)
            {
                foreach (object o in _lstLog.Items) { sb.AppendLine(o.ToString()); count++; }
            }
            else
            {
                foreach (object o in _lstLog.SelectedItems) { sb.AppendLine(o.ToString()); count++; }
            }
            if (count == 0)
            {
                AppendLog(Lv.Info, "未选中任何日志 (可单击或拖动选择, Ctrl+A 全选)");
                return;
            }
            try
            {
                Clipboard.SetText(sb.ToString());
                AppendLog(Lv.Info, "已复制 " + count + " 条日志到剪贴板");
            }
            catch { AppendLog(Lv.Err, "复制到剪贴板失败"); }
        }

        private void OnLogKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopyLogs(false);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                _lstLog.BeginUpdate();
                for (int i = 0; i < _lstLog.Items.Count; i++) _lstLog.SetSelected(i, true);
                _lstLog.EndUpdate();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void ExportLog()
        {
            try
            {
                using (SaveFileDialog dlg = new SaveFileDialog())
                {
                    dlg.Filter = "文本文件 (*.txt)|*.txt";
                    dlg.FileName = "rdpnet_events_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    var sb = new StringBuilder();
                    foreach (LogItem item in _logs)
                        sb.Append("[").Append(item.Time.ToString("HH:mm:ss")).Append("] [")
                          .Append(item.Level.ToString().ToUpperInvariant()).Append("] ").AppendLine(item.Text);
                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                }
                AppendLog(Lv.Info, "日志已导出");
            }
            catch (Exception ex) { AppendLog(Lv.Err, "导出失败: " + ex.Message); }
        }

        // ---------------- 定时刷新 ----------------
        private void OnTick(object sender, EventArgs e)
        {
            DrainEvents();
            if (_mon == null) return;
            Sample s = _mon.Latest;
            if (s == null || s.Time == DateTime.MinValue) return;
            UpdateTop(s);
            if (s.Time != _lastSampleTime)
            {
                _lastSampleTime = s.Time;
                _chRt.Add(0, s.RxBps);
                _chRt.Add(1, s.TxBps);
                double rtt = s.EffTcpRttMs >= 0 ? s.EffTcpRttMs : (s.UdpRttMs >= 0 ? s.UdpRttMs : double.NaN);
                if (!double.IsNaN(rtt)) _chRtt.Add(0, rtt);
                if (s.IcmpRttMs >= 0) _chRtt.Add(1, s.IcmpRttMs);
                UpdateMetrics(s);
            }
        }

        private void UpdateTop(Sample s)
        {
            string modeText;
            Color modeColor;
            if (s.Mode == MonitorMode.Server) { modeText = "模式: 服务端 (有人连入)"; modeColor = CGreen; }
            else if (s.Mode == MonitorMode.Client) { modeText = "模式: 客户端 (本机连出)"; modeColor = CBlue; }
            else { modeText = "模式: 监视中 (无连接)"; modeColor = CDim; }
            _lblMode.Text = modeText;
            _lblMode.ForeColor = modeColor;
            _lblRemote.Text = "远程: " + (s.Connected ? s.RemoteIp : "—");
            string proto = s.Connected ? "TCP 3389" : "—";
            if (s.Connected && s.UdpNegotiated) proto += s.UdpActive ? " +UDP 有流量" : " +UDP 空闲";
            _lblProto.Text = "协议: " + proto;
            string rtt = "—";
            if (s.EffTcpRttMs >= 0) rtt = s.EffTcpRttMs.ToString("0.#") + "ms";
            if (s.UdpRttMs >= 0) rtt += " / UDP " + s.UdpRttMs.ToString("0.#") + "ms";
            if (s.IcmpRttMs >= 0) rtt += " / ICMP " + s.IcmpRttMs + "ms";
            _lblRtt.Text = "RTT: " + rtt;
            _lblRx.Text = "↓ " + (s.Connected ? Fmt.Bps(s.RxBps) : "—");
            _lblTx.Text = "↑ " + (s.Connected ? Fmt.Bps(s.TxBps) : "—");
            Text = "RDP 网络监视器" + (s.Connected ? " — " + s.RemoteIp : "");
        }

        private void AddRow(string k, string v)
        {
            _rows.Add(new string[] { k, v });
        }

        // 行键未变化时原地更新文本, 保持滚动条位置; 结构变化才重建
        private void ApplyRows()
        {
            bool same = _lv.Items.Count == _rows.Count;
            if (same)
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    if (_lv.Items[i].SubItems[0].Text != _rows[i][0]) { same = false; break; }
                }
            }
            if (same)
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    ListViewItem it = _lv.Items[i];
                    if (it.SubItems[1].Text != _rows[i][1]) it.SubItems[1].Text = _rows[i][1];
                }
            }
            else
            {
                _lv.Items.Clear();
                foreach (string[] r in _rows) _lv.Items.Add(new ListViewItem(r));
            }
        }

        // 列宽随面板自适应, 避免横向滚动条 (ClientSize 已扣除竖向滚动条)
        private void OnLvResize(object sender, EventArgs e)
        {
            try
            {
                int w = _lv.ClientSize.Width;
                if (w <= 60 || _lv.Columns.Count < 2) return;
                int c1 = Math.Max(90, Math.Min(180, (int)(w * 0.42)));
                int c2 = Math.Max(100, w - c1 - 4);
                _lv.Columns[0].Width = c1;
                _lv.Columns[1].Width = c2;
            }
            catch { }
        }

        private void UpdateMetrics(Sample s)
        {
            _lv.BeginUpdate();
            _rows.Clear();
            if (!s.Connected)
            {
                AddRow("监视模式", s.Mode == MonitorMode.None ? "等待 RDP 连接..." : "连接已断开, 等待重连");
                AddRow("提示", "建立/断开连接后本面板将自动刷新");
                AddRow("采样频率", "1 次/秒");
                AddRow("数据文件", Path.GetFileName(_mon.Log.CsvPath));
                AddRow("日志目录", _mon.Log.LogDir);
                ApplyRows();
                _lv.EndUpdate();
                return;
            }

            AddRow("监视模式", s.Mode == MonitorMode.Server ? "服务端 (有人远程连入本机)" : "客户端 (本机 mstsc 连出)");
            AddRow("远程计算机", s.RemoteIp);
            string proto = "TCP 3389 已连接";
            if (s.UdpNegotiated) proto += s.UdpActive ? " + UDP (有数据流)" : " + UDP (已协商, 空闲)";
            else proto += "; UDP 未启用";
            AddRow("传输协议", proto);
            string rttSrc = "ICMP 链路";
            if (s.EstatsOk) rttSrc = "TCP estats";
            else if (s.TcpRttMs > 0 && !s.RfxRttStale) rttSrc = "RemoteFX";
            AddRow("往返时间 (有效)", Fmt.Ms(s.EffTcpRttMs) + " · 来源 " + rttSrc);
            if (s.TcpRttMs >= 0) AddRow("RemoteFX TCP RTT", Fmt.Ms(s.TcpRttMs) + (s.RfxRttStale ? " (存疑,已忽略)" : ""));
            if (s.UdpRttMs >= 0) AddRow("UDP 往返时间", Fmt.Ms(s.UdpRttMs));
            if (s.BaseUdpRttMs > 0) AddRow("UDP 基准 RTT", Fmt.Ms(s.BaseUdpRttMs));
            AddRow("链路 RTT (ICMP)", Fmt.Ms(s.IcmpRttMs));
            AddRow("接收速率", Fmt.Bps(s.RxBps) + (s.Mode == MonitorMode.Server ? " (客户端→本机)" : ""));
            AddRow("发送速率", Fmt.Bps(s.TxBps) + (s.Mode == MonitorMode.Server ? " (本机→客户端)" : ""));
            AddRow("峰值 接收/发送", Fmt.Bps(s.PeakRxBps) + " / " + Fmt.Bps(s.PeakTxBps));
            if (s.TcpBwEst >= 0) AddRow("TCP 带宽估计", Fmt.Mbps(s.TcpBwEst));
            if (s.UdpBwEst >= 0) AddRow("UDP 带宽估计", Fmt.Mbps(s.UdpBwEst));
            if (s.UdpSharePct >= 0) AddRow("UDP 流量占比", s.UdpSharePct.ToString("0.#") + "%");
            if (s.LossRatePct >= 0) AddRow("丢包率", Fmt.Pct(s.LossRatePct));
            if (s.RetranRatePct >= 0) AddRow("重传率", Fmt.Pct(s.RetranRatePct));
            if (s.FecRate >= 0) AddRow("FEC 率", s.FecRate.ToString("0.##"));
            if (s.RxTotalBytes >= 0) AddRow("累计传输", "接收 " + Fmt.Bytes(s.RxTotalBytes) + " / 发送 " + Fmt.Bytes(s.TxTotalBytes));
            AddRow("会话时长", Monitor.FmtDuration(s.Duration));
            if (s.Mode == MonitorMode.Server)
            {
                if (!string.IsNullOrEmpty(s.SessionName)) AddRow("会话", s.SessionName);
                if (!string.IsNullOrEmpty(s.ClientName)) AddRow("客户端计算机", s.ClientName);
                if (s.ClientBuild > 0) AddRow("客户端版本", "10.0." + s.ClientBuild);
                if (!string.IsNullOrEmpty(s.RfxInstance)) AddRow("数据源", "RemoteFX Network [" + s.RfxInstance + "] (协议级)");
            }
            else
            {
                AddRow("本地 OS", Fmt.LocalOsString());
                AddRow("数据源", "mstsc 进程 I/O (近似网络流量)");
            }
            if (s.EstatsOk)
            {
                AddRow("TCP 统计 (estats)", "SampleRtt=" + s.EstatsSampleRttMs + "ms RttVar=" + s.EstatsRttVarMs + "ms");
                AddRow("重传/超时/拥塞", s.EstatsRetrans + " / " + s.EstatsTimeouts + " / " + s.EstatsCongSignals);
            }
            else
            {
                AddRow("TCP 统计 (estats)", "不可用 (通常需以管理员身份运行)");
                AddRow("重传/超时/拥塞", "— (estats 不可用)");
            }
            AddRow("采样频率", "1 次/秒");
            AddRow("数据文件", Path.GetFileName(_mon.Log.CsvPath));
            AddRow("日志目录", _mon.Log.LogDir);
            ApplyRows();
            _lv.EndUpdate();
        }

        // ---------------- 复制诊断 ----------------
        private void CopyDiagnostic()
        {
            string text = BuildDiagnosticText();
            try
            {
                Clipboard.SetText(text);
                AppendLog(Lv.Info, "诊断信息已复制到剪贴板");
            }
            catch { }
        }

        private string BuildDiagnosticText()
        {
            Sample s = _mon != null ? _mon.Latest : null;
            var sb = new StringBuilder();
            sb.AppendLine("远程桌面连接 - 诊断信息 (由 RDP 网络监视器生成)");
            sb.AppendLine("时间戳 (UTC): " + DateTime.UtcNow.ToString("MM/dd/yy hh:mm:ss tt", CultureInfo.InvariantCulture));
            sb.AppendLine("活动 ID: " + Guid.NewGuid());
            if (s != null && s.Connected)
            {
                sb.AppendLine("[客户端信息]");
                if (s.Mode == MonitorMode.Server)
                {
                    sb.AppendLine("客户端计算机: " + (string.IsNullOrEmpty(s.ClientName) ? "未知" : s.ClientName));
                    if (s.ClientBuild > 0) sb.AppendLine("客户端版本: 10.0." + s.ClientBuild);
                }
                else
                {
                    sb.AppendLine("本地 OS: " + Fmt.LocalOsString());
                }
                sb.AppendLine("[网络信息]");
                string proto = "TCP 3389 已连接";
                if (s.UdpNegotiated) proto += s.UdpActive ? "; UDP 已协商 (有数据流)" : "; UDP 已协商 (当前空闲)";
                sb.AppendLine("传输协议: " + proto);
                if (s.EffTcpRttMs >= 0) sb.AppendLine("往返时间 (有效): " + s.EffTcpRttMs.ToString("0.#") + " ms"
                    + (s.EstatsOk ? " (TCP estats)" : " (链路)"));
                if (s.TcpRttMs >= 0) sb.AppendLine("TCP 往返时间 (RemoteFX 报告): " + s.TcpRttMs.ToString("0.#") + " ms"
                    + (s.RfxRttStale ? " (存疑,已忽略)" : ""));
                if (s.UdpRttMs >= 0) sb.AppendLine("UDP 往返时间: " + s.UdpRttMs.ToString("0.#") + " ms");
                if (s.IcmpRttMs >= 0) sb.AppendLine("链路往返时间 (ICMP): " + s.IcmpRttMs + " ms");
                if (s.TcpBwEst >= 0) sb.AppendLine("TCP 带宽估计: " + (s.TcpBwEst / 1e6).ToString("0.00") + " Mbps");
                if (s.UdpBwEst >= 0) sb.AppendLine("UDP 带宽估计: " + (s.UdpBwEst / 1e6).ToString("0.00") + " Mbps");
                sb.AppendLine("当前吞吐量: 接收 " + Fmt.Bps(s.RxBps) + " / 发送 " + Fmt.Bps(s.TxBps));
                sb.AppendLine("峰值吞吐量: 接收 " + Fmt.Bps(s.PeakRxBps) + " / 发送 " + Fmt.Bps(s.PeakTxBps));
                if (s.UdpSharePct >= 0) sb.AppendLine("UDP 流量占比: " + s.UdpSharePct.ToString("0.#") + "%");
                if (s.LossRatePct >= 0) sb.AppendLine("丢包率: " + s.LossRatePct.ToString("0.##") + "%");
                if (s.RetranRatePct >= 0) sb.AppendLine("重传率: " + s.RetranRatePct.ToString("0.##") + "%");
                if (s.FecRate >= 0) sb.AppendLine("FEC 率: " + s.FecRate.ToString("0.##"));
                sb.AppendLine("[远程计算机信息]");
                sb.AppendLine("远程会话类型: 远程桌面");
                sb.AppendLine("网关名称: 没有在使用中");
                sb.AppendLine("网关登录方法: 没有在使用中");
                sb.AppendLine("远程计算机: " + s.RemoteIp);
                sb.AppendLine("[会话统计]");
                if (!string.IsNullOrEmpty(s.SessionName)) sb.AppendLine("会话: " + s.SessionName);
                sb.AppendLine("会话时长: " + Monitor.FmtDuration(s.Duration));
                if (s.RxTotalBytes >= 0)
                    sb.AppendLine("累计传输: 接收 " + Fmt.Bytes(s.RxTotalBytes) + " / 发送 " + Fmt.Bytes(s.TxTotalBytes));
                if (s.EstatsOk)
                    sb.AppendLine("TCP 连接统计 (estats): SampleRtt=" + s.EstatsSampleRttMs + "ms RttVar=" + s.EstatsRttVarMs
                        + "ms 重传段=" + s.EstatsRetrans + " 超时=" + s.EstatsTimeouts + " 拥塞信号=" + s.EstatsCongSignals);
                else
                    sb.AppendLine("TCP 连接统计 (estats): 不可用 (通常需要管理员权限)");
            }
            else
            {
                sb.AppendLine("[状态]");
                sb.AppendLine("当前没有检测到活动的 RDP 连接。");
            }
            return sb.ToString();
        }
    }
}
