// Upgrades dialog: shows installed vs latest versions for pi, every configured
// pi package, and pi-web; upgrades the selected component(s) or everything,
// then restarts the pi-web server automatically.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

sealed class UpgradeItem
{
    public string Kind;        // "pi" | "piweb" | "package"
    public string Display;
    public string NpmName;     // null for git/local package sources
    public string Source;      // pi source string, e.g. "npm:@scope/name"
    public string InstallDir;
    public string Installed = "?";
    public string Latest = "";
    public string Status = "not checked";
    public ListViewItem Row;
}

sealed class UpgradeForm : Form
{
    const int PiTimeoutMs = 900000;
    const int NpmTimeoutMs = 900000;

    readonly TrayApp tray;
    readonly List<UpgradeItem> items = new List<UpgradeItem>();
    readonly object fileLock = new object();
    readonly string logPath;

    ListView list;
    TextBox logBox;
    Label statusLabel;
    Button checkButton, selectedButton, allButton, closeButton;
    volatile bool running;

    public UpgradeForm(TrayApp owner)
    {
        tray = owner;
        logPath = tray.UpgradeLogPath;

        Text = "Pi suite upgrades";
        ClientSize = new Size(800, 560);
        MinimumSize = new Size(680, 460);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { }

        list = new ListView();
        list.View = View.Details;
        list.FullRowSelect = true;
        list.GridLines = true;
        list.MultiSelect = true;
        list.HideSelection = false;
        list.Dock = DockStyle.Fill;
        list.Columns.Add("Component", 210);
        list.Columns.Add("Installed", 95);
        list.Columns.Add("Latest", 95);
        list.Columns.Add("Status", 340);
        list.DoubleClick += delegate { UpgradeSelected(); };

        logBox = new TextBox();
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Both;
        logBox.WordWrap = false;
        logBox.Font = new Font("Consolas", 8.75F);
        logBox.BackColor = Color.FromArgb(252, 252, 250);
        logBox.Dock = DockStyle.Bottom;
        logBox.Height = 190;

        Panel bottom = new Panel();
        bottom.Dock = DockStyle.Bottom;
        bottom.Height = 66;

        statusLabel = new Label();
        statusLabel.Dock = DockStyle.Top;
        statusLabel.Height = 24;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusLabel.Padding = new Padding(6, 0, 0, 0);
        statusLabel.Text = "Ready.";

        FlowLayoutPanel buttons = new FlowLayoutPanel();
        buttons.Dock = DockStyle.Fill;
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Padding = new Padding(6, 0, 6, 6);

        closeButton = new Button();
        closeButton.Text = "Close";
        closeButton.Width = 90;
        closeButton.Click += delegate { Close(); };

        allButton = new Button();
        allButton.Text = "Upgrade all";
        allButton.Width = 110;
        allButton.Click += delegate { UpgradeAll(); };

        selectedButton = new Button();
        selectedButton.Text = "Upgrade selected";
        selectedButton.Width = 130;
        selectedButton.Click += delegate { UpgradeSelected(); };

        checkButton = new Button();
        checkButton.Text = "Check for updates";
        checkButton.Width = 140;
        checkButton.Click += delegate { StartCheck(); };

        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(allButton);
        buttons.Controls.Add(selectedButton);
        buttons.Controls.Add(checkButton);

        bottom.Controls.Add(buttons);
        bottom.Controls.Add(statusLabel);

        Controls.Add(list);
        Controls.Add(logBox);
        Controls.Add(bottom);

        AcceptButton = checkButton;
        CancelButton = closeButton;

        BuildItems();
        FormClosing += OnFormClosing;
        Shown += delegate
        {
            TopMost = true;
            Activate();
            BeginInvoke(new MethodInvoker(delegate { TopMost = false; }));
            StartCheck();
        };
    }

    // ---------- item list ----------

