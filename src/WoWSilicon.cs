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
                string home = Environment.GetEnvironmentVariable("WINE_HOST_HOME") ?? Environment.GetEnvironmentVariable("HOME");
                if (string.IsNullOrEmpty(home) || !home.StartsWith("/")) return launch;
                string file = "Z:" + home.Replace('/', '\\') + @"\Library\Application Support\WoWSilicon\versions.json";
                if (!File.Exists(file)) return launch;
                var store = Json.Parse(File.ReadAllText(file));
                var versions = store?.Obj("versions");
                if (versions == null) return launch;
                string root = Unix(c.Root)?.TrimEnd('/') ?? "";
                Dictionary<string, object> profile = null;
                foreach (var kv in versions)
                    if (kv.Value is Dictionary<string, object> v && v.Str("game_path").TrimEnd('/').Equals(root, StringComparison.OrdinalIgnoreCase)) { profile = v; break; }
                if (profile == null) profile = versions.Obj(store.Str("currentVersionID"));
                var settings = profile?.Obj("settings");
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

        // The game, started through the Mac's shell. Throws when it cannot.
        public static void Start(Client c, Launch launch)
        {
            string wine = WineLoader() ?? throw new IOException("Wine's own path is not known (no WINELOADER), so the game cannot start as WoWSilicon does");
            string dir = Unix(c.Root) ?? throw new IOException("the client's Mac path is not known");
            string exe = Unix(launch.Exe) ?? throw new IOException("the game's Mac path is not known");
            string line = "cd " + Quote(dir) + " && "
                        + (launch.X87Key != "" ? launch.X87Key + "=" + Quote(launch.X87Path) + " " : "")
                        + "exec " + Quote(wine) + " " + Quote(exe);

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
                int status = UnixSpawn(argv, 0);
                if (status != 0) throw new IOException("the Mac shell did not start (status 0x" + status.ToString("x8") + ")");
            }
            finally
            {
                foreach (var p in argv) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            }
        }
    }
}
