using System;
using System.Collections.Generic;
using System.Linq;

namespace ComfyLauncher
{
    // What a row is. The mark in front of it comes from this and nothing else.
    enum RowKind
    {
        Button,    // no mark: Enter does it (Start the game, Update available)
        Check,     // [x] or [ ]: a package you choose
        Required,  // [x] always: a package ComfyCraft needs, never unticked
        Part,      // its package's tick: an addon that belongs to a package on the Mods tab
        Radio,     // (o) or ( ), one of a group
        Add,       //  +  opens a prompt
        Link,      // no mark, as a button has none: Enter opens Url in the browser
        Label,     // a section heading, or a blank line; the cursor skips it
        Setting,   // [x] or [ ]: a launcher setting, which Space changes at once
        Config,    // < choice >: a line in the client's Config.wtf, written with the other work
    }

    // What is on the disk now, which is a different question from what the
    // tick says you want. Aside is a file of the player's own that the
    // launcher renamed to .old beside where it was, instead of deleting it.
    // Off is on the disk and switched off: a patch renamed .mpq.off, or a
    // mod whose line is not in dlls.txt.
    enum Have { None, Current, Behind, Missing, Aside, Off }

    enum PartType { Addon, Patch, Dll, File }

    // ONE FILE A PACKAGE WRITES, and where it comes from. The dry run lists
    // these; the real work in stage 3 will fetch them.
    sealed class Part
    {
        public PartType Type;
        public string Name = "";   // addon folder, dll file, or a path relative to the client
        public char Letter;        // patches
        public string Url = "";
        public string Sha256 = "";
        public long Size;
        public long ZipOffset = -1; // a stored entry inside a zip at Url, fetched by range
        public string Extract = ""; // the zip entry or subtree it comes from, for mods
        public string Etag = "";    // the host's ETag when the size was pinned
        public string Crc32 = "";   // the zip's own check for a stored entry
        public bool OffWithMod;     // an addon that goes aside when its mod is switched off
        public List<long> Was = new List<long>();  // patches: sizes an earlier pin shipped, which an update replaces

        public string Show =>
            Type == PartType.Addon ? "the " + Name + " addon"
            : Type == PartType.Patch ? "patch-" + Letter + ".mpq"
            : Name;
    }

    // ONE BUILD OF A FILE THAT SITS BESIDE WoW.exe, told apart by its hash:
    // the DXVK row's choices. No Url means it cannot be fetched, only kept.
    sealed class Build
    {
        public string Name = "";
        public string Note = "";
        public string Sha256 = "";          // of the file itself
        public string Url = "";             // a .tar.gz release
        public long Size;                   // of the download
        public string ArchiveSha256 = "";
        public string Extract = "";         // the entry in the archive
        public string Aside;                // where it is set aside, or null
        public string[] Conf = new string[0];   // lines it needs in dxvk.conf (async)
    }

    // A ROW IS A PACKAGE: one name for whatever parts it takes, a mod, a
    // patch, an addon, an ini file, in any mix. Ticking it takes all of them.
    sealed class Row
    {
        public string Id;
        public RowKind Kind;
        public string Tab;
        public string Label;
        public string Note = "";
        public bool On;
        public Have Have;
        public bool Ours;          // ComfyCraft ships it, so Update available covers it
        public bool Unknown;       // on the disk, but no package claims it
        public bool Recorded;      // the state file says this launcher put it there, so an update deletes the old copy
        public int SizeKb;
        public string Parts = "";  // for the detail line
        public string PartOf;      // Part rows: the package it belongs to
        public Row Owner;          // Part rows: that package's row, whose tick it shows
        public string Url;         // Link rows
        public string Advice = ""; // a last sentence for the detail line

        // A PART OF THE ROWS THAT NEED IT, drawn on no tab: comfylogin.dll
        // under Saved accounts (2026-10-14). Ticking one of those rows ticks
        // it, and it goes off when no ticked row needs it.
        public bool Hidden;

        // A SWITCH, NOT A DOWNLOAD: mods and patches. Unticking one turns it
        // off and keeps the file, so ticking it again costs nothing.
        public bool Toggle;

        // The real files, when the row came from a client rather than the
        // sample. PartList for a plain row, ChoicePartList per choice.
        public List<Part> PartList = new List<Part>();
        public List<Part>[] ChoicePartList;
        public List<Part> PartsNowList => ChoicePartList != null ? ChoicePartList[Choice] : PartList;

        // Realm rows.
        public string Address = "";
        public string Patchlist = "";

        // What comfylauncher.json records once this row is installed: the
        // item name or "mod:<file>", and the version, as the .ps1 records it.
        // Config rows: what each choice says beside it, and the SET lines it
        // writes. The saved display row writes nothing of its own: choosing
        // one moves the window and resolution rows.
        public string[] ChoiceNotes;
        public List<(string Key, string Value)>[] ChoiceSets;

        public string StateKey = "";
        public string Version = "";
        public string[] ChoiceVersion;
        public List<string> Supersedes = new List<string>();
        // Files in the client's folder a mod took the place of (2026-10-06: comfyatmos.dll replaced comfyfog.dll and
        // comfygrass.dll). After the mod is in place, each old DLL leaves dlls.txt and each file goes aside.
        public List<string> SupersedesFiles = new List<string>();

        // Operations of its own, for a row that is not a package: an addon
        // added from a link.
        public List<Op> Custom;

        // THE DXVK ROW: one build per choice, of BuildFile beside WoW.exe.
        // Switching sets the old file aside; it is never deleted.
        public List<Build> Builds;
        public string BuildFile = "";

        // THE PATCH LETTERS IT WRITES, every choice included. Two packs that
        // write the same Data\patch-X.mpq cannot both be on a client, so
        // ticking one is refused while the other is on. Project Reforged and Faithful
        // Upscale share nine letters.
        public readonly HashSet<char> Writes = new HashSet<char>();

        // DECLARED CONFLICTS, by package id, from the manifest: packages
        // that cannot be on together although they write different files,
        // such as two retextures of the same characters. One side declaring
        // it is enough; App.Clashes reads both.
        public readonly List<string> Conflicts = new List<string>();