    void BuildItems()
    {
        items.Clear();

        UpgradeItem pi = new UpgradeItem();
        pi.Kind = "pi";
        pi.Display = "pi (agent)";
        pi.NpmName = "@earendil-works/pi-coding-agent";
        pi.Source = "pi";
        pi.InstallDir = Paths.PiPackageDir;
        items.Add(pi);

        UpgradeItem web = new UpgradeItem();
        web.Kind = "piweb";
        web.Display = "pi-web";
        web.NpmName = "@agegr/pi-web";
        web.InstallDir = Paths.PiWebPackageDir;
        items.Add(web);

        foreach (string source in ConfiguredPackages())
        {
            UpgradeItem item = new UpgradeItem();
            item.Kind = "package";
            item.Source = source;
            bool isNpm = source.StartsWith("npm:", StringComparison.OrdinalIgnoreCase);
            item.NpmName = isNpm ? source.Substring(4) : null;
            item.Display = isNpm ? item.NpmName : source;
            item.InstallDir = isNpm
                ? Path.Combine(Paths.PiPackagesDir, item.NpmName.Replace('/', Path.DirectorySeparatorChar))
                : null;
            items.Add(item);
        }

        list.BeginUpdate();
        list.Items.Clear();
        foreach (UpgradeItem item in items)
        {
            ListViewItem row = new ListViewItem(item.Display);
            row.SubItems.Add(item.Installed);
            row.SubItems.Add(item.Latest);
            row.SubItems.Add(item.Status);
            row.Tag = item;
            item.Row = row;
            list.Items.Add(row);
        }
        list.EndUpdate();
        if (list.Items.Count > 0) list.Items[0].Selected = true;

        try
        {
            File.WriteAllText(logPath,
                "pi-web-tray upgrade log - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { }
    }

    internal static List<string> ConfiguredPackages()
    {
        List<string> result = new List<string>();
        try
        {
            if (File.Exists(Paths.SettingsPath))
            {
                Dictionary<string, string> map = FlatJson.Parse(File.ReadAllText(Paths.SettingsPath, Encoding.UTF8));
                result = FlatJson.ParsePackageList(FlatJson.GetString(map, "packages", null));
            }
        }
        catch { }

        if (result.Count == 0)
        {
            try
            {
                if (File.Exists(Paths.PiPackagesManifest))
                {
                    Dictionary<string, string> map = FlatJson.Parse(File.ReadAllText(Paths.PiPackagesManifest, Encoding.UTF8));
                    string deps;
                    if (map.TryGetValue("dependencies", out deps))
                    {
                        Dictionary<string, string> depMap = FlatJson.Parse(deps);
                        foreach (string name in depMap.Keys) result.Add("npm:" + name);
                    }
                }
            }
            catch { }
        }

        List<string> unique = new List<string>();
        foreach (string source in result)
        {
            if (!unique.Contains(source)) unique.Add(source);
        }
        return unique;
    }

    // ---------- checking ----------

    void StartCheck()
    {
        RunWork("Checking for updates...", delegate
        {
            CheckAll();
            int outdated = 0;
            foreach (UpgradeItem item in items)
            {
                if (NpmRegistry.IsOutdated(item.Installed, item.Latest)) outdated++;
            }
            if (outdated == 0) Status("Everything is up to date (" + items.Count + " components).");
            else Status(outdated + " of " + items.Count + " components can be updated.");
        });
    }

    void CheckAll()
    {
        foreach (UpgradeItem item in items)
        {
            SetRow(item, null, null, "checking...");
            string installed = Paths.ReadVersion(item.InstallDir);
            item.Installed = installed == null ? "?" : installed;

            if (item.NpmName == null)
            {
                item.Latest = "n/a";
                item.Status = "git or local source (updated by Upgrade all)";
                UpdateRow(item);
                continue;
            }

            string latest = NpmRegistry.LatestVersion(item.NpmName);
            if (latest == null)
            {
                item.Latest = "?";
                item.Status = "check failed: " + (NpmRegistry.LastError == null ? "npm registry unreachable" : NpmRegistry.LastError);
            }
            else
            {
                item.Latest = latest;
                int cmp = installed == null ? -2 : NpmRegistry.CompareVersions(installed, latest);
                if (cmp == -2) item.Status = "not installed locally; update available";
                else if (cmp < 0) item.Status = "update available";
                else if (cmp > 0) item.Status = "newer than npm (local build?)";
                else item.Status = "up to date";
            }
            UpdateRow(item);
        }
    }

    // ---------- upgrading ----------

    void UpgradeSelected()
    {
        List<UpgradeItem> chosen = new List<UpgradeItem>();
        foreach (ListViewItem row in list.SelectedItems)
        {
            UpgradeItem item = row.Tag as UpgradeItem;
            if (item != null && !chosen.Contains(item)) chosen.Add(item);
        }
        if (chosen.Count == 0) return;

        StringBuilder question = new StringBuilder();
        question.AppendLine(chosen.Count == 1 ? "Upgrade this component?" : "Upgrade these " + chosen.Count + " components?");
        question.AppendLine();
        foreach (UpgradeItem item in chosen)
        {
            question.AppendLine("  " + item.Display + "  " + item.Installed +
                (item.Latest.Length > 0 && item.Latest != "?" && item.Latest != "n/a" ? "  ->  " + item.Latest : ""));
        }
        if (MessageBox.Show(this, question.ToString(), "Pi suite upgrades",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        RunWork("Upgrading...", delegate
        {
            foreach (UpgradeItem item in chosen) UpgradeOne(item);
            CheckAll();
            Status("Upgrade finished.");
        });
    }

    void UpgradeAll()
    {
        RunWork("Upgrading...", delegate
        {
            CheckAll();

            UpgradeItem pi = FindItem("pi");
            UpgradeItem web = FindItem("piweb");
            bool packagesOutdated = false;
            foreach (UpgradeItem item in items)
            {
                if (item.Kind == "package" && NpmRegistry.IsOutdated(item.Installed, item.Latest)) packagesOutdated = true;
            }

            foreach (UpgradeItem item in items)
            {
                if (item.NpmName != null && !NpmRegistry.IsOutdated(item.Installed, item.Latest))
                    Log(item.Display + " " + item.Installed + " is already latest; skipped");
            }

            bool piOutdated = pi != null && NpmRegistry.IsOutdated(pi.Installed, pi.Latest);
            bool webOutdated = web != null && NpmRegistry.IsOutdated(web.Installed, web.Latest);

            if (!piOutdated && !packagesOutdated && !webOutdated)
            {
                Status("Everything is up to date.");
                Log("Nothing to upgrade.");
                return;
            }

            if (piOutdated) UpgradeOne(pi);
            if (packagesOutdated) UpgradePackages();
            if (webOutdated) UpgradeOne(web);

            CheckAll();
            Status("Upgrade finished.");
        });
    }

    UpgradeItem FindItem(string kind)
    {
        foreach (UpgradeItem item in items)
        {
            if (item.Kind == kind) return item;
        }
        return null;
    }

    void UpgradeOne(UpgradeItem item)
    {
        string before = Paths.ReadVersion(item.InstallDir);
        Log("");
        Log("== " + item.Display + " ==");
        Log("installed " + (before == null ? "?" : before) +
            (item.Latest.Length > 0 && item.Latest != "?" ? ", latest " + item.Latest : ""));
        SetRow(item, before == null ? "?" : before, item.Latest, "upgrading...");

        int code;
        if (item.Kind == "pi")
        {
            code = RunPi("update --self", PiTimeoutMs);
            if (code == 0) Log("pi updated - restart your running pi session to pick up the new version.");
        }
        else if (item.Kind == "piweb")
        {
            tray.LogUpgrade("stopping pi-web server for the upgrade");
            Log("stopping the pi-web server...");
            tray.SuspendServer();
            try
            {
                code = RunNpm("install -g @agegr/pi-web@latest", NpmTimeoutMs);
            }
            finally
            {
                tray.LogUpgrade("starting pi-web server again");
                Log("starting the pi-web server again...");
                bool started = tray.ResumeServer();
                Log(started ? "pi-web server restarted." : "pi-web server did NOT restart - see the server log.");
            }
        }
        else if (item.Source != null && item.Source.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
        {
            code = RunPi("update --extension \"" + item.Source + "\"", PiTimeoutMs);
            if (code == 0) Log("package updated - it loads on the next pi session.");
        }
        else
        {
            Log("not an npm source; use Upgrade all (pi update --extensions)");
            code = -1;
        }

        string after = Paths.ReadVersion(item.InstallDir);
        bool changed = !string.Equals(before, after, StringComparison.Ordinal);
        item.Installed = after == null ? "?" : after;
        if (code == 0)
        {
            if (changed) item.Status = "updated to " + after;
            else if (after != null && !string.IsNullOrEmpty(item.Latest) && NpmRegistry.CompareVersions(after, item.Latest) >= 0)
                item.Status = "already up to date";
            else item.Status = "no change";
        }
        else item.Status = "failed (exit " + code + ")";

        Log(item.Display + ": " + item.Status);
        UpdateRow(item);
    }

    void UpgradePackages()
    {
        Log("");
        Log("== pi packages ==");
        foreach (UpgradeItem item in items)
        {
            if (item.Kind == "package" && NpmRegistry.IsOutdated(item.Installed, item.Latest))
                SetRow(item, item.Installed, item.Latest, "upgrading...");
        }
        int code = RunPi("update --extensions", PiTimeoutMs);
        foreach (UpgradeItem item in items)
        {
            if (item.Kind != "package" || item.NpmName == null) continue;
            string after = Paths.ReadVersion(item.InstallDir);
            item.Installed = after == null ? "?" : after;
            item.Status = code == 0
                ? (NpmRegistry.IsOutdated(after, item.Latest) ? "still outdated" : "up to date")
                : "failed (exit " + code + ")";
            UpdateRow(item);
        }
        Log(code == 0 ? "pi packages updated - they load on the next pi session." : "pi package update failed (exit " + code + ").");
    }

    // ---------- process helpers ----------

    int RunPi(string piArgs, int timeoutMs)
    {
        string node = tray.NodePath;
        string cli = tray.PiCliEntry;
        if (node == null || cli == null || !File.Exists(cli))
        {
            Log("! pi installation not found (" + Paths.PiPackageDir + ")");
            return -1;
        }
        return Paths.RunCommand(node, "\"" + cli + "\" " + piArgs, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), timeoutMs, LogLine);
    }

    int RunNpm(string npmArgs, int timeoutMs)
    {
        return Paths.RunNpm(npmArgs, timeoutMs, LogLine);
    }

    void LogLine(string line)
    {
        Log(line);
    }

    // ---------- ui plumbing ----------

    void RunWork(string label, ThreadStart work)
    {
        if (running) return;
        running = true;
        SetButtonsEnabled(false);
        Status(label);

        Thread thread = new Thread(delegate()
        {
            try { work(); }
            catch (Exception ex) { Log("! " + ex.Message); }
            finally
            {
                running = false;
                Ui(delegate
                {
                    SetButtonsEnabled(true);
                });
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }

    void SetButtonsEnabled(bool enabled)
    {
        checkButton.Enabled = enabled;
        selectedButton.Enabled = enabled;
        allButton.Enabled = enabled;
        closeButton.Enabled = enabled;
    }

    void SetRow(UpgradeItem item, string installed, string latest, string status)
    {
        if (installed != null) item.Installed = installed;
        if (latest != null) item.Latest = latest;
        if (status != null) item.Status = status;
        UpdateRow(item);
    }

    void UpdateRow(UpgradeItem item)
    {
        Ui(delegate
        {
            if (item.Row == null) return;
            item.Row.SubItems[1].Text = item.Installed;
            item.Row.SubItems[2].Text = item.Latest;
            item.Row.SubItems[3].Text = item.Status;
            bool outdated = NpmRegistry.IsOutdated(item.Installed, item.Latest);
            item.Row.BackColor = outdated ? Color.FromArgb(255, 249, 224) : Color.White;
        });
    }

    void Log(string line)
    {
        string text = DateTime.Now.ToString("HH:mm:ss") + "  " + line;
        Ui(delegate { logBox.AppendText(text + Environment.NewLine); });
        lock (fileLock)
        {
            try { File.AppendAllText(logPath, text + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }
    }

    void Status(string text)
    {
        Ui(delegate { statusLabel.Text = " " + text; });
    }

    void Ui(MethodInvoker action)
    {
        try
        {
            if (!IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch { }
    }

    void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (!running) return;
        e.Cancel = true;
        MessageBox.Show(this, "An upgrade is still running. Wait for it to finish.", "Pi suite upgrades",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
