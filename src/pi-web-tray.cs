// pi-web-tray: tiny Windows tray supervisor for pi-web (@agegr/pi-web).
// Keeps pi-web alive in the background, reopens the browser on click,
// prevents duplicate servers, auto-restarts crashes, and updates pi-web via npm.
// Built against the in-box .NET Framework (no external runtime, no NuGet).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Native
{
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);
}

// ---------- settings (config.json) ----------

sealed class Config
{
    public int Port = 30141;
    public string Host = "127.0.0.1";
    public bool OpenBrowserAtLogin = false;
    public bool AutoRestart = true;
    public int MaxLogMB = 2;
}

// Minimal reader/writer for the flat JSON object used by config.json.
// Avoids pulling System.Web.Extensions (several MB) into a tray add-on.
static class FlatJson
{
    public static Dictionary<string, string> Parse(string text)
    {
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        SkipWhitespace(text, ref i);
        if (i >= text.Length || text[i] != '{') throw new FormatException("expected '{' at the start");
        i++;
        while (true)
        {
            SkipWhitespace(text, ref i);
            if (i >= text.Length) break;
            if (text[i] == '}') { i++; break; }
            string key = ReadString(text, ref i);
            SkipWhitespace(text, ref i);
            if (i >= text.Length || text[i] != ':') throw new FormatException("expected ':' after \"" + key + "\"");
            i++;
            SkipWhitespace(text, ref i);
            string value;
            if (i < text.Length && text[i] == '"') value = ReadString(text, ref i);
            else value = ReadRawValue(text, ref i);
            map[key] = value;
            SkipWhitespace(text, ref i);
            if (i < text.Length && text[i] == ',') { i++; continue; }
            if (i < text.Length && text[i] == '}') { i++; break; }
            if (i >= text.Length) break;
            throw new FormatException("unexpected character '" + text[i] + "' at offset " + i);
        }
        return map;
    }

    static void SkipWhitespace(string text, ref int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
    }

    static string ReadString(string text, ref int i)
    {
        if (i >= text.Length || text[i] != '"') throw new FormatException("expected a quoted string at offset " + i);
        i++;
        StringBuilder sb = new StringBuilder();
        while (i < text.Length)
        {
            char c = text[i++];
            if (c == '"') return sb.ToString();
            if (c == '\\' && i < text.Length)
            {
                char e = text[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 <= text.Length)
                        {
                            int code;
                            if (int.TryParse(text.Substring(i, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                        }
                        break;
                    default: sb.Append(e); break; // \" \\ \/ \b \f
                }
                continue;
            }
            sb.Append(c);
        }
        throw new FormatException("unterminated string");
    }

    // Numbers, booleans, null, and (defensively) nested containers as raw text.
    static string ReadRawValue(string text, ref int i)
    {
        int start = i;
        int depth = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '[' || c == '{') depth++;
            else if (c == ']' || c == '}') { if (depth == 0) break; depth--; }
            else if (c == ',' && depth == 0) break;
            i++;
        }
        return text.Substring(start, i - start).Trim();
    }