        // Which HD pack it comes from, short, for naming it outside its own
        // section: "Reforged Characters and gear".
        public string Pack;
        public string FullName => Pack == null ? Label : Pack + " " + Label;

        // NEEDS. A child row is drawn indented under its Parent and needs it.
        // Needs holds the rest, which are drawn elsewhere.
        public Row Parent;
        public readonly List<Row> Needs = new List<Row>();

        // A PACKAGE WITH VARIANTS: Space steps through off and each choice,
        // starting from the one on the disk and going round past the end.
        // Choice is what the tick wants; HaveChoice is what is on the disk.
        public string[] Choices;
        public int[] ChoiceKb;
        public string[] ChoiceParts;
        public int Choice;
        public int HaveChoice;

        // Set while the row is being worked on.
        public double? Progress;
        public string Busy;
        public string Failed;      // why its last download failed, or null

        // The rows that unticking this one took with it. Ticking it again
        // brings them back, so one Space too many is undone by one more.
        public List<Row> CascadedOff = new List<Row>();

        // An action that deletes something that cannot come back without a
        // download. Setting aside is not one: Space brings the file back.
        public bool Destroys => Action == "remove";

        // An action that only moves or renames files on the disk.
        public bool Moves => Action == "remove" || Action == "set aside" || Action == "restore"
                          || Action == "enable" || Action == "disable" || Action == "set";

        public IEnumerable<Row> AllNeeds => Parent == null ? Needs : new[] { Parent }.Concat(Needs);

        public int Kb => Choices != null && ChoiceKb != null ? ChoiceKb[Choice] : SizeKb;

        public string PartsNow => Choices != null && ChoiceParts != null ? ChoiceParts[Choice] : Parts;

        // WHAT ENTER WILL DO TO THIS ROW, as one word. The status column and
        // the controls line both read it, so they cannot disagree.
        public string Action
        {
            get
            {
                switch (Kind)
                {
                    case RowKind.Required:
                        if (Have == Have.Behind) return "update";
                        if (Have == Have.None || Have == Have.Missing) return "install";
                        if (Have == Have.Off) return "enable";
                        return "";
                    case RowKind.Check:
                        if (On)
                        {
                            if (Have == Have.None)    return "install";
                            // ANOTHER VARIANT IS ANOTHER FILE. It comes before
                            // enable: the ultra patch-T is not the standard one
                            // switched back on, and ultra adds patch-U.
                            if (Choices != null && Choice != HaveChoice) return "change";
                            if (Have == Have.Missing) return "reinstall";
                            if (Have == Have.Behind)  return "update";
                            if (Have == Have.Aside)   return "restore";
                            if (Have == Have.Off)     return "enable";
                            return "";
                        }
                        // THE PLAYER'S OWN FILES ARE NEVER DELETED. A switch
                        // is turned off; anything else no package claims is
                        // moved aside, and Space brings it back. Ours can be
                        // deleted: it can be downloaded again.
                        if (Have == Have.Current || Have == Have.Behind)
                            return Toggle ? "disable" : Unknown ? "set aside" : "remove";
                        return "";
                    case RowKind.Radio:
                        return On && Have != Have.Current ? "switch" : "";
                    case RowKind.Config:
                        return ChoiceSets != null && Choice != HaveChoice ? "set" : "";
                    default:
                        return "";
                }
            }
        }
    }

    // A LIST TO CHOOSE FROM, drawn in place of the rows: Enter on a choice
    // row with more values than Space should have to step through.
    sealed class Picker
    {
        public string Title;
        public List<(string Label, string Note)> Options = new List<(string, string)>();
        public int At;
        public Action<int> Done;
    }

    sealed class Prompt
    {
        public string Title;
        public string Hint;
        public string Text = "";
        public Action<App, string> Done;

        // A SEARCH: the matches for Text are listed under it as it is typed,
        // Up and Down pick one, and Enter hands that one to Chose.
        public Func<string, List<CatalogEntry>> Search;
        public Action<App, CatalogEntry> Chose;
        public int At;
    }

    // ONE LOOK BEFORE A BIG OR DESTRUCTIVE STEP. Drawn in place of the rows;
    // Enter goes ahead, any other key goes back.
    sealed class Confirm
    {
        public string Title = "Before this starts";
        public List<(string Head, string Text, Ink Ink)> Lines = new List<(string, string, Ink)>();
        public bool CanGo = true;
        public Action Go;
    }

    sealed class App
    {
        public Confirm Confirm;
        public Picker Picker;

        // THE SETTING BEING ADJUSTED: Space starts it, Left and Right change
        // the value, Space or Enter keeps it, Esc puts back EditFrom.
        public Row Editing;
        public int EditFrom;

        // STAGE 1 OF THE REAL LAUNCHER READS AND NEVER WRITES. With a real
        // client loaded, Enter shows every file operation it would do, and
        // does none of them.
        public bool DryRun;
        public Client Client;

        // THE LAUNCHER ITSELF: behind when the manifest's launcher.release
        // is not this exe's. It updates first and alone, then restarts into
        // the new exe, which does the rest.
        public bool SelfBehind;
        public string SelfUrl = "";
        public string SelfSha = "";     // the zip's sha256, which Replace requires
        public string RestartInto;
        public string Home = "";        // the launcher's folder: comfylauncher.json, set-aside, downloads
        public Dictionary<string, object> State = new Dictionary<string, object>();
        public string StateFile => System.IO.Path.Combine(Home, "comfylauncher.json");
        public string Source = "";      // where the manifest came from, for the dry run
        public string CatalogUrl = "";  // dist\addons.json, when the manifest names one
        public List<CatalogEntry> Catalog;   // fetched when the search first opens
        public List<string> Notes = new List<string>();  // what loading had to say

        // Esc during work: finish the file in hand, then stop.
        public bool StopRequested;

        // A download failed. The player may start the game anyway, or try
        // again; neither is hidden behind the other.
        public bool UpdateFailed;

