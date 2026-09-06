// DriverWatch — a system-tray app that lists this machine's drivers with their
// version and release date, checks weekly whether each is current (against
// Windows Update and, for known components, the vendor), and offers a one-click
// update on the ones that are behind.
//
// Beacon-family app: it serves the Beacon web look (dark ground, gold accent,
// row-per-item) on a loopback port and lives in the tray with no taskbar entry.
// Windows-only, compiled on the box by the .NET Framework csc (C# 5), same as
// the Bezel agent. Build/deploy/register live in Scripts/.
//
// Target: /target:winexe so there is no console window. Runs in the interactive
// session (the tray needs a desktop), started by a per-user logon task.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
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
        public string Date;           // yyyy-MM-dd, or "" when Windows does not record one
        public string Vendor;
        public string Class;          // DISPLAY, NET, MEDIA, SYSTEM, ...
        public List<string> HardwareIds = new List<string>();

        public bool UpdateAvailable;
        public string UpdateSource;   // "Windows Update", "AMD", "Realtek", ...
        public string UpdateVersion;  // the newer version, when known
        public string UpdateRef;      // a Windows Update UpdateID, or a vendor URL
        public bool CanInstall;       // true = we can install it here; false = opens the vendor page
    }

    static class Program
    {
        const int Port = 48620;
        static NotifyIcon _tray;
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
            try { Directory.CreateDirectory(DataDir()); }
            catch { }

            // A headless mode the weekly logon task can call to refresh in the
            // background without a tray, if we ever want it separate. For now the
            // running app schedules its own weekly check, so this is a manual hook.
            if (args.Length > 0 && args[0] == "--check")
            {
                RunCheck();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            SetupTray();
            StartServer();
            StartScheduler();

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
            open.Click += delegate { OpenDashboard(); };
            ToolStripMenuItem check = new ToolStripMenuItem("Check now");
            check.Click += delegate { ThreadPool.QueueUserWorkItem(delegate { RunCheck(); }); };
            ToolStripMenuItem quit = new ToolStripMenuItem("Quit");
            quit.Click += delegate { Application.Exit(); };
            menu.Items.Add(open);
            menu.Items.Add(check);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quit);
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { OpenDashboard(); };
        }

        static void OpenDashboard()
        {
            try { Process.Start("http://127.0.0.1:" + Port + "/"); }
            catch (Exception e) { Log("open dashboard failed: " + e.Message); }
        }

        // A drawn icon so there is no .ico to ship: a gold dot for the Beacon
        // family, amber-ringed when something needs updating so the tray tells
        // you at a glance.
        static Icon MakeIcon(bool attention)
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
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
            return Icon.FromHandle(h);
        }

        static void RefreshTrayIcon()
        {
            bool attention;
            lock (_gate)
            {
                attention = false;
                foreach (DriverEntry e in _entries) if (e.UpdateAvailable) { attention = true; break; }
            }
            try { if (_tray != null) _tray.Icon = MakeIcon(attention); }
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
            }, null, TimeSpan.FromSeconds(20), TimeSpan.FromHours(6));
        }

        // ---- The check ------------------------------------------------------

        static void RunCheck()
        {
            lock (_gate) { if (_checking) return; _checking = true; _checkError = ""; }
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
                Log("check: done — " + list.Count + " drivers, " + n + " with updates");
            }
            catch (Exception e)
            {
                Log("check failed: " + e.Message);
                _checkError = e.Message;
            }
            finally { lock (_gate) { _checking = false; } }
        }

        // Only the classes worth showing — the ones with real hardware behind them
        // that actually get vendor drivers. Skipping the software/enumerator noise
        // keeps the list about drivers, not the hundred inbox shims.
        static readonly HashSet<string> Interesting = new HashSet<string>(
            new string[] { "DISPLAY", "NET", "MEDIA", "SYSTEM", "HDC", "USB", "BLUETOOTH", "MONITOR", "PRINTER", "IMAGE", "SCSIADAPTER", "KEYBOARD", "MOUSE", "FIRMWARE" },
            StringComparer.OrdinalIgnoreCase);

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
                    if (name.Length == 0 || ver.Length == 0) continue;
                    if (!Interesting.Contains(cls)) continue;

                    string vendor = AsString(mo["DriverProviderName"]);
                    string date = ParseDriverDate(AsString(mo["DriverDate"]));
                    string key = cls + "|" + name + "|" + vendor;

                    DriverEntry existing;
                    if (byKey.TryGetValue(key, out existing))
                    {
                        // Same device enumerated more than once (per-monitor, per-port):
                        // keep the newest driver version we see for it.
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
                int c = string.Compare(a.Class, b.Class, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

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
                store[id] = u;

                DriverEntry match = MatchByTitle(drivers, title);
                if (match != null)
                {
                    match.UpdateAvailable = true;
                    match.UpdateSource = "Windows Update";
                    match.UpdateVersion = "";
                    match.UpdateRef = id;
                    match.CanInstall = true;
                }
                else
                {
                    DriverEntry e = new DriverEntry();
                    e.Id = "wu-" + id;
                    e.Name = title;
                    e.Version = "";
                    e.Date = "";
                    e.Vendor = "Windows Update";
                    e.Class = "UPDATE";
                    e.UpdateAvailable = true;
                    e.UpdateSource = "Windows Update";
                    e.UpdateRef = id;
                    e.CanInstall = true;
                    drivers.Add(e);
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
        // parts we recognise we compare against the vendor directly. Each checker
        // is deliberately small and defensive: a vendor page that has moved or
        // changed shape returns "unknown", never a wrong answer.

        static void CheckVendors(List<DriverEntry> drivers)
        {
            foreach (DriverEntry d in drivers)
            {
                if (d.UpdateAvailable) continue;   // Windows Update already spoke
                VendorResult r = null;

                bool amd = d.Vendor.IndexOf("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) >= 0
                        || d.Vendor.Equals("AMD", StringComparison.OrdinalIgnoreCase);

                if (amd && d.Class.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase))
                    r = CheckAmdGraphics(d.Version);

                if (r != null && r.Available)
                {
                    d.UpdateAvailable = true;
                    d.UpdateSource = r.Source;
                    d.UpdateVersion = r.Version;
                    d.UpdateRef = r.Url;
                    d.CanInstall = false;   // vendor packages install themselves; we open the page
                }
            }
        }

        class VendorResult { public bool Available; public string Version; public string Url; public string Source = "Vendor"; }

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
                if (notesUrl == null) return r;
                string notes = Fetch(notesUrl);
                Match store = Regex.Match(notes ?? "", @"Windows Driver Store Version\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)",
                    RegexOptions.IgnoreCase);
                if (!store.Success) return r;
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
                // Revisions within a month, newest first, plus the bare "YY-M" form.
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
                try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls; } catch { }
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
                // .NET Framework defaults to SSL3/TLS1.0, which modern vendor
                // sites reject at the handshake. Opt into TLS 1.2 (present on 4.8).
                try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls; } catch { }
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DriverWatch/1.0";
                req.Timeout = 15000;
                req.AllowAutoRedirect = true;
                using (WebResponse resp = req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                    return sr.ReadToEnd();
            }
            catch (Exception e) { Log("fetch " + url + ": " + e.Message); return null; }
        }

        // ---- Install --------------------------------------------------------

        static string InstallUpdate(string entryId)
        {
            DriverEntry target = null;
            object wu = null;
            lock (_gate)
            {
                foreach (DriverEntry e in _entries) if (e.Id == entryId) { target = e; break; }
                if (target != null && target.CanInstall && target.UpdateRef != null)
                    _wuUpdates.TryGetValue(target.UpdateRef, out wu);
            }
            if (target == null) return "That driver is no longer in the list; run a check and try again.";
            if (!target.UpdateAvailable) return "No update is pending for that driver.";

            if (!target.CanInstall)
            {
                // Vendor package: we cannot install a .exe unattended reliably, so
                // open the vendor's page for the user to run it.
                try { Process.Start(target.UpdateRef); return "Opened " + target.UpdateSource + "'s download page."; }
                catch (Exception e) { return "Could not open the vendor page: " + e.Message; }
            }

            if (wu == null) return "The Windows Update entry expired; run a check and try again.";
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
                if (code == 2) { ThreadPool.QueueUserWorkItem(delegate { RunCheck(); }); return "Installed. " + (reboot ? "A restart is needed to finish." : ""); }
                return "Windows Update returned result code " + code + " (needs elevation, or the update was superseded).";
            }
            catch (Exception e)
            {
                Log("install failed: " + e.Message);
                return "Install failed: " + e.Message + " (DriverWatch must run elevated to install drivers).";
            }
        }

        // ---- HTTP server ----------------------------------------------------

        static void StartServer()
        {
            Thread th = new Thread(ServerLoop);
            th.IsBackground = true;
            th.Start();
            // Kick off the first check now that the server can show progress.
            ThreadPool.QueueUserWorkItem(delegate { RunCheck(); });
        }

        static void ServerLoop()
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            try { listener.Start(); }
            catch (Exception e) { Log("HTTP listen failed: " + e.Message); return; }
            Log("serving on http://127.0.0.1:" + Port + "/");
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; }
                try { Handle(ctx); }
                catch (Exception e) { Log("request: " + e.Message); }
                finally { try { ctx.Response.Close(); } catch { } }
            }
        }

        static void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath;
            string method = ctx.Request.HttpMethod;

            if (method == "GET" && path == "/") { Write(ctx, "text/html; charset=utf-8", Html); return; }
            if (method == "GET" && path == "/api/drivers") { Write(ctx, "application/json", DriversJson()); return; }
            if (method == "POST" && path == "/api/check") { ThreadPool.QueueUserWorkItem(delegate { RunCheck(); }); Write(ctx, "application/json", "{\"ok\":true}"); return; }
            if (method == "POST" && path == "/api/update")
            {
                string body;
                using (StreamReader sr = new StreamReader(ctx.Request.InputStream)) body = sr.ReadToEnd();
                string id = JsonField(body, "id");
                string msg = InstallUpdate(id);
                Write(ctx, "application/json", "{\"ok\":true,\"message\":" + JsonStr(msg) + "}");
                return;
            }
            ctx.Response.StatusCode = 404;
            Write(ctx, "text/plain", "not found");
        }

        static void Write(HttpListenerContext ctx, string contentType, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        static string DriversJson()
        {
            StringBuilder sb = new StringBuilder();
            lock (_gate)
            {
                sb.Append("{\"host\":").Append(JsonStr(Environment.MachineName.ToLowerInvariant()));
                sb.Append(",\"checking\":").Append(_checking ? "true" : "false");
                sb.Append(",\"lastCheck\":").Append(JsonStr(_lastCheck == DateTime.MinValue ? "" : _lastCheck.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
                sb.Append(",\"error\":").Append(JsonStr(_checkError));
                sb.Append(",\"drivers\":[");
                for (int i = 0; i < _entries.Count; i++)
                {
                    DriverEntry e = _entries[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(JsonStr(e.Id));
                    sb.Append(",\"name\":").Append(JsonStr(e.Name));
                    sb.Append(",\"version\":").Append(JsonStr(e.Version));
                    sb.Append(",\"date\":").Append(JsonStr(e.Date));
                    sb.Append(",\"vendor\":").Append(JsonStr(e.Vendor));
                    sb.Append(",\"class\":").Append(JsonStr(e.Class));
                    sb.Append(",\"updateAvailable\":").Append(e.UpdateAvailable ? "true" : "false");
                    sb.Append(",\"updateSource\":").Append(JsonStr(e.UpdateSource == null ? "" : e.UpdateSource));
                    sb.Append(",\"updateVersion\":").Append(JsonStr(e.UpdateVersion == null ? "" : e.UpdateVersion));
                    sb.Append(",\"canInstall\":").Append(e.CanInstall ? "true" : "false");
                    sb.Append('}');
                }
                sb.Append("]}");
            }
            return sb.ToString();
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
            string[] pa = (a ?? "").Split('.');
            string[] pb = (b ?? "").Split('.');
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

        static string JsonStr(string s)
        {
            if (s == null) return "\"\"";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        static string JsonField(string body, string field)
        {
            Match m = Regex.Match(body ?? "", "\"" + Regex.Escape(field) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        // ---- the page (Beacon look) ----------------------------------------

        static string Html = string.Empty;
        static Program()
        {
            Html = BuildHtml();
        }

        static string BuildHtml()
        {
            StringBuilder s = new StringBuilder();
            s.Append("<!doctype html><html><head><meta charset='utf-8'>");
            s.Append("<meta name='viewport' content='width=device-width, initial-scale=1'>");
            s.Append("<title>DriverWatch</title><style>");
            s.Append(":root{color-scheme:dark;}");
            s.Append("body{margin:0;background:#15171c;color:#e8e6e1;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;}");
            s.Append("header{padding:18px 20px;font-size:20px;font-weight:600;border-bottom:1px solid #2a2d34;display:flex;align-items:center;gap:10px;}");
            s.Append("header .dot{width:9px;height:9px;border-radius:50%;background:#f4c95d;}");
            s.Append("header .sub{margin-left:auto;font-size:12px;color:#9a9a9a;font-weight:400;display:flex;align-items:center;gap:12px;}");
            s.Append("header button{background:#23262d;border:1px solid #34373f;color:#e8e6e1;border-radius:6px;padding:5px 11px;font-size:12px;cursor:pointer;}");
            s.Append(".wrap{max-width:820px;margin:0 auto;padding:8px 12px 40px;}");
            s.Append(".hd,.row{display:grid;grid-template-columns:1fr 150px 110px 130px;gap:12px;align-items:center;padding:11px 12px;}");
            s.Append(".hd{font-size:11px;text-transform:uppercase;letter-spacing:.04em;color:#7d818b;border-bottom:1px solid #2a2d34;position:sticky;top:0;background:#15171c;}");
            s.Append(".row{border-bottom:1px solid #23262d;}");
            s.Append(".row:hover{background:#191c22;}");
            s.Append(".name{font-size:14px;font-weight:600;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}");
            s.Append(".klass{font-size:11px;color:#7d818b;text-transform:uppercase;letter-spacing:.03em;}");
            s.Append(".ver{font-size:12.5px;font-family:ui-monospace,SFMono-Regular,Menlo,monospace;color:#c8c6c0;}");
            s.Append(".date{font-size:12.5px;font-family:ui-monospace,SFMono-Regular,Menlo,monospace;color:#9a9a9a;}");
            s.Append(".pill{font-size:11.5px;font-weight:600;border:none;border-radius:999px;padding:5px 12px;cursor:pointer;}");
            s.Append(".pill.upd{background:#f4c95d;color:#15171c;}");
            s.Append(".pill.upd:hover{background:#ffd86f;}");
            s.Append(".pill.vendor{background:transparent;color:#f4c95d;border:1px solid #6a5a2a;}");
            s.Append(".cur{font-size:11.5px;color:#4c8c5a;}");
            s.Append(".busy{font-size:11.5px;color:#7d818b;}");
            s.Append(".err{color:#ff6b6b;font-size:12px;padding:10px 12px;}");
            s.Append("</style></head><body>");
            s.Append("<header><span class='dot'></span> DriverWatch");
            s.Append("<span class='sub'><span id='meta'></span><button onclick='check()'>Check now</button></span></header>");
            s.Append("<div class='wrap'><div class='hd'><div>Driver</div><div>Version</div><div>Released</div><div style='text-align:right'>Status</div></div>");
            s.Append("<div id='list'></div><div id='err' class='err'></div></div>");
            s.Append("<script>");
            s.Append("function esc(s){return String(s==null?'':s).replace(/[&<>\"]/g,function(c){return{'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;'}[c];});}");
            s.Append("async function load(){var r=await fetch('/api/drivers');var d=await r.json();");
            s.Append("var meta=document.getElementById('meta');meta.textContent=(d.checking?'Checking\\u2026 ':'')+(d.lastCheck?('Checked '+d.lastCheck):'Not checked yet');");
            s.Append("document.getElementById('err').textContent=d.error||'';");
            s.Append("var list=document.getElementById('list');var h='';");
            s.Append("for(var i=0;i<d.drivers.length;i++){var e=d.drivers[i];");
            s.Append("var status;");
            s.Append("if(e.updateAvailable){var label='Update available';");
            s.Append("var tip=e.updateVersion?(e.updateSource+' \\u2192 '+e.updateVersion):e.updateSource;");
            s.Append("if(e.canInstall){status='<button class=\"pill upd\" onclick=\"upd(\\''+e.id+'\\')\" title=\"'+esc(tip)+'\">'+label+'</button>';}");
            s.Append("else{status='<button class=\"pill vendor\" onclick=\"upd(\\''+e.id+'\\')\" title=\"Opens '+esc(tip)+'\">'+label+'</button>';}}");
            s.Append("else{status='<span class=\"cur\">Up to date</span>';}");
            s.Append("h+='<div class=\"row\"><div><div class=\"name\" title=\"'+esc(e.name)+'\">'+esc(e.name)+'</div><div class=\"klass\">'+esc(e.class)+(e.vendor?(' \\u00b7 '+esc(e.vendor)):'')+'</div></div>';");
            s.Append("h+='<div class=\"ver\">'+esc(e.version||'\\u2014')+'</div>';");
            s.Append("h+='<div class=\"date\">'+esc(e.date||'\\u2014')+'</div>';");
            s.Append("h+='<div style=\"text-align:right\">'+status+'</div></div>';}");
            s.Append("list.innerHTML=h;}");
            s.Append("async function upd(id){var r=await fetch('/api/update',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({id:id})});var j=await r.json();if(j.message)alert(j.message);load();}");
            s.Append("async function check(){await fetch('/api/check',{method:'POST'});setTimeout(load,600);}");
            s.Append("load();setInterval(load,4000);");
            s.Append("</script></body></html>");
            return s.ToString();
        }
    }
}
