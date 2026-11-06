// PC Monitor Panel
// A borderless, always-on-top desktop sensor panel fed by HWiNFO shared memory.
// Framerate and frametime come from RTSS through HWiNFO's RTSS integration.
// Everything shown is defined in panel.json next to the exe (written on first run).
// Build: build.cmd (uses the C# compiler that ships with .NET Framework 4.x on every Windows 10/11 PC)
// License: MIT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PCMonitorPanel
{
    // ================= configuration =================
    public class SourceDef
    {
        public string sensor { get; set; }   // regex on HWiNFO sensor name, e.g. "^CPU \\[#0\\]"
        public string label { get; set; }    // regex on reading label, e.g. "^Total CPU Usage$"
        public string unit { get; set; }     // optional regex on reading unit, e.g. "RPM"
    }

    public class MetricDef
    {
        public string section { get; set; }
        public string label { get; set; }
        public string unit { get; set; }
        public string format { get; set; }
        public double min { get; set; }
        public double max { get; set; }          // 0 = autoscale
        public double? warn { get; set; }
        public double? crit { get; set; }
        public bool lowIsBad { get; set; }       // warn/crit trigger at or below the thresholds (e.g. drive life)
        public bool sameRow { get; set; }        // share the previous row, side by side (e.g. C: | G:)
        public double scale { get; set; }        // value multiplier, 0 treated as 1
        public string agg { get; set; }          // first | max | min | sum | avg
        public string special { get; set; }      // "ping" for the built-in pinger, "refresh" for a monitor's refresh rate
        public string display { get; set; }      // for "refresh": regex on the monitor name, hardware id or device name like DISPLAY2 (empty = primary)
        public List<SourceDef> sources { get; set; }
    }

    public class SectionDef
    {
        public string name { get; set; }
        public string color { get; set; }
        public int column { get; set; }
    }

    public class Config
    {
        public int screenWidth { get; set; }     // pick the monitor with this resolution...
        public int screenHeight { get; set; }
        public int? monitorIndex { get; set; }   // ...or this index in Screen.AllScreens (omit or null = by resolution, then primary)
        public int width { get; set; }
        public string anchor { get; set; }       // right | left
        public int[] bounds { get; set; }        // optional absolute override: [x, y, w, h]
        public int columns { get; set; }
        public double fontScale { get; set; }
        public double opacity { get; set; }
        public string pingHost { get; set; }
        public string gpuLimiterSensor { get; set; }
        public string wheaSensor { get; set; }
        public List<SectionDef> sections { get; set; }
        public List<MetricDef> metrics { get; set; }
    }

    // ================= HWiNFO shared memory =================
    class Reading { public string Sensor, Label, Unit; public double Value; }

    class Feed
    {
        public List<Reading> Readings = new List<Reading>();
        public bool Ok;
        public string Signature = "";
        long lastPoll; DateTime lastPollChange = DateTime.Now;
        public bool Stale { get { return Ok && (DateTime.Now - lastPollChange).TotalSeconds > 6; } }

        static string Str(MemoryMappedViewAccessor v, long off, int len, byte[] buf)
        {
            v.ReadArray(off, buf, 0, len);
            int n = Array.IndexOf(buf, (byte)0, 0, len); if (n < 0) n = len;
            return Encoding.Default.GetString(buf, 0, n);
        }

        public void Read()
        {
            try
            {
                using (var m = MemoryMappedFile.OpenExisting("Global\\HWiNFO_SENS_SM2", MemoryMappedFileRights.Read))
                using (var v = m.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                {
                    long poll = v.ReadInt64(12);
                    if (poll != lastPoll) { lastPoll = poll; lastPollChange = DateTime.Now; }
                    uint offS = v.ReadUInt32(20), szS = v.ReadUInt32(24), nS = v.ReadUInt32(28);
                    uint offR = v.ReadUInt32(32), szR = v.ReadUInt32(36), nR = v.ReadUInt32(40);
                    var buf = new byte[128];
                    var names = new string[nS];
                    for (uint i = 0; i < nS; i++) names[i] = Str(v, offS + i * szS + 8, 128, buf);
                    string sig = nS + "/" + nR;
                    bool rebuild = sig != Signature || Readings.Count != nR;
                    if (rebuild) Readings = new List<Reading>((int)nR);
                    for (uint i = 0; i < nR; i++)
                    {
                        long o = offR + i * szR;
                        if (rebuild)
                        {
                            uint si = v.ReadUInt32(o + 4);
                            var r = new Reading();
                            r.Sensor = si < nS ? names[si] : "";
                            r.Label = Str(v, o + 12, 128, buf);
                            r.Unit = Str(v, o + 268, 16, buf);
                            Readings.Add(r);
                        }
                        Readings[(int)i].Value = v.ReadDouble(o + 284);
                    }
                    Signature = sig;
                }
                Ok = true;
            }
            catch { Ok = false; }
        }
    }

    // ================= monitor refresh rate (current display mode, not live VRR) =================
    static class Displays
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, x, y, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string dev, int i, ref DISPLAY_DEVICE dd, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);

        static Dictionary<string, string> cache = new Dictionary<string, string>();   // pattern -> device name
        static DateTime cacheAt = DateTime.MinValue;

        static string Find(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return Screen.PrimaryScreen.DeviceName;
            if ((DateTime.Now - cacheAt).TotalSeconds > 30) { cache.Clear(); cacheAt = DateTime.Now; }
            string hit;
            if (cache.TryGetValue(pattern, out hit)) return hit;
            var rx = new Regex(pattern, RegexOptions.IgnoreCase);
            for (int i = 0; i < 32 && hit == null; i++)
            {
                var a = new DISPLAY_DEVICE(); a.cb = Marshal.SizeOf(a);
                if (!EnumDisplayDevices(null, i, ref a, 0)) break;
                if ((a.StateFlags & 1) == 0) continue;   // not attached to the desktop
                if (rx.IsMatch(a.DeviceName)) { hit = a.DeviceName; break; }
                for (int j = 0; j < 8; j++)
                {
                    var m = new DISPLAY_DEVICE(); m.cb = a.cb;
                    if (!EnumDisplayDevices(a.DeviceName, j, ref m, 0)) break;
                    if (rx.IsMatch(m.DeviceString ?? "") || rx.IsMatch(m.DeviceID ?? "")) { hit = a.DeviceName; break; }
                }
            }
            cache[pattern] = hit;
            return hit;
        }

        public static double RefreshRate(string pattern)
        {
            string dev = Find(pattern);
            if (dev == null) return double.NaN;
            var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(dm);
            if (!EnumDisplaySettings(dev, -1, ref dm) || dm.dmDisplayFrequency <= 1) return double.NaN;
            return dm.dmDisplayFrequency;
        }
    }

    // ================= runtime metric =================
    class Metric
    {
        public MetricDef Def;
        public List<int> Idx = new List<int>();
        public bool Found;
        public double[] Hist = new double[3600];
        public int Count, Head;
        public double Last = double.NaN;
        public Metric(MetricDef d) { Def = d; }
        public void Push(double v) { Last = v; Hist[Head] = v; Head = (Head + 1) % Hist.Length; if (Count < Hist.Length) Count++; }
        public double Ago(int i) { return Hist[((Head - 1 - i) % Hist.Length + Hist.Length) % Hist.Length]; }
    }

    // ================= the panel =================
    class Panel : Form
    {
        Config cfg;
        Feed feed = new Feed();
        List<Metric> metrics = new List<Metric>();
        Dictionary<string, SectionDef> sections = new Dictionary<string, SectionDef>();
        string resolvedFor = null;
        bool resolvedOnce;
        System.Windows.Forms.Timer timer;
        int historyMode = 0;
        volatile int pingMs = -1;
        double wheaBase = double.NaN;
        Regex limiterRx, wheaRx;

        static Color Bg = Color.FromArgb(13, 16, 22), Card = Color.FromArgb(20, 24, 32), Grid = Color.FromArgb(34, 40, 52),
                     TextDim = Color.FromArgb(138, 148, 166), TextHi = Color.FromArgb(232, 236, 243),
                     OkC = Color.FromArgb(84, 214, 140), WarnC = Color.FromArgb(246, 190, 70), CritC = Color.FromArgb(255, 84, 84);
        Font fLabel, fValue, fUnit, fSmall, fSec, fHead;

        public Panel(Config c)
        {
            cfg = c;
            float fs = (float)(cfg.fontScale > 0 ? cfg.fontScale : 1.0);
            fLabel = new Font("Segoe UI", 12f * fs); fValue = new Font("Segoe UI Semibold", 19f * fs); fUnit = new Font("Segoe UI", 10f * fs);
            fSmall = new Font("Segoe UI", 9.5f * fs); fSec = new Font("Segoe UI Semibold", 11f * fs); fHead = new Font("Segoe UI Semibold", 17f * fs);

            foreach (var s in cfg.sections ?? new List<SectionDef>()) sections[s.name] = s;
            foreach (var d in cfg.metrics ?? new List<MetricDef>())
            {
                if (!sections.ContainsKey(d.section)) { var s = new SectionDef(); s.name = d.section; s.color = "#8AA0C0"; sections[d.section] = s; }
                metrics.Add(new Metric(d));
            }
            if (!string.IsNullOrEmpty(cfg.gpuLimiterSensor)) limiterRx = new Regex(cfg.gpuLimiterSensor);
            if (!string.IsNullOrEmpty(cfg.wheaSensor)) wheaRx = new Regex(cfg.wheaSensor);

            Text = "PC Monitor Panel";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg;
            if (cfg.opacity > 0 && cfg.opacity < 1) Opacity = cfg.opacity;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            PlaceInSlot();

            var menu = new ContextMenuStrip();
            menu.Items.Add("History: 7 minutes", null, (s, e) => { historyMode = 0; Invalidate(); });
            menu.Items.Add("History: 1 hour", null, (s, e) => { historyMode = 1; Invalidate(); });
            menu.Items.Add("-");
            menu.Items.Add("Edit panel.json", null, (s, e) => { try { System.Diagnostics.Process.Start("notepad.exe", "\"" + Program.ConfigPath + "\""); } catch { } });
            menu.Items.Add("Reload (restart panel)", null, (s, e) => { Application.Restart(); });
            menu.Items.Add("-");
            menu.Items.Add("Exit panel", null, (s, e) => Close());
            ContextMenuStrip = menu;

            if (metrics.Exists(m => m.Def.special == "ping")) { var pt = new Thread(PingLoop); pt.IsBackground = true; pt.Start(); }

            timer = new System.Windows.Forms.Timer(); timer.Interval = 1000;
            timer.Tick += (s, e) => Tick();
            timer.Start();
            Tick();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => PlaceInSlot();
        }

        void PlaceInSlot()
        {
            if (cfg.bounds != null && cfg.bounds.Length == 4) { Bounds = new Rectangle(cfg.bounds[0], cfg.bounds[1], cfg.bounds[2], cfg.bounds[3]); return; }
            Screen target = Screen.PrimaryScreen;
            var all = Screen.AllScreens;
            if (cfg.monitorIndex.HasValue && cfg.monitorIndex.Value >= 0 && cfg.monitorIndex.Value < all.Length) target = all[cfg.monitorIndex.Value];
            else foreach (var s in all) if (s.Bounds.Width == cfg.screenWidth && s.Bounds.Height == cfg.screenHeight) { target = s; break; }
            var b = target.Bounds;
            int w = Math.Min(cfg.width > 0 ? cfg.width : 912, b.Width);
            int x = (cfg.anchor ?? "right").ToLower() == "left" ? b.Left : b.Right - w;
            Bounds = new Rectangle(x, b.Top, w, b.Height);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW: no taskbar / Alt-Tab entry
                cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE: never steals focus
                cp.ExStyle |= 0x8;         // WS_EX_TOPMOST
                return cp;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        void PingLoop()
        {
            var p = new Ping();
            string host = string.IsNullOrEmpty(cfg.pingHost) ? "1.1.1.1" : cfg.pingHost;
            while (true)
            {
                try { var r = p.Send(host, 1500); pingMs = r.Status == IPStatus.Success ? (int)r.RoundtripTime : -1; }
                catch { pingMs = -1; }
                Thread.Sleep(2000);
            }
        }

        // map each metric to the HWiNFO readings that match its patterns (first source with any match wins)
        void Resolve()
        {
            foreach (var m in metrics)
            {
                m.Idx.Clear(); m.Found = false;
                if (m.Def.sources == null) continue;
                foreach (var s in m.Def.sources)
                {
                    Regex rs = string.IsNullOrEmpty(s.sensor) ? null : new Regex(s.sensor);
                    Regex rl = string.IsNullOrEmpty(s.label) ? null : new Regex(s.label);
                    Regex ru = string.IsNullOrEmpty(s.unit) ? null : new Regex(s.unit);
                    for (int i = 0; i < feed.Readings.Count; i++)
                    {
                        var r = feed.Readings[i];
                        if ((rs == null || rs.IsMatch(r.Sensor)) && (rl == null || rl.IsMatch(r.Label)) && (ru == null || ru.IsMatch(r.Unit)))
                            m.Idx.Add(i);
                    }
                    if (m.Idx.Count > 0) { m.Found = true; break; }
                }
            }
            resolvedFor = feed.Signature;
            resolvedOnce = true;
        }

        double Value(Metric m)
        {
            if (m.Def.special == "ping") return pingMs < 0 ? double.NaN : pingMs;
            if (m.Def.special == "refresh") return Displays.RefreshRate(m.Def.display);
            if (m.Idx.Count == 0) return double.NaN;
            string agg = (m.Def.agg ?? "first").ToLower();
            double acc = double.NaN; int n = 0;
            foreach (int i in m.Idx)
            {
                double v = feed.Readings[i].Value;
                if (double.IsNaN(v)) continue;
                if (agg == "first") { acc = v; n = 1; break; }
                if (double.IsNaN(acc)) acc = v;
                else if (agg == "max") acc = Math.Max(acc, v);
                else if (agg == "min") acc = Math.Min(acc, v);
                else acc += v;
                n++;
            }
            if (n == 0) return double.NaN;
            if (agg == "avg") acc /= n;
            return acc * (m.Def.scale == 0 ? 1 : m.Def.scale);
        }

        bool IsShown(Metric m) { return !resolvedOnce || m.Found || !string.IsNullOrEmpty(m.Def.special); }

        int tickN;
        void Tick()
        {
            feed.Read();
            if (feed.Ok && feed.Signature != resolvedFor) Resolve();
            foreach (var m in metrics)
            {
                double v = double.NaN;
                if ((feed.Ok && !feed.Stale) || !string.IsNullOrEmpty(m.Def.special)) { try { v = Value(m); } catch { } }
                m.Push(v);
            }
            if (feed.Ok && double.IsNaN(wheaBase)) wheaBase = WheaTotal();
            if (++tickN % 5 == 0) SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            Invalidate();
        }

        double WheaTotal()
        {
            if (wheaRx == null || !feed.Ok) return 0;
            double t = 0; foreach (var r in feed.Readings) if (wheaRx.IsMatch(r.Sensor)) t += r.Value;
            return t;
        }

        string Limiters()
        {
            if (limiterRx == null || !feed.Ok) return "";
            var sb = new StringBuilder();
            foreach (var r in feed.Readings)
            {
                if (!limiterRx.IsMatch(r.Sensor) || !r.Label.StartsWith("Performance Limit - ") || r.Value <= 0) continue;
                string k = r.Label.Substring(20);
                if (k == "Power") k = "PWR"; else if (k == "Thermal") k = "THERM"; else if (k == "Reliability Voltage") k = "VREL";
                else if (k == "Max Operating Voltage") k = "VOP"; else if (k == "Utilization") k = "UTIL"; else k = k.Split(' ')[0].ToUpper();
                sb.Append(k).Append(' ');
            }
            return sb.ToString().Trim();
        }

        static Color ParseColor(string hex)
        {
            try { return ColorTranslator.FromHtml(hex); } catch { return Color.FromArgb(138, 160, 192); }
        }

        Color Level(MetricDef d, double v)
        {
            if (double.IsNaN(v)) return TextDim;
            if (d.lowIsBad)
            {
                if (d.crit.HasValue && v <= d.crit.Value) return CritC;
                if (d.warn.HasValue && v <= d.warn.Value) return WarnC;
                return TextHi;
            }
            if (d.crit.HasValue && v >= d.crit.Value) return CritC;
            if (d.warn.HasValue && v >= d.warn.Value) return WarnC;
            return TextHi;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Bg);
            int W = ClientSize.Width, H = ClientSize.Height, pad = 10;

            int headH = (int)(52 * (cfg.fontScale > 0 ? cfg.fontScale : 1));
            using (var b = new SolidBrush(TextHi)) g.DrawString(DateTime.Now.ToString("HH:mm:ss"), fHead, b, pad, 8);
            string status; Color sc;
            if (!feed.Ok) { status = "HWiNFO not running or shared memory off"; sc = CritC; }
            else if (feed.Stale) { status = "HWiNFO feed stalled (free version 12h limit?)"; sc = CritC; }
            else { status = "live  ·  " + (historyMode == 0 ? "7 min" : "1 hr") + " history"; sc = OkC; }
            using (var b = new SolidBrush(sc)) { var sz = g.MeasureString(status, fSmall); g.DrawString(status, fSmall, b, W - pad - sz.Width, 6); }

            var parts = new List<string>();
            if (limiterRx != null) { string lim = Limiters(); parts.Add("GPU limit: " + (lim == "" ? "none" : lim)); }
            int wheaNew = 0;
            if (wheaRx != null) { wheaNew = double.IsNaN(wheaBase) ? 0 : (int)Math.Max(0, WheaTotal() - wheaBase); parts.Add("WHEA: " + wheaNew); }
            if (parts.Count > 0)
            {
                string l2 = string.Join("    ", parts.ToArray());
                using (var b = new SolidBrush(wheaNew > 0 ? CritC : TextDim)) { var sz = g.MeasureString(l2, fSmall); g.DrawString(l2, fSmall, b, W - pad - sz.Width, 6 + fSmall.Height + 4); }
            }

            int cols = Math.Max(1, cfg.columns);
            int colGap = 16;
            float colW = (W - 2 * pad - colGap * (cols - 1)) / (float)cols;
            for (int c = 0; c < cols; c++)
            {
                var col = new List<Metric>();
                foreach (var m in metrics)
                {
                    int mc = Math.Min(cols - 1, Math.Max(0, sections[m.Def.section].column));
                    if (mc == c && IsShown(m)) col.Add(m);
                }
                if (col.Count > 0) DrawColumn(g, col, pad + c * (colW + colGap), headH, colW, H - headH - 4);
                if (c > 0) using (var p = new Pen(Grid)) g.DrawLine(p, pad + c * (colW + colGap) - colGap / 2f, headH + 8, pad + c * (colW + colGap) - colGap / 2f, H - 8);
            }
        }

        void DrawColumn(Graphics g, List<Metric> col, float x, float top, float w, float h)
        {
            // group metrics into rows: a metric with sameRow joins the previous row of the same section
            var rows = new List<List<Metric>>();
            var secs = new List<string>();
            foreach (var m in col)
            {
                if (!secs.Contains(m.Def.section)) secs.Add(m.Def.section);
                var last = rows.Count > 0 ? rows[rows.Count - 1] : null;
                if (m.Def.sameRow && last != null && last[0].Def.section == m.Def.section) last.Add(m);
                else rows.Add(new List<Metric> { m });
            }
            float secH = fSec.Height + 6, gap = 6, cellGap = 14;
            float rowH = (h - secs.Count * (secH + gap)) / rows.Count;
            float y = top;
            string cur = null;
            Color accent = TextHi;
            foreach (var row in rows)
            {
                if (row[0].Def.section != cur)
                {
                    cur = row[0].Def.section; accent = ParseColor(sections[cur].color);
                    y += gap;
                    using (var b = new SolidBrush(accent)) g.DrawString(cur, fSec, b, x - 2, y + 1);
                    using (var p = new Pen(Color.FromArgb(70, accent))) g.DrawLine(p, x + g.MeasureString(cur, fSec).Width + 2, y + secH / 2f + 1, x + w, y + secH / 2f + 1);
                    y += secH;
                }
                float cw = (w - cellGap * (row.Count - 1)) / row.Count;
                for (int i = 0; i < row.Count; i++) DrawRow(g, row[i], accent, x + i * (cw + cellGap), y, cw, rowH);
                y += rowH;
            }
        }

        void DrawRow(Graphics g, Metric m, Color accent, float x, float y, float w, float h)
        {
            var d = m.Def;
            string fmt = string.IsNullOrEmpty(d.format) ? "0" : d.format;
            string unit = d.unit ?? "";
            float textH = fValue.Height + 2, gy = y + textH, gh = h - textH - 8;
            using (var b = new SolidBrush(TextDim)) g.DrawString(d.label, fLabel, b, x, y + (fValue.Height - fLabel.Height) * 0.75f);

            string val = double.IsNaN(m.Last) ? "--" : m.Last.ToString(fmt);
            var vs = g.MeasureString(val, fValue); var us = g.MeasureString(unit, fUnit);
            using (var b = new SolidBrush(Level(d, m.Last))) g.DrawString(val, fValue, b, x + w - us.Width - vs.Width + 6, y - 1);
            using (var b = new SolidBrush(TextDim)) g.DrawString(unit, fUnit, b, x + w - us.Width + 2, y + (fValue.Height - fUnit.Height) * 0.8f);

            // fixed time window (7 min or 1 hr) mapped onto the cell width; newest sample at the right edge
            int px = Math.Max(1, (int)w);
            int span = historyMode == 0 ? 420 : 3600;
            int avail = Math.Min(m.Count, span);
            if (avail < 2 || gh < 6) return;
            int buckets = Math.Min(px, span);
            double per = span / (double)buckets;
            var vals = new double[buckets];
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < buckets; i++)
            {
                vals[i] = double.NaN;
                int a = (int)(i * per), z = Math.Max(a + 1, (int)((i + 1) * per));
                if (a >= avail) continue;
                double mx = double.NaN;
                for (int k = a; k < z && k < avail; k++) { double v = m.Ago(k); if (!double.IsNaN(v) && (double.IsNaN(mx) || v > mx)) mx = v; }
                vals[i] = mx;
                if (!double.IsNaN(mx)) { lo = Math.Min(lo, mx); hi = Math.Max(hi, mx); }
            }
            using (var b = new SolidBrush(Card)) g.FillRectangle(b, x, gy, w, gh);
            using (var p = new Pen(Grid)) for (int q = 1; q < 4; q++) g.DrawLine(p, x, gy + gh * q / 4f, x + w, gy + gh * q / 4f);
            if (hi == double.MinValue) return;
            double min = d.min, max = d.max > 0 ? d.max : Math.Max(hi * 1.15, 1);
            if (d.max > 0 && hi > max) max = hi;
            if (max <= min) max = min + 1;

            if (d.warn.HasValue && d.max > 0 && d.warn.Value > min && d.warn.Value < max)
            {
                float wy = gy + gh - (float)((d.warn.Value - min) / (max - min)) * gh;
                using (var p = new Pen(Color.FromArgb(90, WarnC))) { p.DashStyle = DashStyle.Dash; g.DrawLine(p, x, wy, x + w, wy); }
            }
            float step = w / (float)buckets;
            var pts = new List<PointF>();
            PointF peak = PointF.Empty, valley = PointF.Empty;
            for (int i = 0; i < buckets; i++)
            {
                if (double.IsNaN(vals[i])) continue;
                double f = Math.Max(0, Math.Min(1, (vals[i] - min) / (max - min)));
                var pt = new PointF(x + w - i * step, gy + gh - (float)f * gh);
                pts.Add(pt);
                if (vals[i] == hi && peak.IsEmpty) peak = pt;
                if (vals[i] == lo && valley.IsEmpty) valley = pt;
            }
            if (pts.Count >= 2)
            {
                var fill = new List<PointF>(pts);
                fill.Add(new PointF(pts[pts.Count - 1].X, gy + gh)); fill.Add(new PointF(pts[0].X, gy + gh));
                using (var b = new LinearGradientBrush(new RectangleF(x, gy - 1, w, gh + 2), Color.FromArgb(110, accent), Color.FromArgb(10, accent), 90f))
                    g.FillPolygon(b, fill.ToArray());
                using (var p = new Pen(accent, 1.8f)) g.DrawLines(p, pts.ToArray());
            }
            if (hi > lo)
            {
                using (var b = new SolidBrush(TextHi)) g.FillEllipse(b, peak.X - 3.5f, peak.Y - 3.5f, 7, 7);
                using (var p = new Pen(TextHi, 1.5f)) g.DrawEllipse(p, valley.X - 3.5f, valley.Y - 3.5f, 7, 7);
            }
            using (var bg = new SolidBrush(Color.FromArgb(200, Card)))
            using (var b = new SolidBrush(TextHi))
            using (var dm = new SolidBrush(TextDim))
            {
                string hs = "▲ " + hi.ToString(fmt), ls = "▼ " + lo.ToString(fmt);
                var hsz = g.MeasureString(hs, fSmall); var lsz = g.MeasureString(ls, fSmall);
                g.FillRectangle(bg, x + 2, gy + 1, Math.Max(hsz.Width, lsz.Width) + 2, gh - 2);
                g.DrawString(hs, fSmall, b, x + 3, gy + 1);
                g.DrawString(ls, fSmall, dm, x + 3, gy + gh - lsz.Height);
            }
        }
    }

    static class Program
    {
        public static string ConfigPath;

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mtx = new Mutex(true, "Local\\PCMonitorPanel", out created))
            {
                if (!created) return;
                try { SetProcessDpiAwareness(2); } catch { }
                Application.EnableVisualStyles();
                ConfigPath = args.Length > 0 ? Path.GetFullPath(args[0])
                    : Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "panel.json");
                Config cfg;
                try
                {
                    if (!File.Exists(ConfigPath))
                        using (var rs = typeof(Program).Assembly.GetManifestResourceStream("panel.default.json"))
                        using (var fs = File.Create(ConfigPath)) rs.CopyTo(fs);
                    cfg = new JavaScriptSerializer().Deserialize<Config>(File.ReadAllText(ConfigPath, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not read " + ConfigPath + "\n\n" + ex.Message, "PC Monitor Panel", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                Application.Run(new Panel(cfg));
            }
        }
        [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int v);
    }
}
