using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ComfyLauncher
{
    // A CLIENT THAT WoWSilicon PATCHED, on a Mac (github.com/WoWSilicon/WoWSilicon,
    // read 2026-10-25). The launcher runs in WoWSilicon's launcher slot, inside
    // its Wine and bottle. Three things differ from Windows and Linux:
    //
    // 1. The game starts as WoW.exe, never VanillaFixes. WoWSilicon patches
    //    DivxDecoder.dll (the original stays as DivxDecoder.dll.bak) so the
    //    game loads dlls.txt itself; VanillaFixes would load every mod twice,
    //    and its own loading fails under that Wine.
    // 2. WoWSilicon moves each mod DLL named in dlls.txt into mods\ and writes
    //    its line as mods/<name> (PatchService.normalizeRootDllMods, run when
    //    the player applies its game patch). Client.Read maps those back, so
    //    a mod there reads as installed and is updated where it is. A new mod
    //    goes beside WoW.exe with a bare line, which WoWSilicon's loader
    //    also reads.
    // 3. The d3d9.dll is WoWSilicon's (D9VK or MTLd3D), so there is no DXVK row.
    //
    // Its own lines, mods/winerosetta.dll and mods/libSiliconPatch.dll, are not
    // rows: unticking one breaks the game.
    static class WoWSilicon
    {
        static readonly string[] OwnDlls = { "winerosetta.dll", "libsiliconpatch.dll" };

        // Set by --wowsilicon, to test the Mods tab on Windows.
        public static bool Force;

        public static bool Is(Client c) =>
            (Terminal.UnderWine || Force) && (File.Exists(c.PathOf("DivxDecoder.dll.bak"))
                                   || c.DllLines.Any(l => ModsName(l)?.Equals("winerosetta.dll", StringComparison.OrdinalIgnoreCase) == true));

        // "mods/x.dll" or "mods\x.dll" gives "x.dll"; anything else null.
        public static string ModsName(string line)
        {
            var m = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^mods[/\\]([^/\\]+\.dll)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        public static bool Own(string name) => OwnDlls.Contains(name, StringComparer.OrdinalIgnoreCase);

        // ---- STARTING THE GAME, as WoWSilicon's Play button does
        // (LaunchService.makeShellCommand):
        //
        //   cd <game> && X87_SIDECAR_PATH=<helper> <env> wine WoW.exe
        //
        // The launcher already has the rest of that environment, because
        // WoWSilicon started it with it. The x87 helper is the one setting it
        // leaves out of the launcher slot, and it must be in the game's UNIX
        // environment: Wine's loader reads it with getenv before Windows code
        // runs. A Windows process cannot set that for a child (Wine's
        // spawn_process passes only WINEDEBUG on), so the game starts through
        // /bin/sh with __wine_unix_spawnvp, the call winebrowser uses.

        [DllImport("ntdll.dll", EntryPoint = "__wine_unix_spawnvp")]
        static extern int UnixSpawn(IntPtr[] argv, int wait);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "wine_get_unix_file_name")]
        static extern IntPtr UnixFileName(string dos);

        // What the game starts as, and how, for the Start the game line.
        public sealed class Launch
        {
            public string Exe;          // the Windows path
            public string X87Key = "";  // X87_SIDECAR_PATH or ROSETTA_X87_PATH, or none
            public string X87Path = ""; // a Unix path
            public string Text => "start " + Path.GetFileName(Exe) + " as WoWSilicon does"
                                  + (X87Key != "" ? ", with " + Path.GetFileName(X87Path) : "");
        }

        // The player's WoWSilicon profile for this client: the one whose
        // game_path is this folder, else the current one. Without it the game
        // starts as WoW.exe with no x87 helper, which runs, slower.
        public static Launch Plan(Client c)
        {
            var launch = new Launch { Exe = c.PathOf("WoW.exe") };
            try
            {
                var settings = Profile(c)?.Obj("settings");
                if (settings == null) return launch;

                if (settings.Bool("enableVanillaTweaks") && File.Exists(c.PathOf("WoW_tweaked.exe")))
                    launch.Exe = c.PathOf("WoW_tweaked.exe");

                string backend = settings.Str("x87Backend");
                if (backend == "" && settings.Bool("enableRosettaX87")) backend = "rosettax87";
                string key = backend == "x87sidecar" ? "X87_SIDECAR_PATH" : backend == "rosettax87" ? "ROSETTA_X87_PATH" : "";
                string helper = key == "" ? null : Helper(backend);
                if (helper != null) { launch.X87Key = key; launch.X87Path = helper; }
            }
            catch { }
            return launch;
        }

        // The profile in ~/Library/Application Support/WoWSilicon/versions.json
        // whose game_path is this client, else the current one, or null.
        static Dictionary<string, object> Profile(Client c)
        {
            try
            {
                string home = Environment.GetEnvironmentVariable("WINE_HOST_HOME") ?? Environment.GetEnvironmentVariable("HOME");
                if (string.IsNullOrEmpty(home) || !home.StartsWith("/")) return null;
                string file = "Z:" + home.Replace('/', '\\') + @"\Library\Application Support\WoWSilicon\versions.json";
                if (!File.Exists(file)) return null;
                var store = Json.Parse(File.ReadAllText(file));
                var versions = store?.Obj("versions");
                if (versions == null) return null;
                string root = Unix(c.Root)?.TrimEnd('/') ?? "";
                foreach (var kv in versions)
                    if (kv.Value is Dictionary<string, object> v && v.Str("game_path").TrimEnd('/').Equals(root, StringComparison.OrdinalIgnoreCase)) return v;
                return versions.Obj(store.Str("currentVersionID"));
            }
            catch { return null; }
        }

        // The helper inside WoWSilicon.app, found from the Wine running this
        // launcher: Resources/Patching/<name>/<name>, or the same inside one of
        // the app's resource bundles.
        static string Helper(string name)
        {
            string loader = WineLoader();
            int at = loader?.IndexOf(".app/Contents/", StringComparison.Ordinal) ?? -1;
            if (at < 0) return null;
            string resources = loader.Substring(0, at + ".app/Contents/".Length) + "Resources";
            string dos = "Z:" + resources.Replace('/', '\\');
            var places = new List<string> { Path.Combine(dos, "Patching", name, name) };
            try
            {
                foreach (var bundle in Directory.GetDirectories(dos, "*.bundle"))
                {
                    places.Add(Path.Combine(bundle, "Patching", name, name));
                    places.Add(Path.Combine(bundle, "Contents", "Resources", "Patching", name, name));
                }
            }
            catch { }
            string found = places.FirstOrDefault(File.Exists);
            return found == null ? null : found.Substring(2).Replace('\\', '/');
        }

        // Wine's own binary, as a Unix path. Wine puts it in the Windows
        // environment as an NT path, \??\Z:\...\bin\wine.
        static string WineLoader()
        {
            string nt = Environment.GetEnvironmentVariable("WINELOADER");
            if (string.IsNullOrEmpty(nt)) return null;
            if (nt.StartsWith(@"\??\")) nt = nt.Substring(4);
            return Unix(nt);
        }

        public static string Unix(string dos)
        {
            try
            {
                IntPtr p = UnixFileName(dos);
                if (p == IntPtr.Zero) return null;
                var bytes = new List<byte>();
                for (int i = 0; ; i++) { byte b = Marshal.ReadByte(p, i); if (b == 0) break; bytes.Add(b); }
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            catch { return null; }
        }

        static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

        // A line for the Mac's /bin/sh, waited for or not. Throws when the
        // shell does not start.
        static void Shell(string line, bool wait)
        {
            var args = new[] { "/bin/sh", "-c", line };
            var argv = new IntPtr[args.Length + 1];
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    var bytes = Encoding.UTF8.GetBytes(args[i] + "\0");
                    argv[i] = Marshal.AllocHGlobal(bytes.Length);
                    Marshal.Copy(bytes, 0, argv[i], bytes.Length);
                }
                int status = UnixSpawn(argv, wait ? 1 : 0);
                if (status != 0) throw new IOException("the Mac shell did not start (status 0x" + status.ToString("x8") + ")");
            }
            finally
            {
                foreach (var p in argv) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            }
        }

        // DYLD_ SETTINGS GO IN THE LINE. macOS takes them out of the
        // environment of /bin/sh and every other system binary, so a shell
        // passes them on only when it sets them itself. Wine copied them into
        // the Windows environment when this launcher started.
        static string DyldAssignments() =>
            string.Concat(new[] { "DYLD_LIBRARY_PATH", "DYLD_FALLBACK_LIBRARY_PATH" }
                .Select(k => (k, v: Environment.GetEnvironmentVariable(k)))
                .Where(x => !string.IsNullOrEmpty(x.v))
                .Select(x => x.k + "=" + Quote(x.v) + " "));

        // The game, started through the Mac's shell. Throws when it cannot.
        public static void Start(Client c, Launch launch)
        {
            string wine = WineLoader() ?? throw new IOException("Wine's own path is not known (no WINELOADER), so the game cannot start as WoWSilicon does");
            string dir = Unix(c.Root) ?? throw new IOException("the client's Mac path is not known");
            string exe = Unix(launch.Exe) ?? throw new IOException("the game's Mac path is not known");
            Shell("cd " + Quote(dir) + " && "
                  + DyldAssignments()
                  + (launch.X87Key != "" ? launch.X87Key + "=" + Quote(launch.X87Path) + " " : "")
                  + "exec " + Quote(wine) + " " + Quote(exe), wait: false);
        }

        // ---- THE MAC APP: ComfyLauncher without opening WoWSilicon first.
        //
        // A script in an .app that starts this exe with WoWSilicon's Wine and
        // the settings WoWSilicon started it with, so nothing of WoWSilicon's
        // logic is copied: after a WoWSilicon update the player presses the
        // button again. Made on the Mac, so it carries no quarantine flag and
        // Gatekeeper does not ask.
        //
        // THE SETTINGS COME FROM THE REAL MAC ENVIRONMENT, read with env through
        // the shell: Wine leaves VK_ variables out of the Windows one
        // (is_ignored_env_var, dlls/ntdll/unix/env.c), and WoWSilicon sets
        // VK_DRIVER_FILES for KosmicKrisp. Only what WoWSilicon adds is kept;
        // the rest is the login session's, which the app gets from Finder,
        // and Wine's own (WINELOADERNOEXEC and the like) would change how a
        // fresh wine starts.

        public const string MacAppId = "mac-app";
        const string BundleId = "com.comfycraft.launcher";
        // Set by the app's script, so the launcher it starts does not offer
        // to make the app again.
        public const string FromAppVariable = "COMFYLAUNCHER_MAC_APP";

        // The two places MakeApp writes, as Windows paths.
        static IEnumerable<string> AppPlaces()
        {
            yield return @"Z:\Applications\ComfyLauncher.app";
            string home = Environment.GetEnvironmentVariable("WINE_HOST_HOME");
            if (!string.IsNullOrEmpty(home)) yield return "Z:" + home.Replace('/', '\\') + @"\Applications\ComfyLauncher.app";
        }

        public static bool AppExists() =>
            AppPlaces().Any(a => { try { string p = Path.Combine(a, @"Contents\Info.plist"); return File.Exists(p) && File.ReadAllText(p).Contains(BundleId); } catch { return false; } });

        static readonly string[] KeepNames = { "WINEPREFIX", "WINEDLLOVERRIDES", "WINE_LARGE_ADDRESS_AWARE", "WINE_D3D_CONFIG", "WINEESYNC", "WINEMSYNC" };
        static readonly string[] KeepPrefixes = { "VK_", "MVK_", "MTL_", "DXVK_", "WOWSILICON_" };

        // Makes the app and returns where it went, as a Mac path.
        public static string MakeApp(Client c)
        {
            string wine = WineLoader() ?? throw new IOException("Wine's own path is not known (no WINELOADER)");
            string dir = Unix(c.Root) ?? throw new IOException("the client's Mac path is not known");
            string exe = Unix(SelfUpdate.Exe) ?? throw new IOException("the launcher's Mac path is not known");
            string home = Environment.GetEnvironmentVariable("WINE_HOST_HOME") ?? throw new IOException("the Mac home folder is not known");

            var env = MacEnvironment(c);
            if (!env.ContainsKey("WINEPREFIX")) throw new IOException("the launcher was not started by WoWSilicon (no WINEPREFIX), so there are no settings to keep");
            foreach (var k in new[] { "DYLD_LIBRARY_PATH", "DYLD_FALLBACK_LIBRARY_PATH" })
            {
                string v = Environment.GetEnvironmentVariable(k);
                if (!string.IsNullOrEmpty(v)) env[k] = v;
            }

            // /Applications when it can be written, which it can for an admin
            // account, else the home folder's Applications.
            string app = null;
            foreach (var dos in AppPlaces())
            {
                try
                {
                    string plist = Path.Combine(dos, @"Contents\Info.plist");
                    if (File.Exists(plist) && !File.ReadAllText(plist).Contains(BundleId)) continue;
                    Directory.CreateDirectory(Path.Combine(dos, @"Contents\MacOS"));
                    Directory.CreateDirectory(Path.Combine(dos, @"Contents\Resources"));
                    app = dos;
                    break;
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            if (app == null) throw new IOException("ComfyLauncher.app could not be written in /Applications or " + home + "/Applications");

            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(Path.Combine(app, @"Contents\Info.plist"), InfoPlist, utf8);

            var script = new StringBuilder();
            script.Append("#!/bin/bash\n");
            script.Append("# ComfyLauncher through WoWSilicon's Wine. Made by ComfyLauncher " + SelfUpdate.Version + " on " + DateTime.Now.ToString("yyyy-MM-dd") + ",\n");
            script.Append("# with the settings WoWSilicon started it with. After a WoWSilicon update,\n");
            script.Append("# start the launcher from WoWSilicon once and press Make a Mac app again.\n");
            script.Append("WINE=" + Quote(wine) + "\n");
            script.Append("EXE=" + Quote(exe) + "\n");
            script.Append("fail() { osascript -e \"display dialog \\\"$1\\\" with title \\\"ComfyLauncher\\\" buttons {\\\"OK\\\"} default button 1 with icon caution\" >/dev/null; exit 1; }\n");
            script.Append("[ -x \"$WINE\" ] || fail \"The Wine from WoWSilicon is not where it was. Start ComfyLauncher from WoWSilicon once, then press Make a Mac app in its Settings tab.\"\n");
            script.Append("[ -f \"$EXE\" ] || fail \"ComfyLauncher.exe is not where it was. Start ComfyLauncher from WoWSilicon once, then press Make a Mac app in its Settings tab.\"\n");
            foreach (var kv in env.OrderBy(k => k.Key, StringComparer.Ordinal))
                script.Append("export " + kv.Key + "=" + Quote(kv.Value) + "\n");
            script.Append("export " + FromAppVariable + "=1\n");
            script.Append("cd " + Quote(dir) + " || exit 1\n");
            script.Append("exec \"$WINE\" \"$EXE\"\n");
            File.WriteAllText(Path.Combine(app, @"Contents\MacOS\ComfyLauncher"), script.ToString(), utf8);

            using (var ico = typeof(WoWSilicon).Assembly.GetManifestResourceStream("ComfyLauncher.ico"))
            using (var to = File.Create(Path.Combine(app, @"Contents\Resources\ComfyLauncher.ico")))
                ico.CopyTo(to);

            // The script's executable flag, and the icon in the form macOS
            // reads. A file Wine writes has no executable flag.
            string macApp = app.Substring(2).Replace('\\', '/');
            string res = macApp + "/Contents/Resources";
            Shell("chmod 755 " + Quote(macApp + "/Contents/MacOS/ComfyLauncher")
                  + " && sips -s format png " + Quote(res + "/ComfyLauncher.ico") + " --out " + Quote(res + "/icon.png") + " >/dev/null 2>&1"
                  + " && sips -z 512 512 " + Quote(res + "/icon.png") + " >/dev/null 2>&1"
                  + " && sips -s format icns " + Quote(res + "/icon.png") + " --out " + Quote(res + "/ComfyLauncher.icns") + " >/dev/null 2>&1"
                  + "; rm -f " + Quote(res + "/icon.png") + " " + Quote(res + "/ComfyLauncher.ico")
                  + "; touch " + Quote(macApp), wait: true);
            return macApp;
        }

        // What WoWSilicon added to this launcher's Mac environment: its own
        // names, and the names in the profile's own environment variables.
        static Dictionary<string, string> MacEnvironment(Client c)
        {
            string tempDos = Path.Combine(Path.GetTempPath(), "comfylauncher-env.txt");
            string temp = Unix(tempDos) ?? throw new IOException("the temporary folder's Mac path is not known");
            if (File.Exists(tempDos)) File.Delete(tempDos);
            Shell("/usr/bin/env -0 > " + Quote(temp), wait: true);
            if (!File.Exists(tempDos)) throw new IOException("the Mac environment could not be read");
            string all = File.ReadAllText(tempDos, Encoding.UTF8);
            File.Delete(tempDos);

            var custom = new HashSet<string>(ProfileVariableNames(c), StringComparer.Ordinal);
            var keep = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in all.Split('\0'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                string k = pair.Substring(0, eq), v = pair.Substring(eq + 1);
                if (KeepNames.Contains(k) || KeepPrefixes.Any(k.StartsWith) || custom.Contains(k)) keep[k] = v;
            }
            return keep;
        }

        // The names in the WoWSilicon profile's environment variables box,
        // which it passes to the launcher too.
        static IEnumerable<string> ProfileVariableNames(Client c)
        {
            var settings = Profile(c)?.Obj("settings");
            string text = settings?.Str("environmentVariables") ?? "";
            return System.Text.RegularExpressions.Regex.Matches(text, @"([A-Za-z_][A-Za-z0-9_]*)=")
                .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value);
        }

        const string InfoPlist =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n<dict>\n" +
            "\t<key>CFBundleName</key>\n\t<string>ComfyLauncher</string>\n" +
            "\t<key>CFBundleDisplayName</key>\n\t<string>ComfyLauncher</string>\n" +
            "\t<key>CFBundleIdentifier</key>\n\t<string>" + BundleId + "</string>\n" +
            "\t<key>CFBundleExecutable</key>\n\t<string>ComfyLauncher</string>\n" +
            "\t<key>CFBundleIconFile</key>\n\t<string>ComfyLauncher</string>\n" +
            "\t<key>CFBundlePackageType</key>\n\t<string>APPL</string>\n" +
            "\t<key>LSMinimumSystemVersion</key>\n\t<string>14.0</string>\n" +
            "</dict>\n</plist>\n";
    }
}
