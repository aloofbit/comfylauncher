using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace ComfyLauncher
{
    // CHECK CLIENT, on the Settings tab: a plain text report of the client,
    // written to Logs\ComfyLauncher-report.txt and put on the clipboard, for a
    // player to paste where somebody can help. It compares the client with
    // the manifest's `base`, the download a player starts from
    // (Snapshot-Client.ps1), and names each file ComfyCraft knows by its row.
    //
    // SIZES FOR THE MPQs, HASHES FOR THE REST. The MPQs are 9 GB, and a hash
    // of them would take minutes; an .exe or .dll is hashed.
    //
    // NO USER NAME IN IT. The client is <WoW folder> and the profile is
    // %USERPROFILE%, so a pasted report does not name the player's account.
    static class Report
    {
        public const string Id = "check-client";
        public const string File = @"Logs\ComfyLauncher-report.txt";

        // NOTHING IN HERE MAY CLOSE THE LAUNCHER: Keys.Handle has no catch. A
        // section that fails is one line in the report, and a file that fails
        // is one line in its section.
        public static string Run(App app)
        {
            string text;
            try { text = Build(app).Replace("\n", "\r\n"); }
            catch (Exception e) { return "no report: " + e.Message; }
            string path = Path.Combine(app.Client.Root, File);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            catch (Exception e) { return @"the report could not be written to Logs\: " + e.Message; }
            bool copied;
            try { copied = Terminal.SetClipboardText(text); } catch { copied = false; }
            return copied ? @"report copied, and saved in Logs\" : @"report saved in Logs\, the clipboard was busy";
        }

        static void Safely(StringBuilder o, Action part)
        {
            try { part(); }
            catch (Exception e) { o.Append("  could not be read: ").Append(e.Message).Append('\n'); }
        }

        public static string Build(App app)
        {
            var c = app.Client;
            var o = new StringBuilder();
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Func<string, string> show = p =>
                p.Equals(c.Root, StringComparison.OrdinalIgnoreCase) ? "<WoW folder>"
                : p.StartsWith(c.Root + "\\", StringComparison.OrdinalIgnoreCase) ? @"<WoW folder>\" + p.Substring(c.Root.Length + 1)
                : profile != "" && p.StartsWith(profile, StringComparison.OrdinalIgnoreCase) ? "%USERPROFILE%" + p.Substring(profile.Length)
                : p;

            // A path twice, or none, is the snapshot's fault and not the client's: the first one counts.
            var files = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in app.Base?.Objs("files") ?? new List<Dictionary<string, object>>())
                if (Safe.IsRelative(f.Str("path")) && !files.ContainsKey(f.Str("path"))) files[f.Str("path")] = f;

            o.Append("ComfyLauncher client report\n");
            o.Append("Taken     ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append('\n');
            o.Append("Launcher  ").Append(SelfUpdate.Version).Append(", manifest built ").Append(app.Built == "" ? "?" : app.Built)
             .Append(", from ").Append(show(app.Source)).Append('\n');
            o.Append("Windows   ").Append(WindowsVersion()).Append(Terminal.UnderWine ? ", as Wine reports it" : "").Append('\n');
            if (Terminal.UnderWine) o.Append("Wine      ").Append(Terminal.WineVersion()).Append('\n');
            o.Append("Client    ").Append(show(c.Root)).Append(c.WoWSilicon ? ", WoWSilicon" : "").Append('\n');
            o.Append("Realm     ").Append(Realmlist(c)).Append('\n');
            o.Append("Base      ").Append(app.Base == null ? "none in the manifest" : app.Base.Str("name") + ", taken " + app.Base.Str("taken")).Append('\n');
            foreach (var n in app.Notes) o.Append("Note      ").Append(n).Append('\n');

            Section(o, "Exclusion");
            Safely(o, () => o.Append("  ").Append(Terminal.UnderWine ? "no Defender under Wine" : Defender.Report(c.Root, show)).Append('\n'));

            Section(o, "Exe");
            Safely(o, () => o.Append("  WoW.exe           ").Append(Exe(c, files)).Append('\n'));
            Safely(o, () => o.Append("  VanillaFixes.exe  ").Append(Plain(c, files, "VanillaFixes.exe")).Append('\n'));

            Section(o, "Files");
            Safely(o, () => Files(o, c, files));

            Section(o, "Patches");
            Safely(o, () => Patches(o, app, files));

            Section(o, "Addons");
            Safely(o, () => Addons(o, app));

            Section(o, "DLLs");
            Safely(o, () => Dlls(o, app, files));

            // Once more over all of it: an error message quotes the full path.
            string text = o.ToString();
            text = System.Text.RegularExpressions.Regex.Replace(text, System.Text.RegularExpressions.Regex.Escape(c.Root), "<WoW folder>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (profile != "")
                text = System.Text.RegularExpressions.Regex.Replace(text, System.Text.RegularExpressions.Regex.Escape(profile), "%USERPROFILE%", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return text;
        }

        static void Section(StringBuilder o, string name) => o.Append('\n').Append(name).Append('\n');

        static string Have(Row r)
        {
            // Still on the disk until Start the game sets it aside.
            if (r.Retired) return "retired, Start the game sets it aside";
            switch (r.Have)
            {
                case ComfyLauncher.Have.Current: return "current";
                case ComfyLauncher.Have.Behind:  return "behind";
                case ComfyLauncher.Have.Missing: return "missing";
                case ComfyLauncher.Have.Aside:   return "set aside";
                case ComfyLauncher.Have.Off:     return "off";
                default:                         return "not installed";
            }
        }

        static string Short(string sha) => sha.Length >= 12 ? sha.Substring(0, 12) : sha;

        static string Mb(long bytes) => bytes >= 1048576 ? (bytes / 1048576.0).ToString("0.0") + " MB" : (bytes / 1024) + " KB";

        static string WindowsVersion()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string name = k?.GetValue("ProductName") as string ?? "Windows";
                    string build = k?.GetValue("CurrentBuild") as string ?? "?";
                    // Windows 11 still says Windows 10 in ProductName; the build tells them apart.
                    if (int.TryParse(build, out int b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
                    string display = k?.GetValue("DisplayVersion") as string ?? "";
                    return name + (display != "" ? " " + display : "") + ", build " + build + "." + (k?.GetValue("UBR") ?? "?");
                }
            }
            catch { return Environment.OSVersion.VersionString; }
        }

        static string Realmlist(Client c)
        {
            try
            {
                var line = System.IO.File.ReadAllLines(c.PathOf("realmlist.wtf"))
                    .FirstOrDefault(l => l.Trim().StartsWith("set realmlist", StringComparison.OrdinalIgnoreCase));
                return line == null ? "no set realmlist line in realmlist.wtf" : line.Trim().Substring("set realmlist".Length).Trim().Trim('"');
            }
            catch { return "no realmlist.wtf"; }
        }

        // The base's hash for a path, or "".
        static string BaseSha(Dictionary<string, Dictionary<string, object>> files, string path) =>
            files.TryGetValue(path, out var f) ? f.Str("sha256") : "";

        static string Exe(Client c, Dictionary<string, Dictionary<string, object>> files)
        {
            string path = c.PathOf("WoW.exe");
            if (!System.IO.File.Exists(path)) return "missing";
            var (sha, changed) = Game.ShippedSha(path);
            string idle = changed ? ", idle logout off (one byte, by the launcher)" : "";
            string want = BaseSha(files, "WoW.exe");
            if (want == "") return "sha256 " + Short(sha) + idle + ", no base to compare";
            return sha == want ? "base" + idle : "not the base: sha256 " + Short(sha) + ", " + Mb(new FileInfo(path).Length) + idle;
        }

        static string Plain(Client c, Dictionary<string, Dictionary<string, object>> files, string name)
        {
            string path = c.PathOf(name);
            if (!System.IO.File.Exists(path)) return "missing";
            string sha = Client.Sha256(path), want = BaseSha(files, name);
            if (want == "") return "sha256 " + Short(sha);
            return sha == want ? "base" : "not the base: sha256 " + Short(sha);
        }

        // Each base file as the base has it. A lettered patch, an addon or a
        // dll of ComfyCraft's is told about in its own section.
        static void Files(StringBuilder o, Client c, Dictionary<string, Dictionary<string, object>> files)
        {
            if (files.Count == 0) { o.Append("  no base in the manifest to compare with\n"); return; }
            var odd = new List<string>();
            int theirs = 0;
            foreach (var f in files.Values)
            {
                string rel = f.Str("path");
                try
                {
                    string path = c.PathOf(rel);
                    var info = new FileInfo(path);
                    // WoWSilicon puts its own in place, on purpose (WoWSilicon.cs).
                    if (c.WoWSilicon && (rel.Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase) || rel.Equals("DivxDecoder.dll", StringComparison.OrdinalIgnoreCase)))
                    { theirs++; odd.Add("WoWSilicon's     " + rel); continue; }
                    if (!info.Exists) { odd.Add("missing          " + rel); continue; }
                    if (info.Length != f.Long("size")) { odd.Add("other size       " + rel + ": " + info.Length + ", base " + f.Long("size")); continue; }
                    string want = f.Str("sha256");
                    if (want == "") continue;
                    string sha = rel.Equals("WoW.exe", StringComparison.OrdinalIgnoreCase) ? Game.ShippedSha(path).Sha : Client.Sha256(path);
                    if (sha != want) odd.Add("other contents   " + rel + ": sha256 " + Short(sha));
                }
                catch (Exception e) { odd.Add("could not read   " + rel + ": " + e.Message); }
            }
            o.Append("  ").Append(files.Count - odd.Count).Append(" of ").Append(files.Count).Append(" base files match")
             .Append(theirs > 0 ? ", " + theirs + " are WoWSilicon's" : "").Append('\n');
            foreach (var line in odd) o.Append("  ").Append(line).Append('\n');
        }

        static IEnumerable<Row> Owning(App app, PartType type, Func<Part, bool> match) =>
            app.Rows.Where(r => r.Kind != RowKind.Part && (r.PartList.Any(p => p.Type == type && match(p))
                || (r.ChoicePartList?.Any(l => l.Any(p => p.Type == type && match(p))) ?? false)));

        static string Version(Row r) => r.ChoiceVersion != null ? r.ChoiceVersion[r.Choice] : r.Version;

        // "Label version, state", without the parts that are empty.
        static string Named(Row r, bool label = true) =>
            string.Join(", ", new[] { string.Join(" ", new[] { label ? r.Label : "", Version(r) ?? "" }.Where(s => s != "")), Have(r) }.Where(s => s != ""));

        static void Patches(StringBuilder o, App app, Dictionary<string, Dictionary<string, object>> files)
        {
            string data = app.Client.PathOf("Data");
            if (!Directory.Exists(data)) { o.Append("  no Data folder\n"); return; }
            var mpqs = new DirectoryInfo(data).GetFiles("patch*.mpq").Concat(new DirectoryInfo(data).GetFiles("patch*.mpq.off"))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var f in mpqs)
            {
                string rel = @"Data\" + f.Name, what;
                bool off = f.Name.EndsWith(".off", StringComparison.OrdinalIgnoreCase);
                string name = off ? f.Name.Substring(0, f.Name.Length - 4) : f.Name;
                var m = System.Text.RegularExpressions.Regex.Match(name, @"^patch-([A-Za-z])\.mpq$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (files.TryGetValue(@"Data\" + name, out var b))
                    what = f.Length == b.Long("size") ? "base" : "base file, other size: base " + Mb(b.Long("size"));
                else if (m.Success)
                {
                    char letter = char.ToUpperInvariant(m.Groups[1].Value[0]);
                    var row = Owning(app, PartType.Patch, p => p.Letter == letter).FirstOrDefault();
                    what = row != null ? Named(row) : "not ComfyCraft's";
                }
                else what = "not in the base";
                if (off && !what.EndsWith(", off")) what += ", turned off";
                o.Append("  ").Append(f.Name.PadRight(18)).Append(Mb(f.Length).PadLeft(10)).Append("  ").Append(what).Append('\n');
            }
            if (mpqs.Count == 0) o.Append("  none\n");
        }

        static void Addons(StringBuilder o, App app)
        {
            string dir = app.Client.PathOf(@"Interface\AddOns");
            if (!Directory.Exists(dir)) { o.Append("  no Interface\\AddOns folder\n"); return; }
            var names = Directory.GetDirectories(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var blizzard = names.Where(n => n.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase)).ToList();
            o.Append("  ").Append(blizzard.Count).Append(" Blizzard addons\n");
            foreach (var n in names.Except(blizzard))
            {
                var row = Owning(app, PartType.Addon, p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                string what = row == null || row.Unknown ? "the player's own"
                    : row.Label.Equals(n, StringComparison.OrdinalIgnoreCase) ? Named(row, label: false)
                    : "part of " + row.Label + ", " + Have(row);
                o.Append("  ").Append(n.PadRight(28)).Append(what).Append('\n');
            }
        }

        static void Dlls(StringBuilder o, App app, Dictionary<string, Dictionary<string, object>> files)
        {
            var c = app.Client;
            if (!c.HasDllsFile) o.Append("  no dlls.txt: VanillaFixes loads no mods\n");
            else if (c.EnabledDlls.Count == 0) o.Append("  dlls.txt has no lines\n");
            foreach (var entry in c.EnabledDlls)
            {
                string path = c.PathOf(entry), what;
                if (!System.IO.File.Exists(path)) what = "listed, file missing";
                else
                {
                    string sha = Client.Sha256(path);
                    var row = Owning(app, PartType.Dll, p => p.Name.Equals(entry, StringComparison.OrdinalIgnoreCase)).FirstOrDefault(r => !r.Unknown);
                    if (BaseSha(files, entry) == sha) what = "base";
                    else if (row != null) what = Named(row);
                    else what = "not ComfyCraft's, sha256 " + Short(sha);
                }
                o.Append("  ").Append(entry.PadRight(28)).Append(what).Append('\n');
            }

            // In the folder and not loaded: a mod switched off, or left behind.
            // The base's own support DLLs (d3d9, fmod and the like) load without a line.
            var support = new HashSet<string>(files.Keys.Where(k => k.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !k.Contains("\\"))
                .Where(k => !Owning(app, PartType.Dll, p => p.Name.Equals(k, StringComparison.OrdinalIgnoreCase)).Any()), StringComparer.OrdinalIgnoreCase);
            var idle = Directory.GetFiles(c.Root, "*.dll").Select(Path.GetFileName)
                .Where(n => !support.Contains(n) && !c.EnabledDlls.Contains(n, StringComparer.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (idle.Count > 0) o.Append("  not in dlls.txt: ").Append(string.Join(", ", idle)).Append('\n');

            if (c.DllEnabled("VanillaMultiMonitorFix.dll"))
            {
                string monitor = Path.Combine(c.Root, "VMMFix_preferred_monitor.txt");
                o.Append("  VMMFix_preferred_monitor.txt  ")
                 .Append(System.IO.File.Exists(monitor) ? "there: " + System.IO.File.ReadAllText(monitor).Trim() : "missing (Start the game writes it)").Append('\n');
            }
        }
    }
}