        // Test switches: --offline fails every download at 30 percent, and
        // --free sets the free disk space the confirm step reports.
        public bool Offline;
        public double? FreeKbOverride;

        public double FreeKb
        {
            get
            {
                if (FreeKbOverride.HasValue) return FreeKbOverride.Value;
                try { return new System.IO.DriveInfo(System.IO.Path.GetPathRoot(Environment.CurrentDirectory)).AvailableFreeSpace / 1024.0; }
                catch { return double.MaxValue; }
            }
        }

        public string Drive => System.IO.Path.GetPathRoot(Environment.CurrentDirectory).TrimEnd('\\');

        // WHAT AN UPDATE COVERS: a required row that is behind, and one of our
        // optional ones only while it is ticked. Unticked means unwanted, and
        // an update does not overrule that.
        //
        // MISSING IS BEHIND for a required row: a new player's client has
        // none of ComfyCraft, and it is not ready to play either.
        public bool NeedsUpdate(Row r) =>
            (r.Kind == RowKind.Required && r.Action != "") ||
            (r.Have == Have.Behind && r.Ours && r.On && r.Kind == RowKind.Check);

        // Nothing of ComfyCraft's required files is there yet: a first install.
        public bool FirstInstall => Rows.Where(r => r.Kind == RowKind.Required).All(r => r.Have == Have.None);
        public readonly List<string> Tabs = new List<string> { "Realm", "Mods", "Addons", "Settings" };

        public bool Setting(string id) => Rows.First(r => r.Id == id).On;

        // THE AUTO START COUNTDOWN, in milliseconds, or null when none runs.
        // It starts only when the launcher opens with nothing to update, and
        // any key stops it for good.
        public const int AutoStartMs = 8000;
        public int? CountdownMs;
        public Action<App> OnCountdownEnd;

        public void Elapse(int ms)
        {
            if (CountdownMs == null) return;
            CountdownMs -= ms;
            if (CountdownMs <= 0)
            {
                CountdownMs = null;
                OnCountdownEnd?.Invoke(this);
            }
        }

        // Called once, after the launcher has read the client.
        public void Opened()
        {
            if (Setting("auto-start") && !Pinned().Contains(Update) && !Pending().Any())
                CountdownMs = AutoStartMs;
        }
        public readonly List<Row> Rows = new List<Row>();
        public readonly Row Play   = new Row { Id = "play",   Kind = RowKind.Button, Label = "Start the game" };
        public readonly Row Update = new Row { Id = "update", Kind = RowKind.Button, Label = "Update available" };

        public int Tab;
        public readonly Dictionary<string, int> Cursor = new Dictionary<string, int>();
        public bool OnBar = true;       // the cursor starts on the button
        public int BarAt;
        public string Flash = "";
        public Prompt Prompt;
        public bool Quit;

        // The work Enter started. One row at a time; Tick advances it.
        public readonly Queue<Row> Work = new Queue<Row>();
        public Row Working;
        public string Stage;    // "unpacking" while the working row's download is being unpacked
        public bool PlayAfterWork;
        public bool IsBusy => Working != null || Work.Count > 0;

        // THE WHOLE UPDATE, for the one bar on the button row. Every row
        // weighs at least 50 KB, so a removal, which has no download, still
        // moves the bar.
        public double BatchKb;
        public double BatchDoneKb;
        public static double Weight(Row row) => row.Moves ? 50 : Math.Max(50, row.Kb);

        public void StartBatch()
        {
            BatchKb = BatchDoneKb = 0;
            foreach (var row in Work) BatchKb += Weight(row);
        }

        public double BatchProgress =>
            BatchKb <= 0 ? 0 : Math.Min(1, (BatchDoneKb + (Working != null ? Weight(Working) * (Working.Progress ?? 0) : 0)) / BatchKb);

        public string CurrentTab => Tabs[Tab];

        // ONE BUTTON AT A TIME. While something of ours is behind, the button
        // is Update available and Start the game is not drawn: a client that
        // is behind is not ready to play. While work runs, the button row is
        // the bar for the whole of it, and stays put until the work ends.
        //
        // AFTER A FAILED UPDATE there are two buttons, because the player has
        // two fair choices and the launcher cannot know which: play on what
        // they have, or try again.
        public List<Row> Pinned()
        {
            bool behind = Rows.Any(NeedsUpdate) || SelfBehind;
            Update.Label = UpdateFailed ? "Try the update again" : FirstInstall ? "Install ComfyCraft" : "Update available";
            Play.Label = behind && UpdateFailed ? "Start anyway" : "Start the game";
            if (IsBusy) return new List<Row> { Update };
            if (behind && UpdateFailed) return new List<Row> { Play, Update };
            return new List<Row> { behind ? Update : Play };
        }

        public List<Row> TabRows(string tab) => Rows.Where(r => r.Tab == tab && !r.Hidden).ToList();

        public List<Row> Stops(string tab) => Rows.Where(r => r.Tab == tab && !r.Hidden && r.Kind != RowKind.Label).ToList();

        public int CursorOn(string tab)
        {
            int n = Stops(tab).Count;
            Cursor.TryGetValue(tab, out int at);
            return Math.Max(0, Math.Min(at, n - 1));
        }

        public Row CurrentRow()
        {
            var pinned = Pinned();
            if (OnBar) return pinned[Math.Min(BarAt, pinned.Count - 1)];
            var stops = Stops(CurrentTab);
            return stops.Count == 0 ? null : stops[CursorOn(CurrentTab)];
        }

        // In list order, which puts every row after what it needs.
        public IEnumerable<Row> Pending() => Rows.Where(r => r.Action != "");

        public int PendingOn(string tab) => Rows.Count(r => r.Tab == tab && !r.Hidden && r.Action != "");

        // The rows that need this one, directly.
        public IEnumerable<Row> Dependents(Row row) => Rows.Where(r => r.AllNeeds.Contains(row));

        // The name a player knows a row by: a hidden row is named for the
        // first row that needs it.
        public string Shown(Row row) => row.Hidden ? Dependents(row).FirstOrDefault()?.Label ?? row.Label : row.Label;

