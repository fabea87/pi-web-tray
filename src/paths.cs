// Shared filesystem, process, npm-registry and version helpers
// used by both the tray and the upgrades dialog.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

static class Paths
{
    public static string NpmGlobalModules
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm", "node_modules");
        }
    }

    public static string PiPackageDir
    {
        get { return Path.Combine(NpmGlobalModules, "@earendil-works", "pi-coding-agent"); }
    }

    public static string PiCliEntry
    {
        get { return Path.Combine(PiPackageDir, "dist", "bundle", "cli.js"); }
    }

    public static string PiWebPackageDir
    {
        get { return Path.Combine(NpmGlobalModules, "@agegr", "pi-web"); }
    }

    public static string AgentDir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".pi", "agent");
        }
    }

    public static string SettingsPath { get { return Path.Combine(AgentDir, "settings.json"); } }

    public static string PiPackagesDir { get { return Path.Combine(AgentDir, "npm", "node_modules"); } }

    public static string PiPackagesManifest { get { return Path.Combine(AgentDir, "npm", "package.json"); } }

    public static string PiWebEntry
    {
        get
        {
            List<string> roots = new List<string>();
            roots.Add(NpmGlobalModules);
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm", "node_modules"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node_modules"));
            roots.Add(PiWebPackageDir);
            foreach (string root in roots)
            {
                string entry = Path.Combine(root, "@agegr", "pi-web", "bin", "pi-web.js");
                try { if (File.Exists(entry)) return entry; }
                catch { }
            }
            return null;
        }
    }

    public static string FindNode()
    {
        List<string> candidates = new List<string>();
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Programs", "nodejs", "node.exe"));
        string path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (string dir in path.Split(';'))
            {
                if (dir.Trim().Length > 0) candidates.Add(Path.Combine(dir.Trim(), "node.exe"));
            }
        }
        foreach (string c in candidates)
        {
            try { if (File.Exists(c)) return c; }
            catch { }
        }
        return null;
    }

    public static string FindNpm()
    {
        List<string> candidates = new List<string>();
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "npm.cmd"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm", "npm.cmd"));
        string path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (string dir in path.Split(';'))
            {
                if (dir.Trim().Length > 0) candidates.Add(Path.Combine(dir.Trim(), "npm.cmd"));
            }
        }
        foreach (string c in candidates)
        {
            try { if (File.Exists(c)) return c; }
            catch { }
        }
        return null;
    }

    /// <summary>Reads the "version" field of a package directory's package.json.</summary>
    public static string ReadVersion(string packageDir)
    {
        if (string.IsNullOrEmpty(packageDir)) return null;
        try
        {
            string manifest = Path.Combine(packageDir, "package.json");
            if (!File.Exists(manifest)) return null;
            Dictionary<string, string> map = FlatJson.Parse(File.ReadAllText(manifest, Encoding.UTF8));
            return FlatJson.GetString(map, "version", null);
        }
        catch { return null; }
    }

    public static void KillTree(int pid)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            string taskkill = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
            psi.FileName = File.Exists(taskkill) ? taskkill : "taskkill.exe";
            psi.Arguments = "/PID " + pid + " /T /F";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
            }
        }
        catch { }
    }
}

// Version lookup over the npm registry (plain HTTP: one request per package,
// instead of spawning an npm process per package).
static class NpmRegistry
{
    static string registryBase;

    public static string LastError;

    static NpmRegistry()
    {
        // .NET Framework negotiates TLS 1.0/1.1 by default; the npm registry needs 1.2+.
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
        catch { }
    }

    public static string RegistryBase
    {
        get
        {
            if (registryBase == null) registryBase = ReadRegistryFromNpmrc();
            return registryBase;
        }
    }

