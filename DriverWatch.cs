// DriverWatch — a system-tray app that lists this machine's AMD and Realtek
// drivers with their version and release date, checks weekly whether each is
// current (against Windows Update and, for the AMD GPU, the vendor), and offers
// a one-click update on the ones that are behind.
//
// Beacon-family app. The window is a native, frameless, always-on-top panel in
// the upper-right — the same look as Beacon Prime's Gio panel (dark 0x202226
// ground, gold 0xf4c95d accent, one row per item) — owner-drawn with GDI+, NOT
// a web page. It lives in the tray with no taskbar entry: left-click the tray
// icon to toggle the panel, right-click for the menu.
//
// Windows-only, compiled on the box by the .NET Framework csc (C# 5), same as
// the Bezel agent. Build/deploy/register live in Scripts/. Target /target:winexe
// so there is no console window; runs in the interactive logon session.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace DriverWatch
{
    // One row in the list: a driver, plus whatever the last check learned about
    // whether it is behind and where a newer one would come from.
    class DriverEntry
    {
        public string Id;             // stable across runs, for the update call
        public string Name;
        public string Version;
        public string Date;           // yyyy-MM-dd, or "" when Windows records none
        public string Vendor;
        public string Class;          // DISPLAY, NET, MEDIA, BLUETOOTH, ...
        public List<string> HardwareIds = new List<string>();

        public bool UpdateAvailable;
        public string UpdateSource;   // "Windows Update", "AMD", ...
        public string UpdateVersion;  // the newer version, when known
        public string UpdateRef;      // a Windows Update UpdateID, or a vendor URL
        public bool CanInstall;       // true = install here; false = opens vendor page
        public bool Unchecked;        // vendor currency could not be determined (e.g. AMD unreachable)
    }

    // An immutable read of state, handed to the panel to paint.
    class Snapshot
    {
        public List<DriverEntry> Rows;
        public bool Checking;
        public DateTime LastCheck;
        public string Error;
    }

    static class Program
    {
        static NotifyIcon _tray;
        static PanelForm _panel;
        static readonly object _gate = new object();
        static List<DriverEntry> _entries = new List<DriverEntry>();
        static DateTime _lastCheck = DateTime.MinValue;
        static bool _checking = false;
        static string _checkError = "";
        // Windows Update update objects kept live so a click can install by id.
        static readonly Dictionary<string, object> _wuUpdates = new Dictionary<string, object>();

        static string DataDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DriverWatch");
        }
        static string LogPath() { return Path.Combine(DataDir(), "driverwatch.log"); }

        [STAThread]
        static void Main(string[] args)
        {
            // Set the TLS floor once, for every entry path (GUI, --check, --render).
            // TLS 1.2 only: never re-enable the deprecated 1.0/1.1, and don't name
            // Tls13 (its enum value isn't present before .NET Framework 4.8).
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; } catch { }

            try { Directory.CreateDirectory(DataDir()); }
            catch { }

            // Headless refresh hook (e.g. for a separate scheduled task). The
            // running app schedules its own weekly check, so this is a manual hook.
            if (args.Length > 0 && args[0] == "--check") { RunCheck(); return; }

            // Headless render hook: paint the panel (after a real check) to a PNG
            // and exit. Used to preview the native window without a live desktop.
            if (args.Length > 1 && args[0] == "--render")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                PanelForm pf = new PanelForm();
                IntPtr hp = pf.Handle;
                RunCheck();
                pf.RenderTo(args[1]);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            SetupTray();
            _panel = new PanelForm();
            // Create the handle now (without showing) so background checks can
            // BeginInvoke a repaint before the panel is first opened.
            IntPtr forceHandle = _panel.Handle;

            StartScheduler();
            ThreadPool.QueueUserWorkItem(delegate { RunCheck(); });   // first check

            Application.Run();

            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        }

        static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath(),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch { }
        }

        // ---- Tray -----------------------------------------------------------

        static void SetupTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = MakeIcon(false);
            _tray.Text = "DriverWatch";
            _tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem open = new ToolStripMenuItem("Open DriverWatch");
            open.Click += delegate { ShowPanel(); };
            ToolStripMenuItem check = new ToolStripMenuItem("Check now");
            check.Click += delegate { RequestCheck(); };
            ToolStripMenuItem quit = new ToolStripMenuItem("Quit");
            quit.Click += delegate { Application.Exit(); };
            menu.Items.Add(open);
            menu.Items.Add(check);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quit);
            _tray.ContextMenuStrip = menu;

            // Left-click toggles the panel; right-click shows the menu (built in).
            _tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) TogglePanel();
            };
        }

        internal static void ShowPanel() { if (_panel != null) _panel.ShowPanel(); }

        internal static void TogglePanel()
        {
            if (_panel == null) return;
            // If the panel was hidden a moment ago by losing focus, this same tray
            // click is the dismiss — don't immediately reopen it.
            if ((DateTime.UtcNow - _panel.LastHidden).TotalMilliseconds < 300) return;
            if (_panel.Visible) _panel.HidePanel();
            else _panel.ShowPanel();
        }

        // Icon.FromHandle wraps a native HICON without owning it, so the handle
        // leaks unless we free it ourselves. We clone into a fully-owned managed
        // Icon and destroy the temporary handle immediately; callers Dispose the
        // returned Icon (RefreshTrayIcon frees the previous one before replacing).
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr handle);

        // A drawn icon so there is no .ico to ship: a gold dot for the Beacon
        // family, amber-ringed when something needs updating so the tray tells
        // you at a glance.
        static Icon MakeIcon(bool attention)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    if (attention)
                    {
                        using (Pen ring = new Pen(Color.FromArgb(0xF2, 0x8B, 0x30), 3f))
                            g.DrawEllipse(ring, 3, 3, 26, 26);
                    }
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(0xF4, 0xC9, 0x5D)))
                        g.FillEllipse(b, 8, 8, 16, 16);
                }
                IntPtr h = bmp.GetHicon();
                try { using (Icon tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone(); }
                finally { DestroyIcon(h); }
            }
        }

        static void RefreshTrayIcon()
        {
            bool attention;
            lock (_gate)
            {
                attention = false;
                foreach (DriverEntry e in _entries) if (e.UpdateAvailable) { attention = true; break; }
            }
            try
            {
                if (_tray != null)
                {
                    Icon old = _tray.Icon;
                    _tray.Icon = MakeIcon(attention);
                    if (old != null) old.Dispose();
                }
            }
            catch { }
        }

        // ---- Scheduler ------------------------------------------------------

        static System.Threading.Timer _timer;
        static void StartScheduler()
        {
            // Check shortly after launch, then re-evaluate every six hours whether
            // a week has passed. Six-hourly wake-ups survive sleep and reboots far
            // better than one seven-day timer that a suspend would silently eat.
            _timer = new System.Threading.Timer(delegate
            {
                if (DateTime.UtcNow - _lastCheck >= TimeSpan.FromDays(7)) RunCheck();
            }, null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
        }

        // ---- The check ------------------------------------------------------

        internal static void RequestCheck()
        {
            ThreadPool.QueueUserWorkItem(delegate { RunCheck(); });
        }

        static void RunCheck()
        {
            lock (_gate) { if (_checking) return; _checking = true; _checkError = ""; }
            if (_panel != null) _panel.PushSnapshot();   // show "Checking…"
            try
            {
                Log("check: starting");
                List<DriverEntry> list = Enumerate();
                Dictionary<string, object> wu = new Dictionary<string, object>();
                try { CheckWindowsUpdate(list, wu); }
                catch (Exception e) { Log("WU check failed: " + e.Message); _checkError = "Windows Update check failed: " + e.Message; }
                try { CheckVendors(list); }
                catch (Exception e) { Log("vendor check failed: " + e.Message); }

                lock (_gate)
                {
                    _entries = list;
                    _wuUpdates.Clear();
                    foreach (KeyValuePair<string, object> kv in wu) _wuUpdates[kv.Key] = kv.Value;
                    _lastCheck = DateTime.UtcNow;
                }
                RefreshTrayIcon();
                int n = 0; foreach (DriverEntry e in list) if (e.UpdateAvailable) n++;
                Log("check: done - " + list.Count + " drivers, " + n + " with updates");
            }
            catch (Exception e)
            {
                Log("check failed: " + e.Message);
                _checkError = e.Message;
            }
            finally
            {
                lock (_gate) { _checking = false; }
                if (_panel != null) _panel.PushSnapshot();
            }
        }

        internal static Snapshot Gather()
        {
            Snapshot s = new Snapshot();
            lock (_gate)
            {
                s.Rows = new List<DriverEntry>(_entries);
                s.Checking = _checking;
                s.LastCheck = _lastCheck;
                s.Error = _checkError;
            }
            return s;
        }

        // Track only what was asked for: the AMD Radeon graphics driver, the AMD
        // High Definition Audio device, and any Realtek driver. Everything else
        // (Microsoft inbox drivers, the monitor, enumerators) is left out.
        static bool Keep(string name, string vendor, string cls)
        {
            string n = name == null ? "" : name;
            string v = vendor == null ? "" : vendor;
            if (Has(n, "Radeon")) return true;                       // AMD GPU
            if (Has(n, "AMD High Definition Audio")) return true;    // AMD HD audio device
            if (Has(n, "Realtek") || Has(v, "Realtek")) return true; // any Realtek
            return false;
        }
        static bool Has(string s, string sub) { return s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0; }

        static List<DriverEntry> Enumerate()
        {
            Dictionary<string, DriverEntry> byKey = new Dictionary<string, DriverEntry>();
            List<DriverEntry> list = new List<DriverEntry>();

            using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                "SELECT DeviceName,DriverVersion,DriverDate,DriverProviderName,DeviceClass,HardWareID FROM Win32_PnPSignedDriver WHERE DriverVersion IS NOT NULL"))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    string name = AsString(mo["DeviceName"]);
                    string ver = AsString(mo["DriverVersion"]);
                    string cls = AsString(mo["DeviceClass"]);
                    string vendor = AsString(mo["DriverProviderName"]);
                    if (name.Length == 0 || ver.Length == 0) continue;
                    if (!Keep(name, vendor, cls)) continue;

                    string date = ParseDriverDate(AsString(mo["DriverDate"]));
                    string key = cls + "|" + name + "|" + vendor;

                    DriverEntry existing;
                    if (byKey.TryGetValue(key, out existing))
                    {
                        // Same device enumerated more than once: keep the newest version.
                        if (CompareVersions(ver, existing.Version) > 0) { existing.Version = ver; existing.Date = date; }
                        continue;
                    }

                    DriverEntry e = new DriverEntry();
                    e.Name = name; e.Version = ver; e.Date = date; e.Vendor = vendor; e.Class = cls;
                    e.Id = "d-" + StableId(key);
                    object hw = mo["HardWareID"];
                    string[] arr = hw as string[];
                    if (arr != null) foreach (string h in arr) if (!string.IsNullOrEmpty(h)) e.HardwareIds.Add(h);
                    byKey[key] = e;
                    list.Add(e);
                }
            }

            list.Sort(delegate(DriverEntry a, DriverEntry b)
            {
                int c = ClassRank(a.Class) - ClassRank(b.Class);
                if (c != 0) return c;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        static int ClassRank(string cls)
        {
            if (Eq(cls, "DISPLAY")) return 0;
            if (Eq(cls, "MEDIA")) return 1;
            if (Eq(cls, "NET")) return 2;
            if (Eq(cls, "BLUETOOTH")) return 3;
            return 4;
        }
        static bool Eq(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        // ---- Windows Update -------------------------------------------------

        static void CheckWindowsUpdate(List<DriverEntry> drivers, Dictionary<string, object> store)
        {
            Type t = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (t == null) return;
            dynamic session = Activator.CreateInstance(t);
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search("IsInstalled=0 and Type='Driver'");
            dynamic updates = result.Updates;
            int count = (int)updates.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic u = updates[i];
                string title = "";
                try { title = (string)u.Title; } catch { }
                string id = "";
                try { id = (string)u.Identity.UpdateID; } catch { }
                if (id.Length == 0) continue;

                // Only surface Windows Update drivers that map to a tracked device;
                // ignore the rest (we deliberately do not show the whole WU list).
                DriverEntry match = MatchByTitle(drivers, title);
                if (match != null)
                {
                    store[id] = u;
                    match.UpdateAvailable = true;
                    match.UpdateSource = "Windows Update";
                    match.UpdateVersion = "";
                    match.UpdateRef = id;
                    match.CanInstall = true;
                }
            }
        }

        // Loose match of a Windows Update driver title to a device row. WU titles
        // read like "AMD - Display - AMD Radeon(TM) Graphics"; if the device name
        // appears in the title, it is the same thing.
        static DriverEntry MatchByTitle(List<DriverEntry> drivers, string title)
        {
            if (string.IsNullOrEmpty(title)) return null;
            string lt = title.ToLowerInvariant();
            foreach (DriverEntry d in drivers)
            {
                if (d.Name.Length >= 6 && lt.IndexOf(d.Name.ToLowerInvariant(), StringComparison.Ordinal) >= 0)
                    return d;
            }
            return null;
        }

        // ---- Vendor checks --------------------------------------------------
        //
        // Best-effort, per known component. Windows Update is conservative and
        // will not always surface a vendor's newest optional driver, so for the
        // AMD GPU we compare against AMD directly. The checker is small and
        // defensive: a vendor page that has moved returns "unknown", never a wrong
        // answer. Realtek and the AMD audio device rely on Windows Update.

        static void CheckVendors(List<DriverEntry> drivers)
        {
            foreach (DriverEntry d in drivers)
            {
                if (d.UpdateAvailable) continue;   // Windows Update already spoke

                bool amd = Has(d.Vendor, "Advanced Micro Devices") || Eq(d.Vendor, "AMD");
                if (amd && Eq(d.Class, "DISPLAY"))
                {
                    VendorResult r = CheckAmdGraphics(d.Version);
                    if (r != null && r.Available)
                    {
                        d.UpdateAvailable = true;
                        d.UpdateSource = r.Source;
                        d.UpdateVersion = r.Version;
                        d.UpdateRef = r.Url;
                        d.CanInstall = false;   // vendor package: open the page, don't run a .exe blind
                    }
                    else if (r == null || !r.Reached)
                    {
                        // We could not reach/parse AMD (their site now bot-walls
                        // plain HTTP). Say so honestly rather than imply "current".
                        d.Unchecked = true;
                    }
                }
            }
        }

        class VendorResult { public bool Available; public bool Reached; public string Version; public string Url; public string Source = "Vendor"; }

        // AMD integrated Vega (Ryzen mobile) rides the Polaris & Vega driver
        // branch. Its release-notes page names the current "Windows Driver Store
        // Version 31.0.NNNNN.NNNN"; compare that to the installed store version.
        //
        // The release-notes INDEX is a Coveo/JS single-page app — the notes links
        // aren't in its static HTML, so scraping it finds nothing. The notes pages
        // themselves are plain server-rendered HTML at a dated URL:
        //   .../release-notes/RN-RAD-WIN-<YY>-<M>-<P>-POLARIS-VEGA.html
        // so we discover the newest by probing that URL from this month backwards
        // (the Polaris/Vega legacy branch is slow-cadence; a small window suffices)
        // and take the first that exists. No SPA, no Coveo token, no guessing.
        const string AmdNotesBase =
            "https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-";

        static VendorResult CheckAmdGraphics(string installed)
        {
            VendorResult r = new VendorResult();
            r.Source = "AMD";
            r.Url = "https://www.amd.com/en/support/download/drivers.html";
            try
            {
                string notesUrl = ProbeLatestAmdNotes();
                if (notesUrl == null) { Log("AMD: no reachable release-notes page (site may be bot-walling)"); return r; }
                string notes = Fetch(notesUrl);
                Match store = Regex.Match(notes == null ? "" : notes, @"Windows Driver Store Version\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)",
                    RegexOptions.IgnoreCase);
                if (!store.Success) { Log("AMD: fetched notes but no store-version match"); return r; }
                // Got a definitive answer from AMD.
                r.Reached = true;
                string latest = store.Groups[1].Value;
                if (CompareVersions(latest, installed) > 0)
                {
                    r.Available = true;
                    r.Version = latest;
                    r.Url = notesUrl;
                }
            }
            catch (Exception e) { Log("AMD check: " + e.Message); }
            return r;
        }

        // Walk dated Polaris/Vega notes URLs newest-first; return the first that
        // exists (HTTP 200 to a HEAD), else null. Bounded to the last 18 months so
        // a total miss (AMD changed the scheme) costs a fixed, small probe budget
        // and yields null rather than a false "up to date".
        static string ProbeLatestAmdNotes()
        {
            DateTime now = DateTime.Now;
            int yy = now.Year % 100, mo = now.Month;
            for (int back = 0; back < 18; back++)
            {
                for (int p = 4; p >= 1; p--)
                {
                    string u = AmdNotesBase + yy + "-" + mo + "-" + p + "-POLARIS-VEGA.html";
                    if (HeadExists(u)) return u;
                }
                string ub = AmdNotesBase + yy + "-" + mo + "-POLARIS-VEGA.html";
                if (HeadExists(ub)) return ub;
                mo--; if (mo == 0) { mo = 12; yy--; }
            }
            return null;
        }

        static bool HeadExists(string url)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "HEAD";
                req.UserAgent = "Mozilla/5.0 (DriverWatch)";
                req.Timeout = 8000;
                req.AllowAutoRedirect = true;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    return resp.StatusCode == HttpStatusCode.OK;
            }
            catch { return false; }   // 404 surfaces as WebException -> not present
        }

        static string Fetch(string url)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DriverWatch/1.0";
                req.Timeout = 15000;
                req.AllowAutoRedirect = true;
                using (WebResponse resp = req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                {
                    // Read with a hard cap. The notes page is small; refusing to
                    // buffer an unbounded body keeps a hostile/broken response from
                    // exhausting memory. 512 KB is far above any real notes page.
                    const int cap = 512 * 1024;
                    char[] buf = new char[16384];
                    StringBuilder sb = new StringBuilder();
                    int read;
                    while (sb.Length < cap && (read = sr.Read(buf, 0, buf.Length)) > 0)
                        sb.Append(buf, 0, read);
                    return sb.ToString();
                }
            }
            catch (Exception e) { Log("fetch " + url + ": " + e.Message); return null; }
        }

        // ---- Install --------------------------------------------------------

        internal static string Install(string entryId)
        {
            DriverEntry target = null;
            object wu = null;
            lock (_gate)
            {
                foreach (DriverEntry e in _entries) if (e.Id == entryId) { target = e; break; }
                if (target != null && target.CanInstall && target.UpdateRef != null)
                    _wuUpdates.TryGetValue(target.UpdateRef, out wu);
            }
            if (target == null) return "That driver is no longer listed; check again.";
            if (!target.UpdateAvailable) return "No update is pending for that driver.";

            if (!target.CanInstall)
            {
                // Vendor package: we cannot install a .exe unattended reliably, so
                // open the vendor's page for the user to run it.
                try { Process.Start(target.UpdateRef); return "Opened " + target.UpdateSource + "'s download page."; }
                catch (Exception e) { return "Could not open the vendor page: " + e.Message; }
            }

            if (wu == null) return "The Windows Update entry expired; check again.";
            try
            {
                Type t = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl");
                dynamic coll = Activator.CreateInstance(t);
                coll.Add((dynamic)wu);

                Type st = Type.GetTypeFromProgID("Microsoft.Update.Session");
                dynamic session = Activator.CreateInstance(st);

                dynamic downloader = session.CreateUpdateDownloader();
                downloader.Updates = coll;
                downloader.Download();

                dynamic installer = session.CreateUpdateInstaller();
                installer.Updates = coll;
                dynamic ir = installer.Install();
                int code = (int)ir.ResultCode;   // 2 == Succeeded
                bool reboot = false; try { reboot = (bool)ir.RebootRequired; } catch { }
                if (code == 2) { ThreadPool.QueueUserWorkItem(delegate { RunCheck(); }); return "Installed. " + (reboot ? "Restart to finish." : ""); }
                return "Windows Update returned code " + code + " (needs elevation, or superseded).";
            }
            catch (Exception e)
            {
                Log("install failed: " + e.Message);
                return "Install failed: " + e.Message;
            }
        }

        // ---- small helpers --------------------------------------------------

        static string AsString(object o) { return o == null ? "" : o.ToString(); }

        static string ParseDriverDate(string wmi)
        {
            // WMI CIM_DATETIME: yyyymmddHHMMSS.mmmmmm+UUU
            if (string.IsNullOrEmpty(wmi) || wmi.Length < 8) return "";
            try
            {
                int y = int.Parse(wmi.Substring(0, 4));
                int m = int.Parse(wmi.Substring(4, 2));
                int d = int.Parse(wmi.Substring(6, 2));
                if (y < 1990 || m < 1 || m > 12 || d < 1 || d > 31) return "";
                return new DateTime(y, m, d).ToString("yyyy-MM-dd");
            }
            catch { return ""; }
        }

        static int CompareVersions(string a, string b)
        {
            string[] pa = (a == null ? "" : a).Split('.');
            string[] pb = (b == null ? "" : b).Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                long va = i < pa.Length ? ParseLong(pa[i]) : 0;
                long vb = i < pb.Length ? ParseLong(pb[i]) : 0;
                if (va != vb) return va < vb ? -1 : 1;
            }
            return 0;
        }
        static long ParseLong(string s) { long v; return long.TryParse(new string(Array.FindAll(s.ToCharArray(), char.IsDigit)), out v) ? v : 0; }

        static string StableId(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in s) { h ^= c; h *= 16777619; }
                return h.ToString("x8");
            }
        }
    }

    // ---- The panel (Beacon Prime look, drawn natively) ----------------------
    //
    // A frameless, opaque, always-on-top window pinned to the upper-right, drawn
    // with GDI+: gold-dotted title, one two-line row per driver, a gold
    // "Update available" pill on the ones that are behind. No web view.
    class PanelForm : Form
    {
        static readonly Color CBg      = Color.FromArgb(0x20, 0x22, 0x26);
        static readonly Color CText    = Color.FromArgb(0xe8, 0xe8, 0xea);
        static readonly Color CMuted   = Color.FromArgb(0x9a, 0x9b, 0xa0);
        static readonly Color CGreen   = Color.FromArgb(0x43, 0xb5, 0x81);
        static readonly Color CGold    = Color.FromArgb(0xf4, 0xc9, 0x5d);
        static readonly Color CGoldHi  = Color.FromArgb(0xff, 0xd8, 0x6f);
        static readonly Color CDivider = Color.FromArgb(22, 255, 255, 255);
        static readonly Color CHover   = Color.FromArgb(10, 255, 255, 255);
        static readonly Color CBorder  = Color.FromArgb(46, 255, 255, 255);
        static readonly Color CErr     = Color.FromArgb(0xff, 0x6b, 0x6b);

        const int W = 424;
        const int Pad = 16;
        const int HeaderH = 54;
        const int RowH = 58;
        const int FooterH = 46;

        readonly Font _fTitle, _fName, _fSub, _fPill, _fMeta, _fLink;

        class PillHit { public Rectangle Rect; public string Id; public string Name; }
        readonly List<PillHit> _pills = new List<PillHit>();
        Rectangle _checkRect = Rectangle.Empty;
        int _hoverRow = -1;

        List<DriverEntry> _rows = new List<DriverEntry>();
        bool _checking;
        DateTime _lastCheck = DateTime.MinValue;
        string _error = "";
        string _status = "";
        DateTime _lastHidden = DateTime.MinValue;
        public DateTime LastHidden { get { return _lastHidden; } }

        public PanelForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Text = "DriverWatch";
            Width = W;
            Height = HeaderH + RowH + FooterH;
            BackColor = CBg;
            DoubleBuffered = true;
            ResizeRedraw = true;
            KeyPreview = true;

            _fTitle = new Font("Segoe UI", 12.5f, FontStyle.Bold);
            _fName  = new Font("Segoe UI", 10f, FontStyle.Bold);
            _fSub   = new Font("Segoe UI", 8.25f, FontStyle.Regular);
            _fPill  = new Font("Segoe UI", 8.25f, FontStyle.Bold);
            _fMeta  = new Font("Segoe UI", 8.25f, FontStyle.Regular);
            _fLink  = new Font("Segoe UI", 8.75f, FontStyle.Bold);
        }

        // Escape (when focused) hides the panel, like dismissing a popover.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { HidePanel(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Click-away dismiss.
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            HidePanel();
        }

        public void ShowPanel()
        {
            SetData(Program.Gather());
            Show();
            TopMost = true;
            BringToFront();
            Activate();
        }

        public void HidePanel()
        {
            _lastHidden = DateTime.UtcNow;
            Hide();
        }

        // Paint the panel at its computed size to a PNG (for headless preview).
        public void RenderTo(string path)
        {
            SetData(Program.Gather());
            using (Bitmap bmp = new Bitmap(Width, Height))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        // Called from any thread when the check state changes.
        public void PushSnapshot()
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { SetData(Program.Gather()); }); }
            catch { }
        }

        void SetData(Snapshot s)
        {
            _rows = s.Rows != null ? s.Rows : new List<DriverEntry>();
            _checking = s.Checking;
            _lastCheck = s.LastCheck;
            _error = s.Error == null ? "" : s.Error;

            int updates = 0, unchecked_ = 0;
            foreach (DriverEntry e in _rows) { if (e.UpdateAvailable) updates++; else if (e.Unchecked) unchecked_++; }
            if (_checking && _rows.Count == 0) _status = "Scanning drivers…";
            else if (_checking) _status = "Checking…";
            else if (_rows.Count == 0) _status = "No matching drivers found.";
            else if (updates > 0) _status = updates + " update" + (updates == 1 ? "" : "s") + " available";
            else if (unchecked_ > 0) _status = _rows.Count + " drivers · " + unchecked_ + " unverified";
            else _status = _rows.Count + " drivers · all current";

            int rowsShown = _rows.Count == 0 ? 1 : _rows.Count;   // a placeholder line when empty
            int h = HeaderH + rowsShown * RowH + FooterH;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            if (h > wa.Height - 24) h = wa.Height - 24;
            Height = h;
            Location = new Point(wa.Right - Width - 12, wa.Top + 12);
            UpdateRegion();
            Invalidate();
        }

        void UpdateRegion()
        {
            using (GraphicsPath p = Rounded(new Rectangle(0, 0, Width, Height), 12))
                Region = new Region(p);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            _pills.Clear();

            Rectangle full = new Rectangle(0, 0, Width, Height);
            using (SolidBrush bg = new SolidBrush(CBg))
            using (GraphicsPath p = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 12))
            {
                g.FillPath(bg, p);
                using (Pen bp = new Pen(CBorder)) g.DrawPath(bp, p);
            }

            // Header: gold dot + title, right-aligned check time.
            using (SolidBrush gold = new SolidBrush(CGold))
                g.FillEllipse(gold, Pad, 22, 9, 9);
            Draw(g, "DriverWatch", _fTitle, new Rectangle(Pad + 17, 15, 220, 26), CText, false);

            string when = _checking
                ? "Checking…"
                : (_lastCheck == DateTime.MinValue ? "Not checked" : "Checked " + _lastCheck.ToLocalTime().ToString("HH:mm"));
            DrawRight(g, when, _fMeta, Width - Pad, 21, W - 240, CMuted);

            using (Pen dv = new Pen(CDivider)) g.DrawLine(dv, Pad, HeaderH, Width - Pad, HeaderH);

            int y = HeaderH;

            if (_rows.Count == 0)
            {
                string msg = _checking ? "Scanning drivers…" : "No matching drivers found.";
                Draw(g, msg, _fSub, new Rectangle(Pad + 17, y + (RowH / 2) - 9, Width - 2 * Pad, 18), CMuted, false);
                y += RowH;
            }
            else
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    DriverEntry d = _rows[i];
                    if (i == _hoverRow)
                        using (SolidBrush hb = new SolidBrush(CHover))
                            g.FillRectangle(hb, 1, y, Width - 2, RowH);

                    // status dot: gold = update, grey = couldn't verify, green = current
                    Color dotc = d.UpdateAvailable ? CGold : (d.Unchecked ? CMuted : CGreen);
                    using (SolidBrush db = new SolidBrush(dotc)) g.FillEllipse(db, Pad, y + 15, 9, 9);

                    // line 1: name (left) + status (right: pill, or "Up to date")
                    int line1 = y + 9;
                    int statusLeft;
                    if (d.UpdateAvailable)
                    {
                        string ptxt = "Update available";
                        Size ts = TextRenderer.MeasureText(ptxt, _fPill);
                        int pw = ts.Width + 22, ph = 23;
                        Rectangle pill = new Rectangle(Width - Pad - pw, line1, pw, ph);
                        bool hot = pill.Contains(PointToClient(Cursor.Position));
                        using (SolidBrush pb = new SolidBrush(hot ? CGoldHi : CGold))
                        using (GraphicsPath pp = Rounded(pill, ph / 2))
                            g.FillPath(pb, pp);
                        Draw(g, ptxt, _fPill, pill, CBg, true);
                        PillHit ph2 = new PillHit(); ph2.Rect = pill; ph2.Id = d.Id; ph2.Name = d.Name;
                        _pills.Add(ph2);
                        statusLeft = pill.Left;
                    }
                    else
                    {
                        string st = d.Unchecked ? "Check unavailable" : "Up to date";
                        Color sc = d.Unchecked ? CMuted : CGreen;
                        Size us = TextRenderer.MeasureText(st, _fSub);
                        statusLeft = Width - Pad - us.Width;
                        DrawRight(g, st, _fSub, Width - Pad, line1 + 2, us.Width + 6, sc);
                    }

                    int nameRight = statusLeft - 12;
                    Draw(g, d.Name, _fName, new Rectangle(Pad + 17, line1, Math.Max(40, nameRight - (Pad + 17)), 20), CText, false);

                    // line 2: version (→ new) / date on the right, "Kind · Vendor" on the left
                    int line2 = y + 31;
                    string meta = d.Version;
                    Color metaCol = CMuted;
                    if (d.UpdateAvailable && d.UpdateVersion != null && d.UpdateVersion.Length > 0)
                    {
                        meta = d.Version + "  →  " + d.UpdateVersion;
                        metaCol = CGold;
                    }
                    else if (d.Date != null && d.Date.Length > 0)
                    {
                        meta = d.Version + "    " + d.Date;
                    }
                    Size mm = TextRenderer.MeasureText(meta, _fMeta);
                    int metaW = Math.Min(mm.Width + 6, 232);
                    int metaLeft = Width - Pad - metaW;
                    DrawRight(g, meta, _fMeta, Width - Pad, line2, metaW, metaCol);

                    string sub = NiceClass(d.Class) + "  ·  " + ShortVendor(d.Vendor);
                    Draw(g, sub, _fSub, new Rectangle(Pad + 17, line2, Math.Max(40, metaLeft - 12 - (Pad + 17)), 18), CMuted, false);

                    using (Pen dv = new Pen(CDivider)) g.DrawLine(dv, Pad, y + RowH, Width - Pad, y + RowH);
                    y += RowH;
                }
            }

            // Footer: status (left) + Check now (right)
            int fy = Height - FooterH;
            using (Pen dv = new Pen(CDivider)) g.DrawLine(dv, Pad, fy, Width - Pad, fy);
            Color statusColor = _error.Length > 0 ? CErr : CMuted;
            string footerLeft = _error.Length > 0 ? _error : _status;
            Draw(g, footerLeft, _fMeta, new Rectangle(Pad, fy + (FooterH / 2) - 9, Width - 2 * Pad - 90, 18), statusColor, false);

            Size cs = TextRenderer.MeasureText("Check now", _fLink);
            _checkRect = new Rectangle(Width - Pad - cs.Width - 10, fy + (FooterH / 2) - (cs.Height / 2) - 3, cs.Width + 10, cs.Height + 6);
            bool chot = _checkRect.Contains(PointToClient(Cursor.Position));
            Draw(g, "Check now", _fLink, _checkRect, chot ? CGoldHi : CGold, true);
        }

        static void Draw(Graphics g, string text, Font f, Rectangle r, Color c, bool center)
        {
            TextFormatFlags fl = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
            if (center) fl |= TextFormatFlags.HorizontalCenter;
            else fl |= TextFormatFlags.Left;
            TextRenderer.DrawText(g, text == null ? "" : text, f, r, c, fl);
        }

        static void DrawRight(Graphics g, string text, Font f, int right, int top, int maxw, Color c)
        {
            Rectangle r = new Rectangle(right - maxw, top, maxw, 18);
            TextRenderer.DrawText(g, text == null ? "" : text, f, r, c,
                TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        static string NiceClass(string cls)
        {
            if (Program0Eq(cls, "DISPLAY")) return "Graphics";
            if (Program0Eq(cls, "MEDIA")) return "Audio";
            if (Program0Eq(cls, "NET")) return "Network";
            if (Program0Eq(cls, "BLUETOOTH")) return "Bluetooth";
            if (Program0Eq(cls, "SYSTEM")) return "System";
            if (cls == null || cls.Length == 0) return "Device";
            return char.ToUpperInvariant(cls[0]) + cls.Substring(1).ToLowerInvariant();
        }

        static string ShortVendor(string v)
        {
            if (v == null) return "";
            if (v.IndexOf("Advanced Micro", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(v, "AMD", StringComparison.OrdinalIgnoreCase)) return "AMD";
            if (v.IndexOf("Realtek", StringComparison.OrdinalIgnoreCase) >= 0) return "Realtek";
            if (v.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0) return "Microsoft";
            return v;
        }

        static bool Program0Eq(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int row = -1;
            if (_rows.Count > 0 && e.Y >= HeaderH && e.Y < Height - FooterH)
                row = (e.Y - HeaderH) / RowH;
            if (row >= _rows.Count) row = -1;
            if (row != _hoverRow) { _hoverRow = row; Invalidate(); }

            bool overClickable = _checkRect.Contains(e.Location);
            if (!overClickable) foreach (PillHit ph in _pills) if (ph.Rect.Contains(e.Location)) { overClickable = true; break; }
            Cursor = overClickable ? Cursors.Hand : Cursors.Default;
            // Repaint hot states (pill / link hover) cheaply.
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverRow != -1) { _hoverRow = -1; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;

            if (_checkRect.Contains(e.Location)) { Program.RequestCheck(); return; }

            foreach (PillHit ph in _pills)
            {
                if (ph.Rect.Contains(e.Location)) { OnPill(ph.Id, ph.Name); return; }
            }
        }

        void OnPill(string id, string name)
        {
            _status = "Updating " + name + "…";
            _error = "";
            Invalidate();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string msg = Program.Install(id);
                try { BeginInvoke((MethodInvoker)delegate { _status = msg; Invalidate(); }); }
                catch { }
            });
        }
    }
}
