using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComfyLauncher
{
    // The WoW folder in Windows Defender's exclusions, from a button on the
    // Settings tab. Defender has removed client mods: it quarantines
    // vanillafixes-1.5.3.zip as Trojan:Win32/Vigorf.A (Operations.NotMatched).
    //
    // ADMIN ONLY TO ADD. Add-MpPreference needs an elevated PowerShell, so
    // Windows asks first. The list cannot be read either without admin:
    // Get-MpPreference says "Must be an administrator to view exclusions", the
    // registry key refuses, and MpCmdRun -CheckExclusion gives 0x80070005
    // (measured 2026-10-27).
    //
    // THE DEFENDER LOG CAN BE READ, and its event 5007 records each change:
    // New Value is "HKLM\...\Exclusions\Paths\<path> = 0x0" when a path is
    // added, Old Value is that text when it is removed. The latest event for
    // the client or a folder above it decides. No event (a cleared log, or one
    // too old to be kept) leaves the button as it is, and a second press adds
    // the path again, which changes nothing. An exact XPath match took 400 ms
    // from PowerShell, so it runs beside the first frame, not before it.
    static class Defender
    {
        public const string Id = "defender";
        const string Log = "Microsoft-Windows-Windows Defender/Operational";
        const string Key = @"HKLM\SOFTWARE\Microsoft\Windows Defender\Exclusions\Paths\";

        // What the log says, for the row and the client report.
        public sealed class Found
        {
            public string Path;       // the folder the event names: the client or one above it
            public DateTime When;
        }

        static Task<Found> check;
        static bool applied;
        public static bool Excluded;

        public static void StartCheck(string root) => check = Task.Run(() => Logged(root));

        // True once, when the check has finished: the main loop redraws then.
        // The rows belong to the main thread, so the result is put on the row here.
        public static bool Apply(App app)
        {
            if (check == null || applied || !check.IsCompleted) return false;
            applied = true;
            if (check.Status == TaskStatus.RanToCompletion && check.Result != null) Mark(app);
            return true;
        }

        // The client report's line. It waits for a check still running, and
        // runs one when none was started.
        public static string Report(string root, Func<string, string> show)
        {
            try
            {
                // It runs on the main thread, so a slow log costs 2 s at most.
                var t = check ?? Task.Run(() => Logged(root));
                if (!t.Wait(2000)) return "not known yet: the Defender log is still being read, check again";
                var f = t.Result;
                if (f == null) return "not in the Defender log: not excluded, or the log no longer has the event";
                return "excluded since " + f.When.ToString("yyyy-MM-dd HH:mm") + ", on " + show(f.Path);
            }
            catch (Exception e) { return "not known: " + (e.InnerException ?? e).Message; }
        }

        static void Mark(App app)
        {
            Excluded = true;
            var row = app.Rows.FirstOrDefault(r => r.Id == Id);
            if (row != null) Note(row);
        }

        public static void Note(Row row)
        {
            row.Note = "in the exclusions, by the Defender log";
            row.Advice = "The Defender log shows the WoW folder, or a folder above it, in the exclusions. Enter adds it again.";
        }

        // The client and each folder above it, as the log writes them: no
        // trailing \ except on a drive.
        static Found Logged(string root)
        {
            var paths = new List<string>();
            for (var d = new DirectoryInfo(root); d != null; d = d.Parent)
                paths.Add(d.Parent == null ? d.FullName : d.FullName.TrimEnd('\\'));
            var values = paths.Where(p => !p.Contains("'")).Select(p => Key + p + " = 0x0").ToList();
            string match = string.Join(" or ", values.Select(v => "Data='" + v + "'"));
            var query = new EventLogQuery(Log, PathType.LogName, "*[System[EventID=5007]] and *[EventData[" + match + "]]") { ReverseDirection = true };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = new EventLogReader(query))
                for (var e = reader.ReadEvent(); e != null; e = reader.ReadEvent())
                    using (e)
                    {
                        if (e.Properties.Count < 4) continue;
                        string old = e.Properties[2].Value as string ?? "", now = e.Properties[3].Value as string ?? "";
                        // Newest first, so the first event for a path is its state.
                        var added = values.FirstOrDefault(v => v.Equals(now, StringComparison.OrdinalIgnoreCase));
                        if (added != null)
                        {
                            if (seen.Add(added))
                                return new Found { Path = added.Substring(Key.Length, added.Length - Key.Length - " = 0x0".Length), When = e.TimeCreated ?? DateTime.MinValue };
                            continue;
                        }
                        var removed = values.FirstOrDefault(v => v.Equals(old, StringComparison.OrdinalIgnoreCase));
                        if (removed != null) seen.Add(removed);
                    }
            return null;
        }

        // ON A TASK, because Process.Start does not return until the player
        // answers the Windows prompt, and the screen must not freeze that long.
        // The main loop takes the result in ApplyExclude.
        static Task<(string Text, bool Added)> adding;

        public static string Exclude(App app)
        {
            if (adding != null) return "waiting for the Windows prompt";
            string root = app.Client.Root, home = app.Home;
            adding = Task.Run(() => Add(root, home));
            return "Windows asks for admin rights";
        }

        // True once, when the add has finished.
        public static bool ApplyExclude(App app)
        {
            if (adding == null || !adding.IsCompleted) return false;
            var (text, added) = adding.Status == TaskStatus.RanToCompletion ? adding.Result : ("not added: " + adding.Exception?.InnerException?.Message, false);
            adding = null;
            app.Flash = text;
            if (added) Mark(app);
            return true;
        }

        // The client, and the launcher's folder when it is somewhere else,
        // since its downloads land there first.
        static (string, bool) Add(string root, string home)
        {
            var paths = new[] { root, home }
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => Path.GetFullPath(p).TrimEnd('\\'))
                .Where(p => p == root.TrimEnd('\\') || !p.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string list = string.Join(",", paths.Select(p => "'" + p.Replace("'", "''") + "'"));
            string script = "try { Add-MpPreference -ExclusionPath " + list + " -ErrorAction Stop; exit 0 } catch { exit 1 }";
            var start = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                using (var p = Process.Start(start))
                {
                    if (p == null) return ("not added: PowerShell did not start", false);
                    if (!p.WaitForExit(120000)) return ("not added: Defender did not answer in 2 minutes", false);
                    if (p.ExitCode != 0) return ("Defender refused: another antivirus may be in charge", false);
                    return ("added the WoW folder to Defender's exclusions", true);
                }
            }
            // 1223: No on the Windows prompt.
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return ("not added: Windows asked and the answer was no", false); }
            catch (Exception e) { return ("not added: " + e.Message, false); }
        }
    }
}