    static string ReadRegistryFromNpmrc()
    {
        List<string> files = new List<string>();
        files.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npmrc"));
        files.Add(Path.Combine(NpmRegistryDir, "npmrc"));
        foreach (string file in files)
        {
            try
            {
                if (!File.Exists(file)) continue;
                foreach (string line in File.ReadAllLines(file))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("registry", StringComparison.OrdinalIgnoreCase)) continue;
                    int equals = trimmed.IndexOf('=');
                    if (equals < 0) continue;
                    string value = trimmed.Substring(equals + 1).Trim().Trim('"');
                    if (value.Length > 0) return value.TrimEnd('/');
                }
            }
            catch { }
        }
        return "https://registry.npmjs.org";
    }

    static string NpmRegistryDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"); }
    }

    /// <summary>Latest published version, or null (LastError explains why).</summary>
    public static string LatestVersion(string npmName)
    {
        LastError = null;
        if (string.IsNullOrEmpty(npmName)) { LastError = "no npm name"; return null; }
        try
        {
            string url = RegistryBase + "/" + npmName.Replace("/", "%2f") + "/latest";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.UserAgent = "pi-web-tray";
            request.AutomaticDecompression = DecompressionMethods.GZip;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                Dictionary<string, string> map = FlatJson.Parse(reader.ReadToEnd());
                string version = FlatJson.GetString(map, "version", null);
                if (version == null) LastError = "no version field in registry response";
                return version;
            }
        }
        catch (WebException ex)
        {
            LastError = ex.Message;
            return null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>-1 when a is older than b, 1 when newer, 0 when equal.</summary>
    public static int CompareVersions(string a, string b)
    {
        int[] coreA = Core(a);
        int[] coreB = Core(b);
        for (int i = 0; i < 3; i++)
        {
            if (coreA[i] != coreB[i]) return coreA[i] < coreB[i] ? -1 : 1;
        }
        return ComparePrerelease(Prerelease(a), Prerelease(b));
    }

    static int ComparePrerelease(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 0;
        if (a.Length == 0) return 1;  // 1.0.0 > 1.0.0-beta
        if (b.Length == 0) return -1;

        string[] partsA = a.Split('.');
        string[] partsB = b.Split('.');
        int shared = Math.Min(partsA.Length, partsB.Length);
        for (int i = 0; i < shared; i++)
        {
            int cmp = CompareIdentifier(partsA[i], partsB[i]);
            if (cmp != 0) return cmp;
        }
        return partsA.Length.CompareTo(partsB.Length); // 1.0.0-beta < 1.0.0-beta.1
    }

    static int CompareIdentifier(string a, string b)
    {
        bool numericA = IsNumeric(a);
        bool numericB = IsNumeric(b);
        if (numericA && numericB)
        {
            long valueA, valueB;
            long.TryParse(a, out valueA);
            long.TryParse(b, out valueB);
            return valueA.CompareTo(valueB);
        }
        if (numericA) return -1; // numeric identifiers sort before alphanumeric ones
        if (numericB) return 1;
        return string.CompareOrdinal(a, b);
    }

    static bool IsNumeric(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text)
        {
            if (!char.IsDigit(c)) return false;
        }
        return true;
    }

    static int[] Core(string version)
    {
        int[] parts = new int[3];
        if (version == null) return parts;
        string[] split = version.Split(new char[] { '-', '+' })[0].Split('.');
        for (int i = 0; i < 3 && i < split.Length; i++)
        {
            int value;
            int.TryParse(split[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            parts[i] = value;
        }
        return parts;
    }

    static string Prerelease(string version)
    {
        if (version == null) return "";
        int dash = version.IndexOf('-');
        if (dash < 0) return "";
        int plus = version.IndexOf('+', dash);
        return plus < 0 ? version.Substring(dash + 1) : version.Substring(dash + 1, plus - dash - 1);
    }

    public static bool IsOutdated(string installed, string latest)
    {
        if (string.IsNullOrEmpty(installed) || string.IsNullOrEmpty(latest)) return false;
        return CompareVersions(installed, latest) < 0;
    }
}
