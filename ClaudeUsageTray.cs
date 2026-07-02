// Claude Usage Tray
// Shows Claude Code 5-hour session usage as a ring-gauge tray icon + a rounded
// pill overlaid on the taskbar; click opens a detail popup with meter bars.
// Data: OAuth usage API (primary) with statusline file fallback, both read via \\wsl.localhost.
// Target: .NET Framework 4.8, compiled with the in-box csc.exe (C# 5 syntax only).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ClaudeUsageTray
{
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string win);
        [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
    }

    // Design tokens: dataviz reference palette, dark surface. Severity rides the
    // marks (ring / meter fill); text always wears ink tokens, never the data color.
    static class Theme
    {
        public static readonly Color Surface = Color.FromArgb(26, 26, 25);        // #1a1a19
        public static readonly Color SurfaceHi = Color.FromArgb(34, 34, 33);      // popup card
        public static readonly Color InkPrimary = Color.White;
        public static readonly Color InkSecondary = Color.FromArgb(195, 194, 183); // #c3c2b7
        public static readonly Color InkMuted = Color.FromArgb(137, 135, 129);    // #898781
        public static readonly Color Hairline = Color.FromArgb(26, 255, 255, 255);
        public static readonly Color Accent = Color.FromArgb(57, 135, 229);       // #3987e5
        public static readonly Color Warning = Color.FromArgb(250, 178, 25);      // #fab219
        public static readonly Color Critical = Color.FromArgb(208, 59, 59);      // #d03b3b

        public static Color Severity(int pct)
        {
            return (pct >= 90) ? Critical : (pct >= 70) ? Warning : Accent;
        }

        public static Color Track(Color severity)
        {
            // unfilled track = translucent step of the fill's own hue
            return Color.FromArgb(56, severity);
        }

        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void ApplyRounded(Form f, int fallbackRadius)
        {
            int pref = 2; // DWMWCP_ROUND (Win11)
            int hr = -1;
            try { hr = Native.DwmSetWindowAttribute(f.Handle, 33, ref pref, 4); }
            catch { }
            if (hr != 0) // Win10: clip with a region instead
                f.Region = new Region(RoundedRect(new Rectangle(0, 0, f.Width, f.Height), fallbackRadius));
        }

        public static void DrawMeter(Graphics g, Rectangle r, double pct, Color severity)
        {
            using (GraphicsPath track = RoundedRect(r, r.Height / 2))
            using (SolidBrush tb = new SolidBrush(Track(severity)))
                { g.FillPath(tb, track); }
            int w = (int)Math.Round(r.Width * Math.Max(0.0, Math.Min(100.0, pct)) / 100.0);
            if (w > 0)
            {
                if (w < r.Height) w = r.Height; // keep the rounded cap legible near zero
                Rectangle fr = new Rectangle(r.X, r.Y, w, r.Height);
                using (GraphicsPath fill = RoundedRect(fr, r.Height / 2))
                using (SolidBrush fb = new SolidBrush(severity))
                    { g.FillPath(fb, fill); }
            }
        }

        public static void DrawRing(Graphics g, RectangleF r, double pct, Color severity, float thickness)
        {
            using (Pen track = new Pen(Track(severity), thickness))
                g.DrawEllipse(track, r);
            float sweep = (float)(Math.Max(0.0, Math.Min(100.0, pct)) * 3.6);
            if (sweep > 0f)
            {
                using (Pen fill = new Pen(severity, thickness))
                {
                    fill.StartCap = LineCap.Round;
                    fill.EndCap = LineCap.Round;
                    g.DrawArc(fill, r, -90f, sweep);
                }
            }
        }
    }

    // borderless always-on-top window that never steals focus
    class OverlayForm : Form
    {
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return cp;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }

        public OverlayForm()
        {
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Surface;
        }
    }

    // rounded pill on the taskbar: [ring gauge] 71% 2h14m
    class WidgetForm : OverlayForm
    {
        public string PctText = "-";
        public string Remaining = "";
        public double RingPct = 0;
        public Color SeverityColor = Theme.InkMuted;

        static readonly Font PctFont = new Font("Segoe UI Semibold", 10f);
        static readonly Font RemFont = new Font("Segoe UI", 8.5f);
        const int H = 28;
        const int PadX = 11;
        const int RingSize = 15;
        const int Gap = 7;

        public void UpdateData(string pctText, string remaining, double ringPct, Color severity)
        {
            PctText = pctText; Remaining = remaining; RingPct = ringPct; SeverityColor = severity;
            using (Graphics g = CreateGraphics())
            {
                int w = PadX + RingSize + Gap + (int)Math.Ceiling(g.MeasureString(PctText, PctFont).Width);
                if (Remaining.Length > 0)
                    w += 2 + (int)Math.Ceiling(g.MeasureString(Remaining, RemFont).Width);
                w += PadX;
                if (Width != w || Height != H) { Size = new Size(w, H); Theme.ApplyRounded(this, H / 2); }
            }
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Size = new Size(110, H);
            Theme.ApplyRounded(this, H / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath pill = Theme.RoundedRect(r, r.Height / 2))
            {
                using (SolidBrush b = new SolidBrush(Theme.Surface)) g.FillPath(b, pill);
                using (Pen p = new Pen(Theme.Hairline)) g.DrawPath(p, pill);
            }

            float t = 2.6f;
            RectangleF ring = new RectangleF(PadX + t / 2, (H - RingSize) / 2f + t / 2, RingSize - t, RingSize - t);
            Theme.DrawRing(g, ring, RingPct, SeverityColor, t);

            float x = PadX + RingSize + Gap;
            Color pctInk = (PctText == "-") ? Theme.InkMuted : Theme.InkPrimary;
            SizeF ps = g.MeasureString(PctText, PctFont);
            using (SolidBrush b = new SolidBrush(pctInk))
                g.DrawString(PctText, PctFont, b, x, (H - ps.Height) / 2f);
            if (Remaining.Length > 0)
            {
                SizeF rs = g.MeasureString(Remaining, RemFont);
                using (SolidBrush b = new SolidBrush(Theme.InkMuted))
                    g.DrawString(Remaining, RemFont, b, x + ps.Width + 2, (H - rs.Height) / 2f + 0.5f);
            }
        }
    }

    // detail card: title, two meter rows (5h / week), footer
    class PopupForm : OverlayForm
    {
        public LastShown Data = null;

        static readonly Font TitleFont = new Font("Segoe UI", 8f);
        static readonly Font LabelFont = new Font("Segoe UI", 9f);
        static readonly Font InfoFont = new Font("Segoe UI", 8f);
        static readonly Font ValueFont = new Font("Segoe UI Semibold", 13f);
        const int W = 320;
        const int Pad = 14;

        public PopupForm()
        {
            BackColor = Theme.SurfaceHi;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Size = new Size(W, 168);
            Theme.ApplyRounded(this, 10);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle rr = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath card = Theme.RoundedRect(rr, 10))
            {
                using (SolidBrush b = new SolidBrush(Theme.SurfaceHi)) g.FillPath(b, card);
                using (Pen p = new Pen(Theme.Hairline)) g.DrawPath(p, card);
            }

            int y = Pad;
            using (SolidBrush muted = new SolidBrush(Theme.InkMuted))
            using (SolidBrush secondary = new SolidBrush(Theme.InkSecondary))
            using (SolidBrush primary = new SolidBrush(Theme.InkPrimary))
            {
                g.DrawString("Claude Code usage", TitleFont, muted, Pad, y);
                y += 22;

                LastShown d = Data;
                if (d == null || d.Pct == null)
                {
                    g.DrawString("no data", LabelFont, secondary, Pad, y + 8);
                    return;
                }

                string reset5 = (d.Reset != null)
                    ? "resets " + d.Reset.Value.ToString("HH:mm") + "  in " + TrayApp.FormatRemaining(d.Reset.Value) : "";
                y = DrawRow(g, y, "5h session", d.Pct.Value, reset5, primary, secondary, muted);

                if (d.WPct != null)
                {
                    string resetW = (d.WReset != null)
                        ? "resets " + d.WReset.Value.ToString("MM/dd HH:mm") + "  in " + TrayApp.FormatRemaining(d.WReset.Value) : "";
                    y = DrawRow(g, y, "week", (int)Math.Round(d.WPct.Value), resetW, primary, secondary, muted);
                }

                g.DrawString(d.Src + "  ·  updated " + d.Upd, InfoFont, muted, Pad, Height - Pad - 14);
            }
        }

        int DrawRow(Graphics g, int y, string label, int pct, string info, SolidBrush primary, SolidBrush secondary, SolidBrush muted)
        {
            g.DrawString(label, LabelFont, secondary, Pad, y);
            if (info.Length > 0)
            {
                SizeF isz = g.MeasureString(info, InfoFont);
                g.DrawString(info, InfoFont, muted, Width - Pad - isz.Width, y + 1.5f);
            }
            y += 20;
            string val = pct + "%";
            g.DrawString(val, ValueFont, primary, Pad - 1, y - 3);
            Color sev = Theme.Severity(pct);
            Rectangle bar = new Rectangle(Pad + 56, y + 7, Width - Pad * 2 - 56, 6);
            Theme.DrawMeter(g, bar, pct, sev);
            return y + 30;
        }
    }

    class ApiData
    {
        public double Pct;
        public object Reset;
        public double? WPct;
        public object WReset;
        public DateTime At;
    }

    class LastShown
    {
        public int? Pct;
        public DateTime? Reset;
        public double? WPct;
        public DateTime? WReset;
        public string Src;
        public string Upd;
    }

    class TrayApp : ApplicationContext
    {
        string jsonPath = @"\\wsl.localhost\Ubuntu-24.04\home\naga3\.claude\usage-monitor\latest.json";
        string credPath = @"\\wsl.localhost\Ubuntu-24.04\home\naga3\.claude\.credentials.json";
        int intervalMs = 5000;
        int apiIntervalSec = 60;
        bool noWidget = false;

        string exeDir;
        string posFile;

        NotifyIcon notify;
        WidgetForm widget;
        PopupForm popup;
        System.Windows.Forms.Timer timer;
        IntPtr prevIconHandle = IntPtr.Zero;

        readonly object apiLock = new object();
        ApiData apiData = null;
        DateTime lastFetch = DateTime.MinValue;
        int fetching = 0;

        LastShown last = null;
        bool hasSavedPos = false;
        Point? dragOff = null;
        Point dragStart;

        public TrayApp()
        {
            exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            posFile = Path.Combine(exeDir, "widget-pos.txt");
            LoadConfig();

            notify = new NotifyIcon();
            notify.Visible = true;

            if (!noWidget) BuildWidget();
            popup = new PopupForm();
            popup.Click += delegate(object s, EventArgs e) { popup.Hide(); };
            BuildMenu();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = intervalMs;
            timer.Tick += delegate(object s, EventArgs e) { MaybeFetchApi(); UpdateTray(); };

            MaybeFetchApi();
            UpdateTray();
            if (widget != null)
            {
                widget.Show();
                if (!hasSavedPos) SnapWidgetToTray();
            }
            timer.Start();
        }

        void LoadConfig()
        {
            // key=value lines in ClaudeUsageTray.cfg next to the exe
            try
            {
                string cfg = Path.Combine(exeDir, "ClaudeUsageTray.cfg");
                if (!File.Exists(cfg)) return;
                foreach (string line in File.ReadAllLines(cfg, Encoding.UTF8))
                {
                    string t = line.Trim();
                    int eq = t.IndexOf('=');
                    if (t.StartsWith("#") || eq < 1) continue;
                    string key = t.Substring(0, eq).Trim();
                    string val = t.Substring(eq + 1).Trim();
                    if (key == "JsonPath") jsonPath = val;
                    else if (key == "CredPath") credPath = val;
                    else if (key == "IntervalMs") intervalMs = int.Parse(val);
                    else if (key == "ApiIntervalSec") apiIntervalSec = int.Parse(val);
                    else if (key == "NoWidget") noWidget = (val == "1" || val.ToLower() == "true");
                }
            }
            catch { }
        }

        // ---------- data ----------

        static object Dig(object o, params string[] path)
        {
            foreach (string k in path)
            {
                IDictionary<string, object> d = o as IDictionary<string, object>;
                if (d == null || !d.ContainsKey(k)) return null;
                o = d[k];
            }
            return o;
        }

        static DateTime? ParseReset(object v)
        {
            if (v == null) return null;
            try
            {
                string s = v as string;
                long epoch;
                if (s != null && !long.TryParse(s, out epoch))
                    return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture).LocalDateTime;
                long e = (s != null) ? long.Parse(s) : Convert.ToInt64(v);
                return DateTimeOffset.FromUnixTimeSeconds(e).LocalDateTime;
            }
            catch { return null; }
        }

        public static string FormatRemaining(DateTime target)
        {
            TimeSpan ts = target - DateTime.Now;
            if (ts.TotalSeconds <= 0) return "now";
            if (ts.TotalHours >= 24) return string.Format("{0}d{1}h", (int)Math.Floor(ts.TotalDays), ts.Hours);
            if (ts.TotalMinutes >= 60) return string.Format("{0}h{1:00}m", (int)Math.Floor(ts.TotalHours), ts.Minutes);
            return string.Format("{0}m", (int)Math.Ceiling(ts.TotalMinutes));
        }

        void MaybeFetchApi()
        {
            if ((DateTime.Now - lastFetch).TotalSeconds < apiIntervalSec) return;
            if (Interlocked.CompareExchange(ref fetching, 1, 0) != 0) return;
            lastFetch = DateTime.Now;
            string cred = credPath;
            Task.Run(delegate()
            {
                try
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    object credObj = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(cred, Encoding.UTF8));
                    string token = Dig(credObj, "claudeAiOauth", "accessToken") as string;
                    if (string.IsNullOrEmpty(token)) return;
                    using (HttpClient client = new HttpClient())
                    {
                        client.Timeout = TimeSpan.FromSeconds(5);
                        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + token);
                        client.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                        string body = client.GetStringAsync("https://api.anthropic.com/api/oauth/usage").Result;
                        object r = ser.Deserialize<Dictionary<string, object>>(body);
                        object util = Dig(r, "five_hour", "utilization");
                        if (util == null) return;
                        ApiData snap = new ApiData();
                        snap.Pct = Convert.ToDouble(util, CultureInfo.InvariantCulture);
                        snap.Reset = Dig(r, "five_hour", "resets_at");
                        object wutil = Dig(r, "seven_day", "utilization");
                        if (wutil != null) snap.WPct = Convert.ToDouble(wutil, CultureInfo.InvariantCulture);
                        snap.WReset = Dig(r, "seven_day", "resets_at");
                        snap.At = DateTime.Now;
                        lock (apiLock) { apiData = snap; }
                    }
                }
                catch { } // WSL down / token expired / endpoint gone -> statusline file fallback
                finally { Interlocked.Exchange(ref fetching, 0); }
            });
        }

        // ---------- display ----------

        void UpdateTray()
        {
            int? pct = null;
            DateTime? reset = null;
            double? wpct = null;
            DateTime? wreset = null;
            string upd = "", src = "";

            ApiData api;
            lock (apiLock) { api = apiData; }
            if (api != null && (DateTime.Now - api.At).TotalSeconds < 300)
            {
                pct = (int)Math.Round(api.Pct);
                reset = ParseReset(api.Reset);
                wpct = api.WPct;
                wreset = ParseReset(api.WReset);
                upd = api.At.ToString("HH:mm");
                src = "api";
            }
            else
            {
                try
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    object j = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(jsonPath, Encoding.UTF8));
                    object used = Dig(j, "rate_limits", "five_hour", "used_percentage");
                    if (used != null)
                    {
                        pct = (int)Math.Round(Convert.ToDouble(used, CultureInfo.InvariantCulture));
                        reset = ParseReset(Dig(j, "rate_limits", "five_hour", "resets_at"));
                        object wused = Dig(j, "rate_limits", "seven_day", "used_percentage");
                        if (wused != null)
                        {
                            wpct = Convert.ToDouble(wused, CultureInfo.InvariantCulture);
                            wreset = ParseReset(Dig(j, "rate_limits", "seven_day", "resets_at"));
                        }
                        upd = File.GetLastWriteTime(jsonPath).ToString("HH:mm");
                        src = "file";
                    }
                }
                catch { } // WSL not running / file missing / broken JSON -> keep gray '-'
            }

            LastShown l = new LastShown();
            l.Pct = pct; l.Reset = reset; l.WPct = wpct; l.WReset = wreset; l.Src = src; l.Upd = upd;
            last = l;

            string pctText = "-", remText = "", tip = "Claude usage: no data";
            double ringPct = 0;
            Color sev = Theme.InkMuted;
            string iconNum = "-";
            string wk = (wpct != null) ? " | wk " + ((int)Math.Round(wpct.Value)) + "%" : "";

            if (pct != null)
            {
                if (src == "file" && reset != null && DateTime.Now >= reset.Value)
                {
                    // 5h window already reset; real value arrives on next statusline write
                    pctText = "0%"; iconNum = "0"; ringPct = 0; sev = Theme.InkMuted;
                    tip = string.Format("5h: reset done (was {0}%){1} | {2} {3}", pct, wk, src, upd);
                }
                else
                {
                    pctText = pct.Value + "%";
                    iconNum = pct.Value.ToString();
                    ringPct = pct.Value;
                    sev = Theme.Severity(pct.Value);
                    if (reset != null && (reset.Value - DateTime.Now).TotalSeconds > 0)
                        remText = FormatRemaining(reset.Value);
                    string resetStr = (reset != null) ? " (reset " + reset.Value.ToString("HH:mm") + ")" : "";
                    tip = string.Format("5h {0}%{1}{2} | {3} {4}", pct, resetStr, wk, src, upd);
                }
            }

            SetTrayIcon(iconNum, ringPct, sev, tip);
            if (widget != null) widget.UpdateData(pctText, remText, ringPct, sev);
            if (popup != null)
            {
                popup.Data = last;
                if (popup.Visible) popup.Invalidate();
            }
        }

        // ---------- icon (ring gauge + number) ----------

        void SetTrayIcon(string num, double ringPct, Color severity, string tip)
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                float t = 4.5f;
                RectangleF ring = new RectangleF(t / 2 + 1, t / 2 + 1, 30 - t, 30 - t);
                Theme.DrawRing(g, ring, ringPct, severity, t);
                float fs = (num.Length >= 3) ? 10f : (num.Length == 2) ? 12f : 13f;
                Color ink = (num == "-") ? Theme.InkMuted : Theme.InkPrimary;
                using (Font font = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush brush = new SolidBrush(ink))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(num, font, brush, new RectangleF(0, 0, 32, 33), sf);
                }
            }
            IntPtr hIcon = bmp.GetHicon();
            bmp.Dispose();
            notify.Icon = Icon.FromHandle(hIcon);
            if (tip.Length > 63) tip = tip.Substring(0, 63); // NotifyIcon.Text hard limit
            notify.Text = tip;
            if (prevIconHandle != IntPtr.Zero) Native.DestroyIcon(prevIconHandle);
            prevIconHandle = hIcon;
        }

        // ---------- widget ----------

        void BuildWidget()
        {
            widget = new WidgetForm();

            // saved position wins over auto-snap
            try
            {
                if (File.Exists(posFile))
                {
                    string[] xy = File.ReadAllText(posFile).Split(',');
                    widget.Location = new Point(int.Parse(xy[0]), int.Parse(xy[1]));
                    hasSavedPos = true;
                }
            }
            catch { }

            // drag to move (saved on release); a click without movement toggles the detail popup
            widget.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) { dragOff = e.Location; dragStart = Cursor.Position; }
            };
            widget.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (dragOff != null)
                {
                    Point p = Cursor.Position;
                    widget.Location = new Point(p.X - dragOff.Value.X, p.Y - dragOff.Value.Y);
                }
            };
            widget.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (dragOff == null) return;
                dragOff = null;
                Point p = Cursor.Position;
                int moved = Math.Abs(p.X - dragStart.X) + Math.Abs(p.Y - dragStart.Y);
                if (moved < 5) TogglePopup();
                else
                {
                    try { File.WriteAllText(posFile, widget.Location.X + "," + widget.Location.Y); }
                    catch { }
                }
            };
        }

        void SnapWidgetToTray()
        {
            // place the widget just left of the notification area (^ / IME / battery block)
            try
            {
                Rectangle sb = Screen.PrimaryScreen.Bounds;
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int x = wa.Right - 430; // fallback
                IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
                if (tray != IntPtr.Zero)
                {
                    IntPtr na = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                    Native.RECT rect;
                    if (na != IntPtr.Zero && Native.GetWindowRect(na, out rect) && rect.Left > 0)
                        x = rect.Left - widget.Width - 8;
                }
                int y;
                if (wa.Bottom < sb.Bottom)
                    y = wa.Bottom + (sb.Bottom - wa.Bottom - widget.Height) / 2;
                else
                    y = sb.Bottom - 60; // auto-hide taskbar etc: float above bottom edge
                widget.Location = new Point(x, y);
            }
            catch { }
        }

        // ---------- popup ----------

        void TogglePopup()
        {
            if (popup.Visible) { popup.Hide(); return; }
            popup.Data = last;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int ax = (widget != null) ? widget.Location.X : Cursor.Position.X;
            int x = Math.Max(wa.Left + 8, Math.Min(ax, wa.Right - popup.Width - 8));
            int y = wa.Bottom - popup.Height - 8;
            popup.Location = new Point(x, y);
            popup.Show();
            popup.TopMost = true;
            popup.Invalidate();
        }

        // ---------- menu ----------

        void BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open latest JSON").Click += delegate(object s, EventArgs e)
            {
                try { System.Diagnostics.Process.Start("notepad.exe", jsonPath); } catch { }
            };
            menu.Items.Add("Refresh now").Click += delegate(object s, EventArgs e)
            {
                lastFetch = DateTime.MinValue;
                MaybeFetchApi();
                UpdateTray();
            };
            if (widget != null)
            {
                menu.Items.Add("Show/Hide taskbar text").Click += delegate(object s, EventArgs e)
                {
                    widget.Visible = !widget.Visible;
                };
            }
            menu.Items.Add("-");
            menu.Items.Add("Exit").Click += delegate(object s, EventArgs e)
            {
                timer.Stop();
                notify.Visible = false;
                notify.Dispose();
                if (widget != null) widget.Close();
                if (popup != null) popup.Close();
                ExitThread();
            };
            notify.ContextMenuStrip = menu;
            if (widget != null) widget.ContextMenuStrip = menu;
            notify.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) TogglePopup();
            };
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (Mutex mutex = new Mutex(false, @"Global\ClaudeUsageTrayMutex"))
            {
                if (!mutex.WaitOne(0, false)) return;
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new TrayApp());
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