    public static int GetInt(Dictionary<string, string> map, string key, int fallback)
    {
        string raw;
        if (!map.TryGetValue(key, out raw)) return fallback;
        int value;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value)) return fallback;
        return value;
    }

    public static bool GetBool(Dictionary<string, string> map, string key, bool fallback)
    {
        string raw;
        if (!map.TryGetValue(key, out raw)) return fallback;
        string v = raw.Trim().ToLowerInvariant();
        if (v == "true" || v == "1" || v == "yes" || v == "on") return true;
        if (v == "false" || v == "0" || v == "no" || v == "off") return false;
        return fallback;
    }

    public static string GetString(Dictionary<string, string> map, string key, string fallback)
    {
        string raw;
        if (!map.TryGetValue(key, out raw)) return fallback;
        return raw;
    }

    /// <summary>
    /// Reads a "packages" value: an array of "npm:name" strings and/or objects
    /// with a "source" key. Returns the source strings only.
    /// </summary>
    public static List<string> ParsePackageList(string raw)
    {
        List<string> result = new List<string>();
        if (string.IsNullOrEmpty(raw)) return result;
        int i = 0;
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '"')
            {
                result.Add(ReadString(raw, ref i));
                continue;
            }
            if (c == '{')
            {
                string objText = ReadRawValue(raw, ref i);
                try
                {
                    Dictionary<string, string> map = Parse(objText);
                    string source;
                    if (map.TryGetValue("source", out source) || map.TryGetValue("package", out source))
                        result.Add(source);
                }
                catch { }
                continue;
            }
            i++;
        }
        return result;
    }

    public static string Escape(string text)
    {
        StringBuilder sb = new StringBuilder();
        foreach (char c in text)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < ' ') sb.Append(' ');
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

// ---------- tray application ----------

sealed class TrayApp : ApplicationContext
{
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "PiWebTray";
    const int MaxRestartAttempts = 3;
    const int RestartDelaySeconds = 5;
    const int UptimeResetSeconds = 30;

    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Icon iconStopped, iconRunning, iconBusy;
    readonly ToolStripMenuItem statusItem, openItem, restartItem, upgradesItem,
        autoItem, watchdogItem, settingsItem, logItem, exitItem;
    readonly string baseDir, logPath, configPath, setupLogPath;
    UpgradeForm upgradeForm;
    readonly object logLock = new object();

    Config config;
    DateTime configWriteUtc = DateTime.MinValue;
    Process server;
    StreamWriter logWriter;
    long logBytes;
    int busy;                 // 0 = idle, 1 = start/restart/update in progress
    int restartAttempts;
    int activePort;           // port of the server we are supervising
    string activeHost;
    DateTime upSinceUtc = DateTime.MinValue;
    volatile bool stopRequested;
    volatile bool exiting;
    volatile bool crashHandling;
    bool lastUp;

    public TrayApp()
    {
        baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "pi-web-tray");
        logPath = Path.Combine(baseDir, "server.log");
        configPath = Path.Combine(baseDir, "config.json");
        setupLogPath = Path.Combine(baseDir, "setup.log");

        config = LoadConfig();
        activePort = config.Port;
        activeHost = config.Host;

        iconStopped = MakeIcon(Color.FromArgb(150, 155, 165));
        iconRunning = MakeIcon(Color.FromArgb(46, 196, 120));
        iconBusy = MakeIcon(Color.FromArgb(240, 180, 60));

        statusItem = new ToolStripMenuItem("Server: checking...");
        statusItem.Enabled = false;

