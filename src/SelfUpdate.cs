using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace ComfyLauncher
{
    // THE LAUNCHER REPLACING ITSELF, and tidying what earlier launchers left.
    //
    // Windows will not overwrite or delete a running exe, but it will RENAME
    // one: the running ComfyLauncher.exe becomes ComfyLauncher.exe.old, the new
    // one takes the name, and the new one deletes the .old when it starts. The
    // .ps1 launcher did the same, so a launcher updated by either ends up the
    // same way.
    static class SelfUpdate
    {
        // <Version> in the .csproj, which Build-Manifest.ps1 publishes as the
        // manifest's launcher.release.
        public static readonly string Version =
            ((AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                typeof(SelfUpdate).Assembly, typeof(AssemblyInformationalVersionAttribute)))?.InformationalVersion ?? "?";

        public static string Exe => Assembly.GetExecutingAssembly().Location;
        static string Here => Path.GetDirectoryName(Exe);

        // A BUILD IN THE REPO NEVER REPLACES ITSELF: it is newer than any
        // release, or about to be one. Known by a .csproj or Build-Manifest.ps1
        // up to four folders above it.
        public static bool IsSource
        {
            get
            {
                var dir = new DirectoryInfo(Here);
                for (int i = 0; i < 5 && dir != null; i++, dir = dir.Parent)
                    if (dir.GetFiles("*.csproj").Length > 0 || File.Exists(Path.Combine(dir.FullName, "Build-Manifest.ps1"))) return true;
                return false;
            }
        }

        // ONLY UPWARDS. It was any difference, which let a manifest put an
        // older launcher back in place of a newer one. A number that does not
        // read as a version is not an update.
        public static bool Behind(string release) =>
            !IsSource && System.Version.TryParse(release, out var theirs) && System.Version.TryParse(Version, out var mine) && theirs > mine;

        // AT EVERY START, beside a player's copy: the .old an update left, the
        // .cmd and .ps1 that launchers up to 0.8 were made of, and the copy of
        // the .ps1 that the 0.9 to 0.15 exe kept in %LOCALAPPDATA%. The .old
        // may still be running for a second, so it is retried for five.
        public static void CleanUp()
        {
            if (IsSource) return;
            foreach (var f in new[] { "ComfyLauncher.cmd", "ComfyLauncher.ps1" })
                try { File.Delete(Path.Combine(Here, f)); } catch { }
            try
            {
                string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComfyLauncher");
                if (Directory.Exists(cache)) Directory.Delete(cache, true);
            }
            catch { }
            string old = Exe + ".old";
            if (File.Exists(old))
                new Thread(() =>
                {
                    for (int i = 0; i < 25 && File.Exists(old); i++)
                    {
                        try { File.Delete(old); } catch { Thread.Sleep(200); }
                    }
                }) { IsBackground = true }.Start();
        }

        // The row Update available carries while the launcher is behind: it
        // goes first, and on its own, since the launcher that does the rest
        // should be the new one.
        public static Row Row(App app, string url, string sha256)
        {
            var row = new Row { Id = "self", Tab = "", Kind = RowKind.Required, Label = "the launcher", Have = Have.Behind, On = true };
            row.Custom = new System.Collections.Generic.List<Op>
            {
                new Op { Text = "download " + url + ", check it, put its ComfyLauncher.exe in place of this one, and start it", Run = p => Replace(app, url, sha256, p) },
            };
            return row;
        }

        // THE ZIP IS CHECKED BEFORE ANYTHING IN IT RUNS, against the sha256
        // the manifest gives for it. A manifest with no hash updates nothing:
        // an exe that replaces itself and starts is the one download that
        // must never fail open. Build-Manifest.ps1 writes the hash since 1.14.
        static void Replace(App app, string url, string sha256, Action<long> progress)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(sha256 ?? "", "^[0-9a-fA-F]{64}$"))
                throw new IOException("the manifest gives no hash for the new launcher, so it is not installed");
            string downloads = Path.Combine(app.Home, "downloads"), zip = Path.Combine(downloads, "ComfyLauncher.zip");
            if (File.Exists(zip + ".part")) File.Delete(zip + ".part");
            Net.Download(url, 0, 0, zip + ".part", "", progress);
            if (!Client.Sha256(zip + ".part").Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(zip + ".part");
                throw new IOException("the new launcher did not match its hash, so it is not installed");
            }

            string fresh = Exe + ".new";
            using (var z = ZipFile.OpenRead(zip + ".part"))
            {
                var e = z.Entries.FirstOrDefault(x => x.Name.Equals("ComfyLauncher.exe", StringComparison.OrdinalIgnoreCase));
                if (e == null) throw new IOException("the download has no ComfyLauncher.exe in it");
                e.ExtractToFile(fresh, true);
            }
            File.Delete(zip + ".part");

            string old = Exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(Exe, old);
            try { File.Move(fresh, Exe); }
            catch
            {
                // Put the running one back rather than leave no launcher.
                File.Move(old, Exe);
                throw;
            }
            app.RestartInto = Exe;
        }

        // The new exe, in this console, with the arguments this one had.
        public static void Restart(string exe, string[] args)
        {
            string line = string.Join(" ", args.Select(a => a.Contains(" ") ? "\"" + a + "\"" : a));
            Process.Start(new ProcessStartInfo(exe, line) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory });
        }
    }
}
