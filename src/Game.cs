using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ComfyLauncher
{
    // The game's processes: is this client running, and starting it. Ported
    // from ComfyLauncher.ps1's Test-ClientRunning and Start-Game.
    static class Game
    {
        static readonly string[] Names = { "WoW", "WoW_untweaked", "SuperWoWlauncher", "VanillaFixes" };

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        // The exe path of a process, or null. Process.MainModule cannot read
        // every process; this asks with the least access that answers.
        static string ImagePath(int pid)
        {
            foreach (int access in new[] { 0x1000, 0x0400 })
            {
                IntPtr h = OpenProcess(access, false, pid);
                if (h == IntPtr.Zero) continue;
                try
                {
                    var sb = new StringBuilder(1024);
                    int n = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref n)) return sb.ToString();
                }
                finally { CloseHandle(h); }
            }
            return null;
        }

        public static System.Collections.Generic.IEnumerable<string> Probe()
        {
            foreach (var name in Names)
                foreach (var p in Process.GetProcessesByName(name))
                    yield return p.Id + " " + name + ": " + (ImagePath(p.Id) ?? "(path unreadable)") + ", error " + Marshal.GetLastWin32Error();
        }

        // THIS CLIENT, not any client: two clients on one machine are normal
        // here. A process whose path cannot be read counts as running, since
        // it may be this one.
        public static bool Running(Client c)
        {
            foreach (var name in Names)
                foreach (var p in Process.GetProcessesByName(name))
                {
                    string exe = ImagePath(p.Id);
                    if (exe == null) return true;
                    if (Path.GetDirectoryName(exe).TrimEnd('\\').Equals(c.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
                }
            return false;
        }

        // A CLIENT THAT WAS CLOSED AND DID NOT EXIT: measured 2026-09-24, the
        // window went and WoW.exe stayed, holding the MPQs, for minutes. Stuck
        // is this client's path, no window, older than 30 seconds, and it is
        // ALL OR NOTHING: any process of this client that is not stuck, or
        // cannot be read, means none is ended, since that may be a live game.
        public static List<Process> Stuck(Client c)
        {
            var stuck = new List<Process>();
            foreach (var name in Names)
                foreach (var p in Process.GetProcessesByName(name))
                {
                    string exe = ImagePath(p.Id);
                    if (exe == null) return new List<Process>();
                    if (!Path.GetDirectoryName(exe).TrimEnd('\\').Equals(c.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.MainWindowHandle != IntPtr.Zero) return new List<Process>();
                    DateTime born;
                    try { born = p.StartTime; } catch { return new List<Process>(); }
                    if ((DateTime.Now - born).TotalSeconds < 30) return new List<Process>();
                    stuck.Add(p);
                }
            return stuck;
        }

        public static void EndStuck(List<Process> stuck)
        {
            foreach (var p in stuck) try { p.Kill(); } catch { }
            foreach (var p in stuck) try { p.WaitForExit(5000); } catch { }
        }

        // VanillaFixes when the client has it, since it loads the mods; then
        // the console hides until the game closes. Steam counts the game as
        // running while this process lives, which is what keeps the overlay.
        public static void Start(App app)
        {
            if (Program.Scripted) return;
            Music.Set(false);   // the game has its own
            var c = app.Client;
            string exe = new[] { "VanillaFixes.exe", "WoW.exe" }.Select(c.PathOf).FirstOrDefault(File.Exists);
            if (exe == null) throw new IOException("nothing to start: no VanillaFixes.exe and no WoW.exe");

            var before = Process.GetProcessesByName("WoW").Select(p => p.Id).ToList();
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = c.Root, UseShellExecute = false });

            // The new WoW.exe, within two minutes; VanillaFixes itself exits
            // once it has started the game.
            Process game = null;
            var giveUp = DateTime.Now.AddSeconds(120);
            while (game == null && DateTime.Now < giveUp)
            {
                game = Process.GetProcessesByName("WoW").FirstOrDefault(p => !before.Contains(p.Id));
                if (game == null) Thread.Sleep(400);
            }
            if (game == null) return;

            ShowWindow(GetConsoleWindow(), 0);   // hide
            try { game.WaitForExit(); }
            finally { ShowWindow(GetConsoleWindow(), 7); }   // shown, minimised, not active
        }
    }
}