        openItem = new ToolStripMenuItem("Open Pi Web");
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);
        openItem.Click += delegate { OpenPiWeb(); };

        restartItem = new ToolStripMenuItem("Restart server");
        restartItem.Click += delegate { RestartServer(true); };

        upgradesItem = new ToolStripMenuItem("Upgrades...");
        upgradesItem.Click += delegate { ShowUpgrades(); };

        autoItem = new ToolStripMenuItem("Start with Windows");
        autoItem.CheckOnClick = false;
        autoItem.Checked = IsAutoStartEnabled();
        autoItem.Click += delegate { ToggleAutoStart(); };

        watchdogItem = new ToolStripMenuItem("Auto-restart if it crashes");
        watchdogItem.CheckOnClick = false;
        watchdogItem.Checked = config.AutoRestart;
        watchdogItem.Click += delegate { ToggleWatchdog(); };

        settingsItem = new ToolStripMenuItem("Edit settings (config.json)");
        settingsItem.Click += delegate { ShowConfigFile(); };

        logItem = new ToolStripMenuItem("Open server log");
        logItem.Click += delegate { ShowLogFile(); };

        exitItem = new ToolStripMenuItem("Exit (stop server)");
        exitItem.Click += delegate { StopServerAndExit(); };

        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add(openItem);
        menu.Items.Add(restartItem);
        menu.Items.Add(upgradesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autoItem);
        menu.Items.Add(watchdogItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(logItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        menu.Items.Add(statusItem);

        tray.ContextMenuStrip = menu;
        tray.Icon = iconStopped;
        tray.Text = "Pi Web";
        tray.Visible = true;
        tray.MouseClick += delegate(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) OpenPiWeb();
        };
        tray.DoubleClick += delegate { OpenPiWeb(); };

        timer.Interval = 2000;
        timer.Tick += delegate { Tick(); };
        timer.Start();

        RefreshStatus();
        // Autostart path: install what is missing, then bring the server up silently
        // (unless configured otherwise).
        RunAsync(delegate
        {
            List<string> missing = MissingComponents();
            if (missing.Count > 0)
            {
                if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
                bool installed;
                try { installed = InstallMissingComponents(missing); }
                finally { SetBusy(false, null); }
                if (!installed) { RefreshStatus(); return; }
            }

            if (IsPortOpen() && !config.OpenBrowserAtLogin) return;
            SetBusy(true, "starting...");
            bool ok;
            try { ok = StartServerAndWait(45); }
            finally { SetBusy(false, null); }
            if (ok && config.OpenBrowserAtLogin) LaunchBrowser();
            RefreshStatus();
        });
    }

    // ---------- first-run bootstrap ----------

    const int NpmInstallTimeoutMs = 900000;

    /// <summary>Global npm packages that are not installed yet.</summary>
    static List<string> MissingComponents()
    {
        List<string> missing = new List<string>();
        if (!Paths.PiInstalled) missing.Add(Paths.PiNpmName);
        if (!Paths.PiWebInstalled) missing.Add(Paths.PiWebNpmName);
        return missing;
    }

    static bool IsInstalled(string npmName)
    {
        return npmName == Paths.PiNpmName ? Paths.PiInstalled : Paths.PiWebInstalled;
    }

    static string DisplayName(string npmName)
    {
        if (npmName == Paths.PiNpmName) return "pi (agent)";
        if (npmName == Paths.PiWebNpmName) return "pi-web";
        return npmName;
    }

    /// <summary>
    /// First run: installs the missing global packages with npm.
    /// Returns false when npm is absent or an install failed.
    /// </summary>
    bool InstallMissingComponents(List<string> missing)
    {
        if (Paths.FindNpm() == null)
        {
            LogSetup("[tray] npm not found; install Node.js from https://nodejs.org and start the tray again");
            Balloon("Node.js is required. Install it from nodejs.org, then start Pi Web Tray again.", ToolTipIcon.Error);
            return false;
        }

        foreach (string npmName in missing)
        {
            string display = DisplayName(npmName);
            LogSetup("[tray] first run: installing " + npmName);
            SetBusy(true, "installing " + display + "...");
            Balloon("Installing " + display + " for the first time - this can take a few minutes.", ToolTipIcon.Info);
            int code = Paths.RunNpm("install -g " + npmName + "@latest", NpmInstallTimeoutMs, LogSetup);
            if (code != 0 || !IsInstalled(npmName))
            {
                LogSetup("[tray] installing " + npmName + " failed (exit " + code + ")");
                Balloon("Could not install " + display + ". See " + setupLogPath + ".", ToolTipIcon.Error);
                return false;
            }
            LogSetup("[tray] installed " + npmName);
        }
        return true;
    }

    // ---------- settings ----------

    Config LoadConfig()
    {
        Config fresh = new Config();
        try
        {
            Directory.CreateDirectory(baseDir);
            if (!File.Exists(configPath))
            {
                SaveConfig(fresh);
                configWriteUtc = File.GetLastWriteTimeUtc(configPath);
                return fresh;
            }
            configWriteUtc = File.GetLastWriteTimeUtc(configPath);
            Dictionary<string, string> map = FlatJson.Parse(File.ReadAllText(configPath, Encoding.UTF8));

            fresh.Port = FlatJson.GetInt(map, "port", fresh.Port);
            if (fresh.Port < 1 || fresh.Port > 65535) fresh.Port = 30141;
            fresh.Host = FlatJson.GetString(map, "host", fresh.Host);
            if (string.IsNullOrEmpty(fresh.Host)) fresh.Host = "127.0.0.1";
            fresh.OpenBrowserAtLogin = FlatJson.GetBool(map, "openBrowserAtLogin", fresh.OpenBrowserAtLogin);
            fresh.AutoRestart = FlatJson.GetBool(map, "autoRestart", fresh.AutoRestart);
            fresh.MaxLogMB = FlatJson.GetInt(map, "maxLogMB", fresh.MaxLogMB);
            if (fresh.MaxLogMB < 0 || fresh.MaxLogMB > 1024) fresh.MaxLogMB = 2;
        }
        catch (Exception ex)
        {
            AppendLine("[tray] config.json ignored (" + ex.Message + "); using defaults");
        }
        return fresh;
    }

    void SaveConfig(Config c)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"_help\": \"port, host, openBrowserAtLogin, autoRestart, maxLogMB. Save the file; the tray reloads it automatically.\",");
        sb.AppendLine("  \"port\": " + c.Port + ",");
        sb.AppendLine("  \"host\": \"" + FlatJson.Escape(c.Host) + "\",");
        sb.AppendLine("  \"openBrowserAtLogin\": " + (c.OpenBrowserAtLogin ? "true" : "false") + ",");
        sb.AppendLine("  \"autoRestart\": " + (c.AutoRestart ? "true" : "false") + ",");
        sb.AppendLine("  \"maxLogMB\": " + c.MaxLogMB);
        sb.AppendLine("}");
        Directory.CreateDirectory(baseDir);
        File.WriteAllText(configPath, sb.ToString(), new UTF8Encoding(false));
    }

    void ReloadConfigIfChanged()
    {
        DateTime write;
        try
        {
            if (!File.Exists(configPath)) return;
            write = File.GetLastWriteTimeUtc(configPath);
        }
        catch { return; }
        if (write == configWriteUtc) return;

        Config old = config;
        config = LoadConfig();
        watchdogItem.Checked = config.AutoRestart;
        AppendLine("[tray] settings reloaded (port " + config.Port + ", host " + config.Host + ")");

        if (old.Port != config.Port || !string.Equals(old.Host, config.Host, StringComparison.OrdinalIgnoreCase))
        {
            Balloon("Settings changed: restarting the server.", ToolTipIcon.Info);
            RestartServer(false);
        }
    }

    void ToggleWatchdog()
    {
        config.AutoRestart = !watchdogItem.Checked;
        watchdogItem.Checked = config.AutoRestart;
        try
        {
            SaveConfig(config);
            configWriteUtc = File.GetLastWriteTimeUtc(configPath);
        }
        catch (Exception ex)
        {
            Balloon("Could not save config.json: " + ex.Message, ToolTipIcon.Error);
        }
    }

    // ---------- status / clock ----------

    string BrowserHost
    {
        get
        {
            string h = activeHost;
            if (string.IsNullOrEmpty(h) || h == "0.0.0.0" || h == "*" || h == "::" || h == "[::]") return "127.0.0.1";
            return h;
        }
    }

    string Url { get { return "http://" + BrowserHost + ":" + activePort; } }

    bool IsPortOpen()
    {
        TcpClient client = new TcpClient();
        try
        {
            IAsyncResult ar = client.BeginConnect(BrowserHost, activePort, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(500, false)) return false;
            client.EndConnect(ar);
            return true;
        }
        catch { return false; }
        finally { client.Close(); }
    }

    void Tick()
    {
        ReloadConfigIfChanged();

        bool up = IsPortOpen();
        if (up)
        {
            if (upSinceUtc == DateTime.MinValue) upSinceUtc = DateTime.UtcNow;
            else if ((DateTime.UtcNow - upSinceUtc).TotalSeconds >= UptimeResetSeconds) restartAttempts = 0;
        }
        else upSinceUtc = DateTime.MinValue;

        // Catches crashes of servers this tray did not spawn itself (adopted ones
        // have no Exited handler).
        if (lastUp && !up) HandleCrash("port " + activePort + " closed");
        lastUp = up;

        if (Interlocked.CompareExchange(ref busy, 0, 0) == 1) return; // busy owns the icon/text
        tray.Icon = up ? iconRunning : iconStopped;
        statusItem.Text = up ? "Server: running on port " + activePort : "Server: stopped";
        tray.Text = up ? "Pi Web - running" : "Pi Web - stopped";
    }

    void RefreshStatus()
    {
        if (Interlocked.CompareExchange(ref busy, 0, 0) == 1) return;
        bool up = IsPortOpen();
        tray.Icon = up ? iconRunning : iconStopped;
        statusItem.Text = up ? "Server: running on port " + activePort : "Server: stopped";
        tray.Text = up ? "Pi Web - running" : "Pi Web - stopped";
    }

    void SetBusy(bool state, string label)
    {
        Interlocked.Exchange(ref busy, state ? 1 : 0);
        RunOnUi(delegate
        {
            if (!state) return;
            tray.Icon = iconBusy;
            string text = label == null ? "starting..." : label;
            statusItem.Text = "Server: " + text;
            tray.Text = "Pi Web - " + text;
        });
    }

    // ---------- actions ----------

    void OpenPiWeb()
    {
        if (IsPortOpen()) { LaunchBrowser(); return; }

        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        SetBusy(true, "starting...");
        RunAsync(delegate
        {
            bool ok;
            try { ok = StartServerAndWait(60); }
            finally { SetBusy(false, null); }

            if (ok) LaunchBrowser();
            else Balloon("Pi Web did not start. See the server log.", ToolTipIcon.Error);
            RefreshStatus();
        });
    }

    void RestartServer(bool notify)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        SetBusy(true, "restarting...");
        RunAsync(delegate
        {
            bool ok = false;
            stopRequested = true;
            try
            {
                KillServerProcesses();
                WaitForPort(false, 10);
                lastUp = false; // we killed it on purpose; do not count it as a crash
                stopRequested = false;
                restartAttempts = 0;
                ok = StartServerAndWait(60);
            }
            catch { }
            finally { stopRequested = false; SetBusy(false, null); }

            if (notify)
            {
                if (ok) Balloon("Pi Web restarted.", ToolTipIcon.Info);
                else Balloon("Restart failed. See the server log.", ToolTipIcon.Error);
            }
            RefreshStatus();
        });
    }

    // ---------- upgrades dialog ----------

    void ShowUpgrades()
    {
        if (upgradeForm != null && !upgradeForm.IsDisposed)
        {
            upgradeForm.Activate();
            return;
        }
        upgradeForm = new UpgradeForm(this);
        try { upgradeForm.ShowDialog(); }
        finally
        {
            upgradeForm.Dispose();
            upgradeForm = null;
        }
    }

    /// <summary>Paths and hooks the upgrade dialog needs.</summary>
    internal string PiCliEntry { get { return Paths.PiCliEntry; } }
    internal string NodePath { get { return Paths.FindNode(); } }
    internal string NpmPath { get { return Paths.FindNpm(); } }
    internal string UpgradeLogPath { get { return Path.Combine(baseDir, "upgrade.log"); } }

    internal bool ServerRunning { get { return IsPortOpen(); } }

    internal void LogUpgrade(string line)
    {
        AppendLine("[upgrade] " + line);
    }

    /// <summary>Stops pi-web and keeps the watchdog quiet until ResumeServer runs.</summary>
    internal void SuspendServer()
    {
        stopRequested = true;
        KillServerProcesses();
        WaitForPort(false, 10);
        lastUp = false; // deliberate stop, not a crash
    }

    internal bool ResumeServer()
    {
        restartAttempts = 0;
        stopRequested = false;
        bool ok = StartServerAndWait(60);
        lastUp = IsPortOpen();
        RefreshStatus();
        return ok;
    }

    void ToggleAutoStart()
    {
        bool enable = !autoItem.Checked;
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (enable) key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(RunValueName, false);
            }
            autoItem.Checked = enable;
        }
        catch (Exception ex)
        {
            Balloon("Could not change autostart: " + ex.Message, ToolTipIcon.Error);
        }
    }

    static bool IsAutoStartEnabled()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            {
                return key != null && key.GetValue(RunValueName) != null;
            }
        }
        catch { return false; }
    }

    void ShowConfigFile()
    {
        try
        {
            if (!File.Exists(configPath)) SaveConfig(config);
            Process.Start("notepad.exe", "\"" + configPath + "\"");
        }
        catch (Exception ex)
        {
            Balloon("Could not open config.json: " + ex.Message, ToolTipIcon.Error);
        }
    }

    void ShowLogFile()
    {
        try
        {
            if (File.Exists(logPath)) Process.Start("notepad.exe", "\"" + logPath + "\"");
            else Balloon("No log yet: " + logPath, ToolTipIcon.Info);
        }
        catch { }
    }

    void StopServerAndExit()
    {
        exiting = true;
        stopRequested = true;
        tray.Visible = false;
        timer.Stop();
        try
        {
            KillOwnedServer();
            KillServerProcesses();
            WaitForPort(false, 5);
        }
        catch { }
        lock (logLock) { CloseLogWriter(); }
        tray.Dispose();
        ExitThread();
    }

    // ---------- server process management ----------

    bool StartServerAndWait(int timeoutSeconds)
    {
        if (IsPortOpen()) return true;
        if (!StartServer()) return false;
        return WaitForPort(true, timeoutSeconds);
    }

    bool StartServer()
    {
        if (server != null && !HasExited(server)) return true;

        string node = FindNode();
        string entry = FindPiWebEntry();
        if (node == null || entry == null)
        {
            Balloon("Could not locate node.exe or pi-web. See README.", ToolTipIcon.Error);
            return false;
        }

        try
        {
            OpenLogWriter();
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = node;
            psi.Arguments = "\"" + entry + "\" --no-open -p " + config.Port + " -H " + config.Host;
            psi.WorkingDirectory = Path.GetDirectoryName(entry);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            Process p = new Process();
            p.StartInfo = psi;
            p.OutputDataReceived += AppendLog;
            p.ErrorDataReceived += AppendLog;
            p.EnableRaisingEvents = true;
            p.Exited += OnServerExited;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            server = p;
            activePort = config.Port;
            activeHost = config.Host;
            AppendLine("[tray] started pi-web on " + activeHost + ":" + activePort + " (pid " + p.Id + ")");
            return true;
        }
        catch (Exception ex)
        {
            AppendLine("[tray] failed to start pi-web: " + ex.Message);
            Balloon("Failed to start pi-web: " + ex.Message, ToolTipIcon.Error);
            return false;
        }
    }

    static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch { return true; }
    }

    void OnServerExited(object sender, EventArgs e)
    {
        Process p = (Process)sender;
        string code = SafeExitCode(p);
        AppendLine("[tray] pi-web exited (code " + code + ")");
        if (ReferenceEquals(server, p)) server = null;
        HandleCrash("process exited with code " + code);
    }

    void HandleCrash(string reason)
    {
        if (stopRequested || exiting) return;
        if (crashHandling) return; // another crash is already being handled

        if (!config.AutoRestart)
        {
            AppendLine("[tray] " + reason + "; auto-restart is off, server stays down");
            return;
        }
        if (restartAttempts >= MaxRestartAttempts)
        {
            AppendLine("[tray] " + reason + "; auto-restart gave up after " + MaxRestartAttempts + " attempts");
            Balloon("pi-web keeps crashing and was not restarted. See the log.", ToolTipIcon.Error);
            return;
        }

        crashHandling = true;
        restartAttempts++;
        AppendLine("[tray] " + reason + "; auto-restart " + restartAttempts + "/" + MaxRestartAttempts + " in " + RestartDelaySeconds + "s");
        RunAsync(delegate
        {
            try
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(RestartDelaySeconds);
                while (DateTime.UtcNow < deadline)
                {
                    if (exiting || stopRequested) return;
                    Thread.Sleep(250);
                }
                if (exiting || stopRequested || IsPortOpen()) return;
                if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return; // user action in progress
                SetBusy(true, "restarting...");
                bool ok;
                try { ok = StartServerAndWait(45); }
                finally { SetBusy(false, null); }
                lastUp = IsPortOpen();
                AppendLine(ok ? "[tray] auto-restart ok" : "[tray] auto-restart failed");
                if (!ok && restartAttempts >= MaxRestartAttempts)
                    Balloon("pi-web could not be restarted automatically. See the log.", ToolTipIcon.Error);
                RefreshStatus();
            }
            finally { crashHandling = false; }
        });
    }

    static string SafeExitCode(Process p)
    {
        try { return p.ExitCode.ToString(); }
        catch { return "?"; }
    }

    void KillOwnedServer()
    {
        Process p = server;
        server = null;
        if (p == null) return;
        try
        {
            if (!HasExited(p)) KillTree(p.Id);
        }
        catch { }
        try { p.Dispose(); }
        catch { }
    }

    // Finds the process listening on the supervised port and kills its whole tree.
    // Killing the Next.js child makes pi-web.js exit on its own.
    void KillServerProcesses()
    {
        int pid = FindListeningPid(activePort);
        if (pid > 0) KillTree(pid);

        Process p = server;
        if (p != null && !HasExited(p)) KillTree(p.Id);

        // Safety sweep: any leftover pi-web node process (never touches the tray itself).
        KillPiWebNodeProcesses();
    }

    static int FindListeningPid(int port)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            string netstat = Path.Combine(Environment.SystemDirectory, "netstat.exe");
            psi.FileName = File.Exists(netstat) ? netstat : "netstat.exe";
            psi.Arguments = "-ano -p TCP";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;

            using (Process p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                string suffix = ":" + port;
                foreach (string line in output.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    if (trimmed.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string[] parts = trimmed.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4) continue;
                    if (!parts[1].EndsWith(suffix, StringComparison.Ordinal)) continue;
                    int pid;
                    if (int.TryParse(parts[parts.Length - 1], out pid) && pid > 0) return pid;
                }
            }
        }
        catch { }
        return 0;
    }

    static void KillPiWebNodeProcesses()
    {
        int self = Process.GetCurrentProcess().Id;
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'node.exe'"))
            {
                foreach (ManagementBaseObject obj in searcher.Get())
                {
                    string cmd = obj["CommandLine"] as string;
                    if (string.IsNullOrEmpty(cmd)) continue;
                    string lower = cmd.ToLowerInvariant();
                    if (lower.IndexOf("pi-web-tray") >= 0) continue; // never our own binary
                    if (lower.IndexOf("pi-web") < 0) continue;
                    int pid = Convert.ToInt32(obj["ProcessId"]);
                    if (pid == self) continue;
                    KillTree(pid);
                }
            }
        }
        catch { }
    }

    static void KillTree(int pid)
    {
        Paths.KillTree(pid);
    }

    bool WaitForPort(bool open, int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (IsPortOpen() == open) return true;
            Thread.Sleep(300);
        }
        return IsPortOpen() == open;
    }

    // ---------- browser ----------

    void LaunchBrowser()
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Url;
            psi.UseShellExecute = true;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open the browser: " + ex.Message, "Pi Web");
        }
    }

    // ---------- locations ----------

    static string FindNode() { return Paths.FindNode(); }

    static string FindPiWebEntry() { return Paths.PiWebEntry; }

    // ---------- logging ----------

    void OpenLogWriter()
    {
        lock (logLock)
        {
            CloseLogWriter();
            Directory.CreateDirectory(baseDir);
            FileStream fs = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            logWriter = new StreamWriter(fs);
            logWriter.AutoFlush = true;
            logBytes = 0;
        }
    }

    void CloseLogWriter()
    {
        if (logWriter == null) return;
        try { logWriter.Flush(); logWriter.Dispose(); }
        catch { }
        logWriter = null;
    }

    void AppendLog(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;
        AppendLine(e.Data);
    }

    /// <summary>Writes a bootstrap (first-run install) line to setup.log as well.</summary>
    void LogSetup(string line)
    {
        AppendLine(line);
        try
        {
            Directory.CreateDirectory(baseDir);
            lock (logLock)
                File.AppendAllText(setupLogPath, DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }

    void AppendLine(string line)
    {
        lock (logLock)
        {
            if (logWriter == null) return;
            try
            {
                logWriter.WriteLine(line);
                logBytes += line.Length + 1;
                if (config.MaxLogMB > 0 && logBytes > (long)config.MaxLogMB * 1024L * 1024L)
                {
                    CloseLogWriter();
                    try
                    {
                        string old = logPath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(logPath, old);
                    }
                    catch { }
                    OpenLogWriter();
                    if (logWriter != null) logWriter.WriteLine("[tray] log rotated");
                }
            }
            catch { }
        }
    }

    // ---------- helpers ----------

    void Balloon(string text, ToolTipIcon icon)
    {
        RunOnUi(delegate { tray.ShowBalloonTip(5000, "Pi Web", text, icon); });
    }

    void RunOnUi(MethodInvoker action)
    {
        try
        {
            if (tray.ContextMenuStrip != null && tray.ContextMenuStrip.InvokeRequired)
                tray.ContextMenuStrip.BeginInvoke(action);
            else
                action();
        }
        catch { }
    }

    static void RunAsync(ThreadStart work)
    {
        Thread thread = new Thread(delegate()
        {
            try { work(); }
            catch (Exception ex) { Debug.WriteLine(ex.ToString()); }
        });
        thread.IsBackground = true;
        thread.Start();
    }

    static Icon MakeIcon(Color dot)
    {
        using (Bitmap bmp = new Bitmap(32, 32))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(34, 38, 48)))
                    g.FillEllipse(bg, 1, 1, 30, 30);
                using (Font f = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush white = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("P", f, white, new RectangleF(0, -1, 32, 32), sf);
                }
                using (SolidBrush d = new SolidBrush(dot))
                using (Pen rim = new Pen(Color.FromArgb(34, 38, 48), 2f))
                {
                    g.FillEllipse(d, 20, 20, 12, 12);
                    g.DrawEllipse(rim, 20, 20, 12, 12);
                }
            }
            IntPtr handle = bmp.GetHicon();
            Icon icon = (Icon)Icon.FromHandle(handle).Clone();
            Native.DestroyIcon(handle);
            return icon;
        }
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (Mutex mutex = new Mutex(true, "Local\\PiWebTray.SingleInstance", out created))
        {
            if (!created)
            {
                // Another tray is already supervising pi-web: just reopen the UI.
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = "http://127.0.0.1:30141";
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                }
                catch { }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
            GC.KeepAlive(mutex);
        }
    }
}
