using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ComfyLauncher
{
    // HOW LONG THE START TOOK, step by step, for a player whose launcher is
    // slow to open. Each step is timed from the moment Windows made the
    // process, so the first line includes .NET loading the exe. The steps
    // go to Logs\ComfyLauncher.log in the client when the first frame is
    // drawn: the client is not known before then. A start under --keys
    // writes nothing.
    //
    // The same steps show on the console while the window is still blank,
    // so a player can see which one it waits on.
    static class StartLog
    {
        const long MaxBytes = 64 * 1024;
        static DateTime start;
        static readonly List<string> lines = new List<string>();
        static bool show, done;

        public static void Begin(bool console)
        {
            try { start = Process.GetCurrentProcess().StartTime; } catch { start = DateTime.Now; }
            show = console;
            Step("reached Main");
        }

        // A step that has finished.
        public static void Step(string what)
        {
            if (done) return;
            lines.Add($"{(DateTime.Now - start).TotalSeconds,7:0.00} s  {what}");
        }

        // What the blank window says it is doing now.
        public static void Say(string text)
        {
            if (!show) return;
            try { Console.WriteLine("  " + text); } catch { }
        }

        // Appended, so a slow start is still there after a quick one. Past
        // 64 KB the older half goes.
        public static void Finish(string clientRoot)
        {
            if (done) return;
            Step("drew the first frame");
            done = true;
            if (!show || clientRoot == null) return;
            try
            {
                string dir = Path.Combine(clientRoot, "Logs"), file = Path.Combine(dir, "ComfyLauncher.log");
                Directory.CreateDirectory(dir);
                var text = new StringBuilder();
                text.AppendLine(start.ToString("yyyy-MM-dd HH:mm:ss") + "  ComfyLauncher " + SelfUpdate.Version);
                foreach (var l in lines) text.AppendLine(l);
                text.AppendLine();
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                {
                    string old = File.ReadAllText(file);
                    int cut = old.IndexOf("\r\n\r\n", old.Length / 2, StringComparison.Ordinal);
                    File.WriteAllText(file, cut < 0 ? "" : old.Substring(cut + 4));
                }
                File.AppendAllText(file, text.ToString());
            }
            catch { }
        }
    }
}