        // TWO PACKAGES CLASH when they write the same patch file, which no
        // manifest can overrule, or when either declares the other.
        public static bool SameFiles(Row a, Row b) => a.Writes.Overlaps(b.Writes);
        public static bool Clashes(Row a, Row b) =>
            SameFiles(a, b) || a.Conflicts.Contains(b.Id) || b.Conflicts.Contains(a.Id);

        // A package may name one of the player's own rows in its conflicts,
        // by id: "patch:Y". That is a patch that breaks it without sharing a
        // file. comfylogin names patch-Y, the auto login it replaced
        // (2026-09-25), since it stays hidden while another one is loaded.
        public static bool Named(Row a, Row b) => a.Conflicts.Contains(b.Id) || b.Conflicts.Contains(a.Id);

        // Every package that clashes with this one, for the detail line, and
        // any row of the player's that it names.
        public IEnumerable<Row> ClashesWith(Row row) =>
            Rows.Where(r => r != row && r.Kind == RowKind.Check && (!r.Unknown || Named(r, row)) && Clashes(r, row));

        // The player's own files that a row, or anything it needs, would
        // overwrite, or that it names as a conflict. Ticking such a row is
        // refused rather than setting the player's file aside behind their
        // back: the owner's call for the named ones, 2026-09-25.
        public List<Row> Blockers(Row row)
        {
            var writes = new HashSet<char>(row.Writes);
            foreach (var need in row.AllNeeds) writes.UnionWith(need.Writes);
            return Rows.Where(r => r.Unknown && r.On &&
                                   (r.Writes.Overlaps(writes) || Named(r, row) || row.AllNeeds.Any(n => Named(r, n)))).ToList();
        }

        // Ticked packages that a row, or anything it needs, cannot be on
        // with. Ticking such a row is refused rather than unticking them:
        // the owner's call, 2026-09-25, so that a 14 GB pack never goes off
        // as a side effect of another tick.
        public List<Row> ClashingOn(Row row)
        {
            var mine = new List<Row> { row };
            mine.AddRange(row.AllNeeds);
            return Rows.Where(r => r.On && r.Kind == RowKind.Check && !mine.Contains(r) && mine.Any(m => Clashes(r, m))).ToList();
        }

        // Ticking a row ticks what it needs, all the way down, brings back
        // what unticking it took unless something ticked since clashes with
        // it, and unticks any row that writes one of the same patch files.
        // Space checks ClashingOn first, so that last step is only reached
        // from other callers. Returns the rows it unticked, so the flash can
        // say so.
        public List<Row> TickOn(Row row, int choice = 0)
        {
            var off = new List<Row>();
            if (row.Kind != RowKind.Check) return off;
            if (!row.On) row.Choice = choice;
            row.On = true;
            foreach (var need in row.AllNeeds) off.AddRange(TickOn(need, need.Choice));
            foreach (var back in row.CascadedOff.Where(r => !r.On && Rows.Contains(r) && ClashingOn(r).Count == 0).ToList()) off.AddRange(TickOn(back, back.Choice));
            row.CascadedOff.Clear();
            foreach (var other in Rows.Where(r => r != row && r.On && r.Kind == RowKind.Check && Clashes(r, row)).ToList())
            {
                off.Add(other);
                off.AddRange(TickOff(other));
            }
            return off;
        }

        // Unticking a row unticks what needs it, and returns those rows so
        // the flash can say what else changed.
        public List<Row> TickOff(Row row)
        {
            var also = new List<Row>();
            row.On = false;
            // A DXVK build read as removed, unticked: the player does not want
            // it back, so the next start does not ask again.
            if (row.Builds != null && row.Have == Have.Missing && Client != null) { Loader.SetSeen(State, Client.Root, null); SaveState(); }
            // Off shows the first choice's size and parts, which is what the
            // next Space will tick. An installed variant keeps its own.
            if (row.Choices != null) row.Choice = row.Have == Have.None ? 0 : row.HaveChoice;
            foreach (var dep in Dependents(row).Where(d => d.On && d.Kind == RowKind.Check).ToList())
            {
                also.Add(dep);
                also.AddRange(TickOff(dep));
            }
            // A hidden part goes with the last ticked row that needs it. The
            // flash does not name it, and ticking the row brings it back.
            foreach (var need in row.AllNeeds.Where(n => n.Hidden && n.On && !Dependents(n).Any(d => d.On)).ToList())
                TickOff(need);
            return also;
        }

        // ---- THE REAL WORK. One row at a time on a worker thread, which runs
        // that row's operations in order and publishes bytes done through
        // Interlocked; everything else, the rows and the screen, belongs to
        // this thread and changes only here, in Tick.
        System.Threading.Tasks.Task job;
        public bool HasJob => job != null;
        List<Op> jobList;
        long jobDone, jobBase, jobTotal;
        int jobOps, jobOpsDone;
        bool starting;

