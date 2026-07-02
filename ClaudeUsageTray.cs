// Claude Usage Tray (C# port of ClaudeUsageTray.ps1)
// Shows Claude Code 5-hour session usage as a tray icon + a text strip on the taskbar.
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
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
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
        OverlayForm widget;
        Label wlabel;
        OverlayForm popup;
        Label plabel;
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
            BuildPopup();
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

        static string FormatRemaining(DateTime target)
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
            string text = "-";
            Color color = Color.Gray;
            string tip = "Claude usage: no data";
            string wtext = "5h -";

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

            string wk = (wpct != null) ? " | wk " + ((int)Math.Round(wpct.Value)) + "%" : "";
            if (pct != null)
            {
                if (src == "file" && reset != null && DateTime.Now >= reset.Value)
                {
                    // 5h window already reset; real value arrives on next statusline write
                    text = "0"; color = Color.Gray;
                    tip = string.Format("5h: reset done (was {0}%){1} | {2} {3}", pct, wk, src, upd);
                    wtext = "5h 0%";
                }
                else
                {
                    text = pct.Value.ToString();
                    color = (pct >= 90) ? Color.OrangeRed : (pct >= 70) ? Color.Orange : Color.White;
                    string resetStr = (reset != null) ? " (reset " + reset.Value.ToString("HH:mm") + ")" : "";
                    tip = string.Format("5h {0}%{1}{2} | {3} {4}", pct, resetStr, wk, src, upd);
                    string rem = (reset != null && (reset.Value - DateTime.Now).TotalSeconds > 0)
                        ? " (" + FormatRemaining(reset.Value) + ")" : "";
                    wtext = string.Format("5h {0}%{1}", pct, rem);
                }
            }

            SetTrayIcon(text, color, tip);
            if (wlabel != null)
            {
                wlabel.Text = wtext;
                wlabel.ForeColor = color;
                widget.TopMost = true; // re-assert above the taskbar
            }
            if (popup != null && popup.Visible) plabel.Text = BuildDetailText();
        }

        string BuildDetailText()
        {
            LastShown l = last;
            if (l == null || l.Pct == null) return "Claude usage: no data";
            StringBuilder sb = new StringBuilder();
            sb.Append(string.Format("5h   : {0}%", l.Pct));
            if (l.Reset != null)
                sb.Append(string.Format("   resets {0} (in {1})", l.Reset.Value.ToString("HH:mm"), FormatRemaining(l.Reset.Value)));
            if (l.WPct != null)
            {
                sb.Append(string.Format("\nweek : {0}%", (int)Math.Round(l.WPct.Value)));
                if (l.WReset != null)
                    sb.Append(string.Format("   resets {0} (in {1})", l.WReset.Value.ToString("MM/dd HH:mm"), FormatRemaining(l.WReset.Value)));
            }
            sb.Append(string.Format("\nsrc  : {0}   updated {1}", l.Src, l.Upd));
            return sb.ToString();
        }

        // ---------- icon ----------

        void SetTrayIcon(string text, Color color, string tip)
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                float fontSize = (text.Length >= 3) ? 15f : 20f;
                using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush brush = new SolidBrush(color))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(text, font, brush, new RectangleF(0, 0, 32, 32), sf);
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
            widget = new OverlayForm();
            widget.FormBorderStyle = FormBorderStyle.None;
            widget.ShowInTaskbar = false;
            widget.TopMost = true;
            widget.StartPosition = FormStartPosition.Manual;
            widget.BackColor = Color.FromArgb(16, 16, 16);
            widget.AutoSize = true;
            widget.AutoSizeMode = AutoSizeMode.GrowAndShrink;

            wlabel = new Label();
            wlabel.AutoSize = true;
            wlabel.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            wlabel.ForeColor = Color.Gray;
            wlabel.BackColor = widget.BackColor;
            wlabel.Text = "5h -";
            wlabel.Padding = new Padding(6, 3, 6, 3);
            widget.Controls.Add(wlabel);

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
            wlabel.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) { dragOff = e.Location; dragStart = Cursor.Position; }
            };
            wlabel.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (dragOff != null)
                {
                    Point p = Cursor.Position;
                    widget.Location = new Point(p.X - dragOff.Value.X, p.Y - dragOff.Value.Y);
                }
            };
            wlabel.MouseUp += delegate(object s, MouseEventArgs e)
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

        void BuildPopup()
        {
            popup = new OverlayForm();
            popup.FormBorderStyle = FormBorderStyle.None;
            popup.ShowInTaskbar = false;
            popup.TopMost = true;
            popup.StartPosition = FormStartPosition.Manual;
            popup.BackColor = Color.FromArgb(24, 24, 24);
            popup.AutoSize = true;
            popup.AutoSizeMode = AutoSizeMode.GrowAndShrink;

            plabel = new Label();
            plabel.AutoSize = true;
            plabel.Font = new Font("Consolas", 10f);
            plabel.ForeColor = Color.White;
            plabel.BackColor = popup.BackColor;
            plabel.Padding = new Padding(12, 8, 12, 8);
            popup.Controls.Add(plabel);
            plabel.Click += delegate(object s, EventArgs e) { popup.Hide(); };
        }

        void TogglePopup()
        {
            if (popup.Visible) { popup.Hide(); return; }
            plabel.Text = BuildDetailText();
            popup.PerformLayout();
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int ax = (widget != null) ? widget.Location.X : Cursor.Position.X;
            int x = Math.Max(wa.Left + 8, Math.Min(ax, wa.Right - popup.Width - 8));
            int y = wa.Bottom - popup.Height - 8;
            popup.Location = new Point(x, y);
            popup.Show();
            popup.TopMost = true;
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
            if (wlabel != null) wlabel.ContextMenuStrip = menu;
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
