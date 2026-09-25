using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ComfyLauncher
{
    // One lettered patch in Data\: live as patch-X.mpq, or switched off as
    // patch-X.mpq.off. Turtle's own digits are never listed.
    sealed class DiskPatch
    {
        public char Letter;
        public string Path;
        public bool Enabled;
        public long Size;
    }

    // WHAT IS ON THE CLIENT'S DISK, read once. Nothing here writes. The rules
    // are ComfyLauncher.ps1's, ported: Resolve-Client, Get-ClientPatches,
    // Get-EnabledDlls, Read-CurrentRealm.
    sealed class Client
    {
        public string Root;
        public List<string> DllLines = new List<string>();
        public List<string> EnabledDlls = new List<string>();
        public bool HasDllsFile;
        public Dictionary<char, DiskPatch> Patches = new Dictionary<char, DiskPatch>();

        // Patches the launcher set aside, in its own folder. Known by size,
        // like the live ones, so switching back is a move, not a download.
        public List<DiskPatch> AsidePatches = new List<DiskPatch>();

        public void ReadAside(string home)
        {
            AsidePatches.Clear();
            string dir = System.IO.Path.Combine(home, @"set-aside\Data");
            if (!Directory.Exists(dir)) return;
            foreach (var f in new DirectoryInfo(dir).GetFiles())
            {
                var m = Regex.Match(f.Name, @"^patch-([A-Za-z])", RegexOptions.IgnoreCase);
                if (m.Success) AsidePatches.Add(new DiskPatch { Letter = char.ToUpperInvariant(m.Groups[1].Value[0]), Path = f.FullName, Size = f.Length });
            }
        }
        public List<string> AddOns = new List<string>();
        public string Realm = "";
        public string Patchlist = "";

        // WTF\Config.wtf's SET lines, by name, ignoring case.
        public Dictionary<string, string> Config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool HasConfig;

        // A folder is a client when it holds WoW.exe, or an Interface\AddOns
        // tree: some people rename the exe. An AddOns or Interface folder
        // inside one is accepted too, for a drag and drop.
        public static string Resolve(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string full;
            try { full = System.IO.Path.GetFullPath(path.Trim('"')); } catch { return null; }
            if (!Directory.Exists(full)) return null;
            if (full.Length > 3) full = full.TrimEnd('\\', '/');
            if (File.Exists(System.IO.Path.Combine(full, "WoW.exe"))) return full;

            string leaf = System.IO.Path.GetFileName(full);
            if (leaf.Equals("AddOns", StringComparison.OrdinalIgnoreCase) || leaf.Equals("Interface", StringComparison.OrdinalIgnoreCase))
            {
                string up = leaf.Equals("AddOns", StringComparison.OrdinalIgnoreCase)
                    ? Directory.GetParent(full)?.Parent?.FullName : Directory.GetParent(full)?.FullName;
                if (up != null && File.Exists(System.IO.Path.Combine(up, "WoW.exe"))) return up;
            }
            if (Directory.Exists(System.IO.Path.Combine(full, @"Interface\AddOns"))) return full;
            return null;
        }

        // Walks up from the launcher's own folder, four levels, so a copy
        // dropped inside the client needs no configuration.
        public static string FindNear(string start)
        {
            string dir = start;
            for (int i = 0; i < 4 && dir != null; i++)
            {
                var found = Resolve(dir);
                if (found != null) return found;
                dir = Directory.GetParent(dir)?.FullName;
            }
            return null;
        }

        public string PathOf(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', '\\'));

        public static Client Read(string root)
        {
            var c = new Client { Root = root };

            string dlls = c.PathOf("dlls.txt");
            if (File.Exists(dlls))
            {
                c.HasDllsFile = true;
                c.DllLines = File.ReadAllLines(dlls).ToList();
                foreach (var line in c.DllLines)
                {
                    string t = line.Trim();
                    if (t != "" && !t.StartsWith("#") && !t.StartsWith(";")) c.EnabledDlls.Add(t);
                }
            }

            string data = c.PathOf("Data");
            if (Directory.Exists(data))
            {
                foreach (var f in new DirectoryInfo(data).GetFiles())
                {
                    var m = Regex.Match(f.Name, @"^patch-([A-Za-z0-9])\.mpq(\.off)?$", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    char letter = char.ToUpperInvariant(m.Groups[1].Value[0]);
                    if (char.IsDigit(letter)) continue;
                    // Both a live and an .off copy: the live one is what the
                    // client reads, so it wins.
                    bool enabled = !m.Groups[2].Success;
                    if (c.Patches.TryGetValue(letter, out var had) && had.Enabled) continue;
                    c.Patches[letter] = new DiskPatch { Letter = letter, Path = f.FullName, Enabled = enabled, Size = f.Length };
                }
            }

            string addons = c.PathOf(@"Interface\AddOns");
            if (Directory.Exists(addons))
                c.AddOns = new DirectoryInfo(addons).GetDirectories()
                    .Select(d => d.Name)
                    .Where(n => !n.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            string realmlist = c.PathOf("realmlist.wtf");
            if (File.Exists(realmlist))
                foreach (var raw in File.ReadAllLines(realmlist))
                {
                    var r = Regex.Match(raw, @"^\s*set\s+realmlist\s+(.+?)\s*$", RegexOptions.IgnoreCase);
                    var p = Regex.Match(raw, @"^\s*set\s+patchlist\s+(.+?)\s*$", RegexOptions.IgnoreCase);
                    if (r.Success) c.Realm = r.Groups[1].Value.Trim('"');
                    else if (p.Success) c.Patchlist = p.Groups[1].Value.Trim('"');
                }
            if (c.Realm != "" && c.Patchlist == "") c.Patchlist = c.Realm;

            string cfg = c.PathOf(@"WTF\Config.wtf");
            if (File.Exists(cfg))
            {
                c.HasConfig = true;
                foreach (var line in File.ReadAllLines(cfg))
                {
                    var m = Regex.Match(line, @"^\s*SET\s+(\S+)\s+""?([^""]*)""?\s*$", RegexOptions.IgnoreCase);
                    if (m.Success) c.Config[m.Groups[1].Value] = m.Groups[2].Value.Trim();
                }
            }

            return c;
        }

        public string ConfigValue(string key) => Config.TryGetValue(key, out var v) ? v : "";

        // THE RESOLUTIONS THE MONITOR REPORTS, never a list anybody typed:
        // Launcher.cs once shipped this machine's 2560x1440 as "fullscreen" for
        // everybody. WMI first, then the adapter's current mode, then whatever
        // Config.wtf already says; biggest first, nothing under 640x480.
        public List<string> Resolutions()
        {
            var sizes = new List<string>();
            try
            {
                using (var q = new System.Management.ManagementObjectSearcher("SELECT HorizontalResolution, VerticalResolution FROM CIM_VideoControllerResolution"))
                    foreach (var r in q.Get())
                        sizes.Add(r["HorizontalResolution"] + "x" + r["VerticalResolution"]);
            }
            catch { }
            if (sizes.Count == 0)
                try
                {
                    using (var q = new System.Management.ManagementObjectSearcher("SELECT CurrentHorizontalResolution, CurrentVerticalResolution FROM Win32_VideoController"))
                        foreach (var v in q.Get())
                            if (v["CurrentHorizontalResolution"] != null) sizes.Add(v["CurrentHorizontalResolution"] + "x" + v["CurrentVerticalResolution"]);
                }
                catch { }
            string now = ConfigValue("gxResolution");
            if (Regex.IsMatch(now, @"^\d+x\d+$")) sizes.Add(now);

            return sizes.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(s => s.Split('x'))
                .Where(p => p.Length == 2 && int.TryParse(p[0], out int w) && int.TryParse(p[1], out int h) && w >= 640 && h >= 480)
                .OrderByDescending(p => int.Parse(p[0])).ThenByDescending(p => int.Parse(p[1]))
                .Select(p => p[0] + "x" + p[1]).ToList();
        }

        public static string Aspect(string size)
        {
            var p = size.Split('x');
            if (p.Length != 2 || !double.TryParse(p[0], out double w) || !double.TryParse(p[1], out double h) || h <= 0) return "";
            double r = w / h;
            foreach (var (v, t) in new[] { (4 / 3.0, "4:3"), (5 / 4.0, "5:4"), (16 / 10.0, "16:10"), (16 / 9.0, "16:9"), (21 / 9.0, "21:9") })
                if (Math.Abs(r - v) / v < 0.02) return t;
            return "";
        }

        public bool DllEnabled(string file) => EnabledDlls.Any(e => e.Equals(file, StringComparison.OrdinalIgnoreCase));

        // Files and bytes under a folder: how an addon installed by hand, or
        // by a launcher that did not record it, is told to be current.
        public static (int Files, long Bytes) DiskPrint(string folder)
        {
            int n = 0; long bytes = 0;
            try
            {
                foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories)) { n++; bytes += f.Length; }
            }
            catch { }
            return (n, bytes);
        }

        public static string Sha256(string file)
        {
            try
            {
                using (var s = File.OpenRead(file))
                using (var h = System.Security.Cryptography.SHA256.Create())
                    return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
            }
            catch { return ""; }
        }
    }
}