        void TickReal()
        {
            if (job != null && !job.IsCompleted)
            {
                if (Working != null)
                {
                    // A SIZE LEARNED FROM THE HOST, for an addon added from a
                    // link: the row and the batch take it, so the bar reads
                    // "of 39 MB" and not the 50 KB every row weighs at least.
                    if (jobTotal == 0)
                    {
                        long learned = jobList.Sum(o => System.Threading.Interlocked.Read(ref o.Learned));
                        if (learned > 0)
                        {
                            double before = Weight(Working);
                            Working.SizeKb = (int)(learned / 1024);
                            BatchKb += Weight(Working) - before;
                            jobTotal = learned;
                        }
                    }
                    long done = System.Threading.Interlocked.Read(ref jobDone);
                    double p = jobTotal > 0 ? (double)done / jobTotal : (double)jobOpsDone / Math.Max(1, jobOps);
                    Working.Progress = Math.Min(1.0, p);
                    Stage = jobList.Select(o => o.Stage).FirstOrDefault(s => s != null);
                    Working.Busy = Stage ?? (jobTotal > 0 ? $"{(int)(Working.Progress * 100),3}%" : Working.Action == "remove" ? "removing" : "moving");
                }
                return;
            }

            // THE GAME CLOSED, or starting it failed.
            if (starting)
            {
                starting = false;
                var failed = job.Exception?.InnerException;
                job = null;
                if (failed != null) { Flash = failed.Message; return; }
                Quit = true;
                return;
            }

            if (job != null && Working != null)
            {
                var row = Working;
                var failed = job.Exception?.InnerException;
                job = null;
                row.Progress = null;
                row.Busy = null;
                Working = null;
                Stage = null;
                if (failed != null)
                {
                    // A FAILED STEP STOPS THE BATCH: what follows may need it.
                    row.Failed = failed.Message;
                    Work.Clear();
                    PlayAfterWork = false;
                    StopRequested = false;
                    if (row.Ours || row.Kind == RowKind.Required) UpdateFailed = true;
                    BatchKb = BatchDoneKb = 0;
                    Flash = row.Label + ": " + failed.Message;
                    OnBar = true; BarAt = 0;
                    SaveState();
                    return;
                }
                BatchDoneKb += Weight(row);
                Finish(row);
                // THE DISK AGAIN, since the next row's operations are planned
                // from it: a patch this row renamed is somewhere else now.
                Client = Client.Read(Client.Root);
                Client.ReadAside(Home);
                SaveState();
                if (StopRequested)
                {
                    int left = Work.Count;
                    Work.Clear();
                    PlayAfterWork = false;
                    StopRequested = false;
                    Flash = left == 0 ? "stopped" : "stopped, " + left + " not done";
                }
                // IN THIS TICK: nothing is queued now, so the loop would not
                // tick again to finish the batch.
                if (Work.Count == 0) EndBatch();
                return;
            }

            if (Work.Count == 0) { EndBatch(); return; }

            if (BatchKb == 0) StartBatch();
            var next = Work.Dequeue();
            Working = next;
            next.Progress = 0;
            var list = Operations.For(this, next);
            jobList = list;
            jobOps = list.Count; jobOpsDone = 0;
            jobTotal = list.Sum(o => o.Bytes);
            jobBase = 0;
            System.Threading.Interlocked.Exchange(ref jobDone, 0);
            job = System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var op in list)
                {
                    long start = jobBase;
                    op.Run(b => System.Threading.Interlocked.Exchange(ref jobDone, start + b));
                    op.Stage = null;
                    jobBase += op.Bytes > 0 ? op.Bytes : System.Threading.Interlocked.Read(ref op.Learned);
                    System.Threading.Interlocked.Exchange(ref jobDone, jobBase);
                    jobOpsDone++;
                }
            });
        }

        // What a finished row now is on the disk, and what the state file
        // records for it.
        void Finish(Row row)
        {
            string action = row.Action;
            if (row.Kind == RowKind.Radio)
            {
                foreach (var r in Rows.Where(r => r.Tab == row.Tab && r.Kind == RowKind.Radio))
                    r.Have = r == row ? Have.Current : Have.None;
                Flash = "realm set to " + row.Label;
                return;
            }
            if (action == "remove") row.Have = Have.None;
            else if (action == "set aside") row.Have = Have.Aside;
            else if (action == "disable") row.Have = Have.Off;
            else { row.Have = Have.Current; row.HaveChoice = row.Choice; }
            row.Failed = null;

            // The DXVK build this launcher put in place, or none after it set
            // it aside, so the next start does not read its own switch as a
            // file that antivirus removed.
            if (row.Builds != null && Client != null)
                Loader.SetSeen(State, Client.Root, action == "disable" ? null : row.Builds[row.Choice].Sha256);

            if (row.StateKey != "" && Client != null)
            {
                var clients = State.Obj("clients");
                if (clients == null) State["clients"] = clients = new Dictionary<string, object>();
                string key = clients.Keys.FirstOrDefault(k => k.Equals(Client.Root, StringComparison.OrdinalIgnoreCase)) ?? Client.Root;
                var mine = clients.Obj(key);
                if (mine == null) clients[key] = mine = new Dictionary<string, object>();
                if (action == "remove") mine.Remove(row.StateKey);
                else if (action != "disable" && action != "enable")
                    mine[row.StateKey] = row.ChoiceVersion != null ? row.ChoiceVersion[row.Choice] : row.Version;
            }
        }

        // THE END OF A BATCH: tidy up, read everything again, then start the
        // game if that is what Enter asked for.
        void EndBatch()
        {
            BatchKb = BatchDoneKb = 0;
            CleanUp();
            // Replaced itself: the new launcher takes over from here.
            if (RestartInto != null) { Quit = true; return; }
            Reload();
            if (PlayAfterWork)
            {
                PlayAfterWork = false;
                starting = true;
                Flash = "Launching!";
                var ops = Operations.Start(this);
                job = System.Threading.Tasks.Task.Run(() => { foreach (var op in ops) op.Run(_ => { }); });
            }
        }

        // EVERY ROW FROM THE DISK AGAIN, after a batch: one row's work can
        // change another's truth, as a pack that sets aside another pack's
        // file does. Ticks and choices not applied yet are carried across by
        // id, so a row the player changed and has not sent keeps its change.
        public Func<App> Reloader;

        // WHAT THE START BUTTON SAYS about the game, looked up at most every
        // two seconds: it walks the process list.
        DateTime gameCheckedAt;
        string gameNote = "";
        public string GameNote
        {
            get
            {
                if (Client == null) return "";
                if ((DateTime.Now - gameCheckedAt).TotalSeconds > 2)
                {
                    gameCheckedAt = DateTime.Now;
                    gameNote = !Game.Running(Client) ? "" : Game.Stuck(Client).Count > 0 ? "stuck closing: Start ends it" : "it is already running";
                }
                return gameNote;
            }
        }
        void Reload()
        {
            if (Reloader == null) return;
            var wanted = Rows.Where(r => r.Id != null).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => (g.First().On, g.First().Choice));
            var fresh = Reloader();
            Rows.Clear();
            Rows.AddRange(fresh.Rows);
            Client = fresh.Client;
            foreach (var r in Rows)
                if (r.Id != null && wanted.TryGetValue(r.Id, out var w) && (r.Kind == RowKind.Check || r.Kind == RowKind.Setting || r.Kind == RowKind.Radio))
                {
                    r.On = w.On;
                    if (r.Choices != null && w.Choice < r.Choices.Length) r.Choice = w.Choice;
                }
        }

        // After a batch: the unpacking folder goes, and so do finished
        // downloads. A .part stays, so a failed download resumes next time.
        void CleanUp()
        {
            try
            {
                string work = System.IO.Path.Combine(Home, "work");
                if (System.IO.Directory.Exists(work)) System.IO.Directory.Delete(work, true);
                string downloads = System.IO.Path.Combine(Home, "downloads");
                if (System.IO.Directory.Exists(downloads))
                {
                    foreach (var f in System.IO.Directory.GetFiles(downloads))
                        if (!f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) System.IO.File.Delete(f);
                    if (System.IO.Directory.GetFileSystemEntries(downloads).Length == 0) System.IO.Directory.Delete(downloads);
                }
            }
            catch { }
        }

        // comfylauncher.json, every key kept, including the ones only the
        // .ps1 launcher reads.
        public void SaveState()
        {
            if (Client == null || DryRun) return;
            State["last"] = Client.Root;
            var settings = State.Obj("settings") ?? new Dictionary<string, object>();
            settings["clearCache"] = Setting("clear-cache");
            settings["stayIn"] = Setting("stay-in");
            settings["autoStart"] = Setting("auto-start");
            settings["music"] = Setting("music");
            State["settings"] = settings;
            try
            {
                System.IO.Directory.CreateDirectory(Home);
                System.IO.File.WriteAllText(StateFile, Json.Write(State), new System.Text.UTF8Encoding(false));
            }
            catch (Exception e) { Flash = "comfylauncher.json could not be written: " + e.Message; }
        }

        // One step of the fake work. A download takes 2 to 6 seconds whatever
        // its size, so a 4 GB pack can be watched without waiting for one.
        public void Tick()
        {
            if (Client != null && !DryRun) { TickReal(); return; }
            if (Working == null)
            {
                if (Work.Count == 0)
                {
                    BatchKb = BatchDoneKb = 0;
                    if (PlayAfterWork)
                    {
                        PlayAfterWork = false;
                        Flash = Setting("clear-cache") ? "the game would start now, cache cleared" : "the game would start now";
                    }
                    return;
                }
                // The first row of new work sizes the whole of it.
                if (BatchKb == 0) StartBatch();
                Working = Work.Dequeue();
                Working.Progress = 0;
            }

            var row = Working;
            string action = row.Action;
            bool moving = row.Moves;
            double step = moving ? 0.34 : Math.Max(1.0 / 120, Math.Min(1.0 / 40, 40.0 / Math.Max(1, row.Kb)));
            row.Progress = Math.Min(1.0, row.Progress.Value + step);
            row.Busy = action == "remove" ? "removing" : moving ? "moving" : $"{(int)(row.Progress * 100),3}%";

            // A FAILED DOWNLOAD stops the whole batch: what follows may need
            // it. The row says so in red, and the buttons offer a way on.
            if (Offline && !moving && row.Progress >= 0.3)
            {
                row.Failed = "could not reach the download host";
                row.Progress = null;
                row.Busy = null;
                Working = null;
                Work.Clear();
                PlayAfterWork = false;
                StopRequested = false;
                UpdateFailed = true;
                BatchKb = BatchDoneKb = 0;
                Flash = row.Label + " failed to download";
                OnBar = true; BarAt = 0;
                return;
            }

            if (row.Progress >= 1.0)
            {
                BatchDoneKb += Weight(row);
                if (action == "remove") row.Have = Have.None;
                else if (action == "set aside") row.Have = Have.Aside;
                else if (action == "disable") row.Have = Have.Off;
                else { row.Have = Have.Current; row.HaveChoice = row.Choice; }
                row.Failed = null;
                row.Progress = null;
                row.Busy = null;
                Working = null;

                // Esc during work lands here: the file in hand is whole, and
                // nothing after it starts.
                if (StopRequested)
                {
                    int left = Work.Count;
                    Work.Clear();
                    PlayAfterWork = false;
                    StopRequested = false;
                    BatchKb = BatchDoneKb = 0;
                    Flash = left == 0 ? "stopped" : "stopped, " + left + " not done";
                }
            }
        }

        const int MB = 1024;

        public static App Sample()
        {
            var app = new App();
            string pack = null;
            Row Add(string tab, RowKind kind, string label, string note = "", bool on = false,
                    Have have = Have.None, int kb = 0, string parts = "")
            {
                var row = new Row { Id = tab + ":" + (pack == null ? "" : pack + ":") + label, Tab = tab, Kind = kind,
                                    Label = label, Note = note, Pack = pack,
                                    On = on || kind == RowKind.Required, Have = have, SizeKb = kb, Parts = parts };
                app.Rows.Add(row);
                return row;
            }
            void Heading(string tab, string text)
            {
                if (app.Rows.Any(r => r.Tab == tab)) Add(tab, RowKind.Label, "");
                Add(tab, RowKind.Label, text);
            }

            // ---- Realm
            Add("Realm", RowKind.Radio, "ComfyCraft", "comfycraft.dedyn.io", on: true, have: Have.Current);
            Add("Realm", RowKind.Radio, "Turtle WoW", "logon.turtle-wow.org");
            Add("Realm", RowKind.Radio, "This machine", "127.0.0.1");
            Add("Realm", RowKind.Add, "Add a realm");

            // ---- Mods: ComfyCraft
            Heading("Mods", "ComfyCraft");
            var content = Add("Mods", RowKind.Required, "ComfyCraft content", "the login screen, and furniture, critter and housing models",
                              have: Have.Behind, kb: 3400, parts: "patch-V.mpq, patch-Z.mpq");
            var ui = Add("Mods", RowKind.Required, "ComfyUI", "the client's own UI, corrected for this server",
                         have: Have.Behind, kb: 1037, parts: "the ComfyUI addon");
            content.Ours = ui.Ours = true;
            // Behind, so an update has an optional row of ours to respect.
            var fog = Add("Mods", RowKind.Check, "Fog and light", "moodier fog, light through the trees, and grass in the wind",
                          on: true, have: Have.Behind, kb: 450, parts: "comfyatmos.dll, comfyatmos.ini, the ComfyAtmosphere addon");
            var time = Add("Mods", RowKind.Check, "Time of day", "choose the time of day, on your screen only",
                           kb: 260, parts: "comfytime.dll, comfytime.ini");
            fog.Ours = time.Ours = true;

            // ---- Mods: game fixes
            Heading("Mods", "Game fixes");
            var helpers = Add("Mods", RowKind.Check, "Vanilla helpers", "needed by the HD packs", on: true, have: Have.Current);
            helpers.Choices = new[] { "standard", "64 bit" };
            helpers.ChoiceKb = new[] { 610, 1900 };
            helpers.ChoiceParts = new[] { "VanillaHelpers.dll", "VanillaHelpers.dll (64 bit build), TextureServer64.exe" };
            Add("Mods", RowKind.Check, "Nampower", "spells cast the moment you press the key", on: true, have: Have.Current, kb: 300, parts: "nampower.dll");
            Add("Mods", RowKind.Check, "UnitXP SP3", "camera and nameplate distance", on: true, have: Have.Current, kb: 900, parts: "UnitXP_SP3.dll");
            Add("Mods", RowKind.Check, "Interact", "a key that talks to whoever you are facing", kb: 90, parts: "Interact.dll, the Interact addon");
            Add("Mods", RowKind.Check, "Transmog", "other players' transmogrified gear shows correctly", on: true, have: Have.Current, kb: 40, parts: "transmogfix.dll");
            Add("Mods", RowKind.Check, "Multi monitor", "fullscreen stays on the monitor you picked", on: true, have: Have.Current, kb: 30, parts: "VanillaMultiMonitorFix.dll");
            Add("Mods", RowKind.Check, "HD fonts", "sharp text at any size", on: true, have: Have.Current, kb: 700, parts: "lexara112.dll");
            Add("Mods", RowKind.Check, "ClassicAPI", "lets addons written for later versions run", kb: 1200, parts: "ClassicAPI.dll");

            // ---- Mods: HD packs. Real names, real sizes, from the R2 host.
            Heading("Mods", "HD packs from Project Reforged");
            pack = "Reforged";
            Add("Mods", RowKind.Link, "Their website", "projectreforged.github.io").Url =
                "https://projectreforged.github.io/vanilla/downloads/turtle/";
            Add("Mods", RowKind.Link, "Their Discord", "discord.gg/qDUtsW2Dm").Url =
                "https://discord.gg/qDUtsW2Dm";
            var core = Add("Mods", RowKind.Check, "Characters and gear", "characters, NPCs, creatures, weapons and armour",
                           kb: 4393 * MB, parts: "patch-A.mpq, patch-C.mpq, patch-G.mpq");
            core.Needs.Add(helpers);
            var extra = Add("Mods", RowKind.Check, "A little extra", "for female characters");
            extra.Parent = core;
            extra.Choices = new[] { "regular", "less thicc" };
            extra.ChoiceKb = new[] { 36 * MB, 51 * MB };
            extra.ChoiceParts = new[] { "patch-L.mpq, by Watchers3D", "patch-L.mpq, by Deezhugs" };
            core.Id = "reforged-characters";
            var tex = Add("Mods", RowKind.Check, "HD textures", "sharper characters and gear");
            tex.Id = "reforged-hd-textures";
            tex.Parent = core;
            tex.Choices = new[] { "standard", "ultra" };
            tex.ChoiceKb = new[] { 517 * MB, 2062 * MB };
            tex.ChoiceParts = new[] { "patch-T.mpq, standard", "patch-T.mpq, ultra base, and patch-U.mpq" };
            var world = Add("Mods", RowKind.Check, "World", "buildings, doodads and the environment",
                            kb: 3792 * MB, parts: "patch-B.mpq, patch-D.mpq, patch-E.mpq");
            world.Needs.Add(helpers);
            Add("Mods", RowKind.Check, "Interface", "buttons, icons and minimap art", kb: 155 * MB, parts: "patch-I.mpq").Needs.Add(helpers);
            Add("Mods", RowKind.Check, "Maps and loading screens", "", kb: 418 * MB, parts: "patch-M.mpq").Needs.Add(helpers);
            Add("Mods", RowKind.Check, "Darker nights", "", kb: 200, parts: "patch-N.mpq").Needs.Add(helpers);
            Add("Mods", RowKind.Check, "Spell effects", "", kb: 7 * MB, parts: "patch-P.mpq").Needs.Add(helpers);
            Add("Mods", RowKind.Check, "Sounds and music", "", kb: 251 * MB, parts: "patch-S.mpq").Needs.Add(helpers);

            // ---- Mods: Faithful Upscale. One 14.7 GB zip on Google Drive
            // that STORES its patches uncompressed, and Drive answers range
            // requests, so each patch can be fetched on its own out of the
            // zip. Sizes are from the zip's own directory, 2026-09-24.
            //
            // Every patch in it is textures only (.blp), no DBC and no model,
            // read from each archive's (listfile). Patch-P carries
            // VanillaHelpers\ResizeCharacterSkin.txt, 4 in the main build and
            // 2 in the performance one: VanillaHelpers reads it to draw the
            // bigger skins, which is why every row here needs it. The
            // README recommends DXVK against memory crashes.
            // No link row: the only page it has is the Drive download itself.
            Heading("Mods", "HD packs from Faithful Upscale");
            pack = "Faithful";
            var faithful = new List<Row>();
            faithful.Add(Add("Mods", RowKind.Check, "World textures", "the whole world, upscaled",
                kb: 10961 * MB, parts: "Patch-B.mpq to Patch-H.mpq"));
            var fchar = Add("Mods", RowKind.Check, "Characters and gear", "characters, weapons and armour, upscaled");
            fchar.Id = "faithful-characters";
            // Declared: Faithful's 4x character textures break on Reforged's
            // HD models, with no patch letter in common, so nothing else
            // would stop the two. Its children need it, so they are covered.
            fchar.Conflicts.Add("reforged-characters");
            fchar.Choices = new[] { "4x", "2x" };
            fchar.ChoiceKb = new[] { 2110 * MB, 510 * MB };
            fchar.ChoiceParts = new[] { "Patch-O.mpq, Patch-P.mpq", "Patch-O.mpq, Patch-P.mpq, the lighter builds" };
            fchar.Advice = "Try 2x if the game runs out of memory in cities.";
            faithful.Add(fchar);
            var fq = Add("Mods", RowKind.Check, "1.18.1 content", "Turtle's newer gear, upscaled", kb: 26 * MB,
                         parts: "Patch-Q.mpq, by Sandrea");
            fq.Parent = fchar;
            faithful.Add(Add("Mods", RowKind.Check, "Interface", "only for the default UI; early work", kb: 11 * MB, parts: "Patch-I.mpq"));
            faithful.Add(Add("Mods", RowKind.Check, "Loading screens", "", kb: 31 * MB, parts: "Patch-L.mpq"));
            var fmaps = Add("Mods", RowKind.Check, "Maps", "some flaws; work in progress", kb: 381 * MB, parts: "Patch-M.mpq");
            fmaps.Advice = "It has no maps for the zones added in 1.18.1.";
            faithful.Add(fmaps);
            foreach (var r in faithful)
            {
                r.Needs.Add(helpers);
                if (r.Advice == "") r.Advice = "DXVK is recommended.";
            }

            // ---- Mods: what no package claims.
            Heading("Mods", "Also in this client");
            pack = null;
            // NAMED FROM ITS OWN Patch.toc, which reads "Twow Raid Visuals
            // 1.4.18", when a patch carries one; otherwise named after its
            // file. The warning is from launcher\patch-names.txt: its seven DBC
            // files are old copies, and they crashed a client on 2026-09-17.
            var odd = Add("Mods", RowKind.Check, "Twow Raid Visuals", "patch-O, not from ComfyCraft", on: true, have: Have.Current,
                          parts: "Data\\patch-O.mpq");
            odd.Unknown = true;
            odd.Advice = "It carries old copies of the game's spell data, which have crashed a client.";
            var cam = Add("Mods", RowKind.Check, "CameraMod.dll", "not known to ComfyCraft", on: true, have: Have.Current, parts: "a line in dlls.txt");
            cam.Unknown = true;

            // ---- Addons: the player's own, and the parts of packages.
            Add("Addons", RowKind.Add, "Add an addon", "paste link to GitHub or to a .zip");
            Heading("Addons", "Part of a mod");
            // The ComfyUI addon is part of the ComfyCraft section, not of the
            // package with its own name.
            foreach (var (name, owner, row) in new[] { ("ComfyUI", "ComfyCraft", ui), ("ComfyAtmosphere", fog.Label, fog) })
            {
                var part = Add("Addons", RowKind.Part, name, "part of " + owner);
                part.PartOf = owner;
                part.Owner = row;
            }
            Heading("Addons", "Your addons");
            string[] theirs =
            {
                "Bagnon", "Bagnon_Forever", "pfQuest", "pfQuest-turtle", "pfUI", "ShaguTweaks", "Atlas",
                "AtlasLoot", "Auctioneer", "EnhTooltip", "Stubby", "KTM", "DPSMate", "WIM", "Postal",
                "MissingCrafts", "SuperMacro", "Clique", "LunaUnitFrames", "Questie",
            };
            foreach (var name in theirs)
                Add("Addons", RowKind.Check, name, "", on: true, have: Have.Current, parts: @"Interface\AddOns\" + name).Unknown = true;

            // ---- Settings. Kept in comfylauncher.json in the real launcher.
            var cache = Add("Settings", RowKind.Setting, "Clear the cache", "clears WDB folder on startup", on: true);
            cache.Id = "clear-cache";
            cache.Advice = "Deletes the WDB folder first, so item, creature and quest changes on the server show at once.";
            var stay = Add("Settings", RowKind.Setting, "Stay logged in", "no logout after 30 minutes idle", on: true);
            stay.Id = "stay-in";
            stay.Advice = "Changes one byte in WoW.exe when the game starts. You still go AFK after 5 minutes.";
            var auto = Add("Settings", RowKind.Setting, "Autostart", "launch game after 8 seconds automatically");
            auto.Id = "auto-start";
            auto.Advice = "Any key stops the countdown.";
            Add("Settings", RowKind.Setting, "Music", "play tune while the launcher is open").Id = "music";

            DeriveWrites(app);
            return app;
        }

        // The letters each row writes: from its real parts when it has them,
        // otherwise from the same text the detail line shows, where
        // "Patch-B.mpq to Patch-H.mpq" is a range.
        public static void DeriveWrites(App app)
        {
            foreach (var row in app.Rows)
            {
                var real = row.PartList.Concat(row.ChoicePartList?.SelectMany(l => l) ?? Enumerable.Empty<Part>())
                                       .Where(p => p.Type == PartType.Patch).ToList();
                if (real.Count > 0) { foreach (var p in real) row.Writes.Add(p.Letter); continue; }

                string text = row.Parts + " " + string.Join(" ", row.ChoiceParts ?? new string[0]);
                var range = System.Text.RegularExpressions.Regex.Match(text, @"patch-([A-Z])\.mpq to patch-([A-Z])\.mpq",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (range.Success)
                    for (char c = char.ToUpper(range.Groups[1].Value[0]); c <= char.ToUpper(range.Groups[2].Value[0]); c++) row.Writes.Add(c);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"patch-([A-Z])\.mpq",
                         System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    row.Writes.Add(char.ToUpper(m.Groups[1].Value[0]));
            }
        }
    }
}
