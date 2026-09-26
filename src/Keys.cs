using System;
using System.Collections.Generic;
using System.Linq;

namespace ComfyLauncher
{
    // What each key does to the state. Nothing here draws.
    //
    // THE RULE from ComfyLauncher.ps1 holds: arrows move and change nothing,
    // SPACE is the only key that chooses, ENTER does what the controls line
    // says it will.
    static class Keys
    {
        public static void Handle(App app, ConsoleKeyInfo key)
        {
            // Any key clears the flash; a handler below may set it again.
            app.Flash = "";

            // ANY KEY STOPS THE AUTO START, and then does its own job, so
            // Enter on Start the game still starts it at once.
            app.CountdownMs = null;

            if (app.Prompt != null) { HandlePrompt(app, key); return; }

            // ADJUSTING A SETTING: the arrows are the value's, not the tabs'.
            if (app.Editing != null)
            {
                var e = app.Editing;
                switch (key.Key)
                {
                    case ConsoleKey.LeftArrow:
                        if (e.Choice > 0) { e.Choice--; Display.Changed(app, e); }
                        return;
                    case ConsoleKey.RightArrow:
                        if (e.Choice < e.Choices.Length - 1) { e.Choice++; Display.Changed(app, e); }
                        return;
                    case ConsoleKey.Escape:
                        e.Choice = app.EditFrom;
                        Display.Changed(app, e);
                        app.Editing = null;
                        return;
                    case ConsoleKey.Spacebar:
                    case ConsoleKey.Enter:
                        app.Editing = null;
                        return;
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.DownArrow:
                        // Kept, and the cursor moves on as usual.
                        app.Editing = null;
                        break;
                    default:
                        return;
                }
            }

            // The picker: arrows move, Enter chooses, anything else goes back.
            if (app.Picker != null)
            {
                var p = app.Picker;
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow: if (p.At > 0) p.At--; return;
                    case ConsoleKey.DownArrow: if (p.At < p.Options.Count - 1) p.At++; return;
                    case ConsoleKey.Home: p.At = 0; return;
                    case ConsoleKey.End: p.At = p.Options.Count - 1; return;
                    case ConsoleKey.Enter: app.Picker = null; p.Done(p.At); return;
                    default: app.Picker = null; return;
                }
            }

            // The confirm step: Enter goes ahead, any other key goes back.
            if (app.Confirm != null)
            {
                var c = app.Confirm;
                app.Confirm = null;
                if (key.Key == ConsoleKey.Enter && c.CanGo) c.Go();
                return;
            }

            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)) { app.Quit = true; return; }

            string tab = app.CurrentTab;
            int stops = app.Stops(tab).Count;
            int pinned = app.Pinned().Count;
            app.BarAt = Math.Min(app.BarAt, pinned - 1);

            switch (key.Key)
            {
                // ESC NEVER LEAVES A HALF WRITTEN FILE. During work it asks
                // for a stop after the file in hand; only an idle launcher
                // quits.
                case ConsoleKey.Escape:
                    if (app.IsBusy) app.StopRequested = true;
                    else app.Quit = true;
                    break;

                case ConsoleKey.UpArrow:
                    if (app.OnBar) { if (app.BarAt > 0) app.BarAt--; }
                    else if (app.CursorOn(tab) > 0) app.Cursor[tab] = app.CursorOn(tab) - 1;
                    else { app.OnBar = true; app.BarAt = pinned - 1; }
                    break;

                case ConsoleKey.DownArrow:
                    if (app.OnBar)
                    {
                        if (app.BarAt < pinned - 1) app.BarAt++;
                        else if (stops > 0) { app.OnBar = false; app.Cursor[tab] = 0; }
                    }
                    else if (app.CursorOn(tab) < stops - 1) app.Cursor[tab] = app.CursorOn(tab) + 1;
                    break;

                case ConsoleKey.Home:
                    app.OnBar = true; app.BarAt = 0;
                    break;

                case ConsoleKey.End:
                    if (stops > 0) { app.OnBar = false; app.Cursor[tab] = stops - 1; }
                    break;

                // Tabs change what you see, never where the cursor is: from
                // Start the game you can look at a tab and press Enter.
                case ConsoleKey.LeftArrow:
                    if (app.Tab > 0) app.Tab--;
                    break;

                case ConsoleKey.RightArrow:
                    if (app.Tab < app.Tabs.Count - 1) app.Tab++;
                    break;

                case ConsoleKey.Spacebar:
                    Choose(app, app.CurrentRow());
                    break;

                case ConsoleKey.Enter:
                    Enter(app, app.CurrentRow());
                    break;

                case ConsoleKey.Delete:
                    Delete(app, app.CurrentRow());
                    break;
            }
        }

        // SPACE. A plain package ticks and unticks. A package with variants
        // steps through off and each choice, and back to off. Ticking takes
        // what the row needs with it; unticking takes what needs the row.
        // The flash names anything else that changed, since it may be on a
        // part of the list you cannot see.
        static void Choose(App app, Row row)
        {
            // TICKS ARE LOCKED WHILE WORK RUNS. The queue was built from the
            // ticks as they were when Enter was pressed, and a tick changed
            // under it would say one thing while the work did another.
            if (row == null || app.IsBusy) return;
            switch (row.Kind)
            {
                case RowKind.Required:
                    app.Flash = "ComfyCraft needs " + row.Label;
                    return;
                case RowKind.Part:
                    app.Flash = "that is part of " + row.PartOf + ", on the Mods tab";
                    return;
                case RowKind.Radio:
                    foreach (var other in app.Rows.Where(r => r.Tab == row.Tab && r.Kind == RowKind.Radio))
                        other.On = other == row;
                    return;
                case RowKind.Setting:
                    // The launcher's own setting: saved now, there is nothing
                    // to wait for.
                    row.On = !row.On;
                    app.SaveState();
                    if (row.Id == "music") Music.Set(row.On);
                    return;
                case RowKind.Config:
                    // Space starts adjusting it; the arrows do the rest.
                    if (row.Choices == null || row.Choices.Length < 2)
                    {
                        if (row.Id == Display.PresetId) app.Flash = "no saved displays yet";
                        return;
                    }
                    app.Editing = row;
                    app.EditFrom = row.Choice;
                    return;
                case RowKind.Check:
                    break;
                default:
                    return;
            }

            bool last = row.Choices == null || row.Choice >= row.Choices.Length - 1;
            if (row.On && row.Choices != null && !last) { row.Choice++; return; }
            if (row.On)
            {
                // Remembered on the row, so ticking it again brings them back.
                var also = app.TickOff(row);
                row.CascadedOff = also;
                if (also.Count > 0) app.Flash = "also unticked " + string.Join(", ", also.Select(r => r.Label));
                return;
            }

            var blockers = app.Blockers(row);
            if (blockers.Count > 0)
            {
                // Name the file they share: the player's row may be named
                // from its Patch.toc, which hides the letter.
                var b = blockers[0];
                var mine = new HashSet<char>(row.Writes);
                foreach (var need in row.AllNeeds) mine.UnionWith(need.Writes);
                char letter = b.Writes.FirstOrDefault(mine.Contains);
                if (letter != default(char))
                    app.Flash = b.Label + " already uses patch-" + letter + ". Untick it first to set it aside";
                else
                    // Named in the conflicts, with no file in common. What to
                    // do comes first, since a narrow window cuts the end.
                    app.Flash = "Untick " + b.Label + " (patch-" + b.Writes.FirstOrDefault() + ") first: it cannot be on with " + row.Label;
                return;
            }

            // Another package in the way is named, not unticked. Short, since
            // the flash shares its line with the button: the pack once, then
            // its rows.
            var clashing = app.ClashingOn(row);
            if (clashing.Count > 0)
            {
                var packs = clashing.Select(r => r.Pack).Distinct().ToList();
                app.Flash = "Untick " + (packs.Count == 1 && packs[0] != null && packs[0] != row.Pack
                    ? packs[0] + " " + string.Join(", ", clashing.Select(r => r.Label))
                    : string.Join(", ", clashing.Select(r => r.Pack == row.Pack ? r.Label : r.FullName))) + " first";
                return;
            }

            var before = app.Rows.Where(r => r.On).ToList();
            var unticked = app.TickOn(row);
            var added = app.Rows.Where(r => r.On && r != row && !before.Contains(r)).Select(r => r.Label).ToList();
            var said = new List<string>();
            if (added.Count > 0) said.Add("also ticked " + string.Join(", ", added));
            if (unticked.Count > 0) said.Add("unticked " + string.Join(", ", unticked.Select(r => r.FullName).Distinct()) + ": cannot be on together");
            app.Flash = string.Join("; ", said);
        }

        // What Enter on this row does, in order: what it needs first.
        public static List<Row> Plan(Row row, List<Row> into = null)
        {
            into = into ?? new List<Row>();
            foreach (var need in row.AllNeeds) Plan(need, into);
            if (row.Action != "" && !into.Contains(row)) into.Add(row);
            return into;
        }

        static void Enter(App app, Row row)
        {
            if (row == null) return;
            // The button row is the progress bar while work runs, and the
            // controls line stops naming Enter, so there is nothing to say.
            if (app.IsBusy) return;

            if (row == app.Play) { StartGame(app); return; }

            if (row == app.Update)
            {
                // THE LAUNCHER FIRST, AND ALONE: an old launcher may not
                // understand the manifest it just read, so the new one does
                // the rest after it restarts.
                if (app.SelfBehind)
                {
                    RunOrConfirm(app, new List<Row> { SelfUpdate.Row(app, app.SelfUrl, app.SelfSha) }, play: false);
                    return;
                }
                var steps = new List<Row>();
                foreach (var r in app.Rows.Where(app.NeedsUpdate).ToList())
                    foreach (var step in Plan(r)) if (!steps.Contains(step)) steps.Add(step);
                // A realm switch waiting goes with it: on a first run that is
                // ComfyCraft, ticked for the player.
                steps.AddRange(app.Rows.Where(r => r.Kind == RowKind.Radio && r.Action == "switch"));
                RunOrConfirm(app, steps, play: false);
                return;
            }

            // ENTER ON A CHOICE opens the list, for a row with more values
            // than Space should have to step through.
            if (row.Kind == RowKind.Config && row.Choices != null && row.Choices.Length > 1)
            {
                var chosen = row;
                app.Picker = new Picker
                {
                    Title = row.Label,
                    Options = row.Choices.Select((c, i) => (c, row.ChoiceNotes != null ? row.ChoiceNotes[i] : "")).ToList(),
                    At = row.Choice,
                    Done = i => { chosen.Choice = i; Display.Changed(app, chosen); },
                };
                return;
            }

            if (row.Id == Display.SaveId)
            {
                app.Prompt = new Prompt
                {
                    Title = "Save this display",
                    Hint = Display.Describe(app) + ". A name that exists is replaced.",
                    Done = (a, name) => Display.Save(a, name),
                };
                return;
            }

            if (row.Id == "find-addon")
            {
                if (app.Catalog == null)
                {
                    app.Catalog = Catalog.Load(app, out string error);
                    if (app.Catalog == null) { app.Flash = error; return; }
                }
                var all = app.Catalog;
                app.Prompt = new Prompt
                {
                    Title = "Find an addon",
                    Hint = "A name, or a word from what it does.",
                    Search = q => Catalog.Search(all, q),
                    Chose = InstallFromCatalog,
                };
                return;
            }

            if (row.Kind == RowKind.Add)
            {
                bool realm = row.Tab == "Realm";
                app.Prompt = new Prompt
                {
                    Title = realm ? "Add a realm" : "Add an addon",
                    Hint = realm ? "The address, as in set realmlist <address>." : "A GitHub link or a link to a .zip.",
                    Done = realm ? AddRealm : (Action<App, string>)AddAddon,
                };
                return;
            }

            if (row.Kind == RowKind.Radio)
            {
                if (row.Action == "switch")
                {
                    // A real client writes its files through the queue; the
                    // sample only changes the rows.
                    if (app.Client != null) RunOrConfirm(app, new List<Row> { row }, play: false);
                    else SetRealm(app, row);
                }
                return;
            }

            if (row.Kind == RowKind.Part) { app.Flash = "that is part of " + row.PartOf + ", on the Mods tab"; return; }
            if (row.Kind == RowKind.Link)
            {
                // A key script must not open a browser.
                if (!Program.Scripted)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.Url) { UseShellExecute = true });
                app.Flash = "opened in your browser";
                return;
            }
            var plan = Plan(row);
            if (plan.Count == 0) { app.Flash = "nothing to do on this row"; return; }
            RunOrConfirm(app, plan, play: false);
        }

        // Enter on Start the game, and the end of the auto start countdown:
        // everything ticked first, then the game.
        //
        // START ANYWAY, after a failed update, starts the game on what is
        // there and does no work: the work is what just failed.
        public static void StartGame(App app)
        {
            if (app.Client != null && !app.DryRun && Game.Running(app.Client))
            {
                // A client stuck closing is ended, and the start goes on; a
                // game being played is left alone.
                var stuck = Game.Stuck(app.Client);
                if (stuck.Count == 0) { app.Flash = "the game is already running"; return; }
                Game.EndStuck(stuck);
                if (Game.Running(app.Client)) { app.Flash = "the game would not close"; return; }
            }
            if (app.Play.Label == "Start anyway")
            {
                app.PlayAfterWork = true;
                app.Tick();
                return;
            }
            RunOrConfirm(app, app.Pending().ToList(), play: true);
        }

        // A download bigger than this is worth one look before it starts.
        const double BigKb = 1024 * 1024;

        // THE CONFIRM STEP comes before anything that deletes one of ours
        // (only a download brings it back) and before a download over 1 GB.
        // It names what goes, what the download costs, and the free space.
        // Setting the player's own files aside does not ask: Space undoes it.
        static void RunOrConfirm(App app, List<Row> steps, bool play)
        {
            // THE DRY RUN: every file operation, grouped under its row, and
            // no way to go ahead. Esc goes back.
            if (app.DryRun)
            {
                var dry = new Confirm { Title = "Dry run. Nothing is written.", CanGo = false };
                foreach (var r in steps)
                {
                    var ops = Operations.For(app, r).Select(o => o.Text).ToList();
                    for (int i = 0; i < ops.Count; i++)
                        dry.Lines.Add((i == 0 ? r.FullName : "", ops[i], r.Destroys ? Ink.Red : Ink.Gray));
                }
                if (play)
                {
                    var ops = Operations.Start(app).Select(o => o.Text).ToList();
                    for (int i = 0; i < ops.Count; i++) dry.Lines.Add((i == 0 ? "Then" : "", ops[i], Ink.Gray));
                }
                if (dry.Lines.Count == 0) dry.Lines.Add(("", "nothing to do", Ink.DarkGray));
                app.Confirm = dry;
                return;
            }

            // THE GAME HOLDS ITS FILES OPEN, and rewrites Config.wtf when it
            // exits, so nothing is written under a running client. It says
            // so rather than doing part of the job.
            if (app.Client != null && Game.Running(app.Client) && steps.Count > 0)
            {
                app.Flash = Game.Stuck(app.Client).Count > 0 ? "the game is stuck closing: Start the game ends it" : "close the game first";
                return;
            }

            var destroys = steps.Where(r => r.Destroys).ToList();
            double downloadKb = steps.Where(r => !r.Moves && r.Kind != RowKind.Radio).Sum(r => (double)r.Kb);
            double freeKb = app.FreeKb;

            void Go()
            {
                foreach (var r in app.Rows) r.Failed = null;
                app.UpdateFailed = false;
                foreach (var r in steps)
                    if (r.Kind == RowKind.Radio && app.Client == null) SetRealm(app, r);
                    else app.Work.Enqueue(r);
                app.PlayAfterWork = play;
                // Nothing to download or move: the game starts at once.
                if (play && app.Work.Count == 0) app.Tick();
            }

            if (destroys.Count == 0 && downloadKb <= BigKb && downloadKb < freeKb) { Go(); return; }

            var c = new Confirm { Go = Go };
            if (destroys.Count > 0)
                c.Lines.Add(("Removes", string.Join(", ", destroys.Select(r => r.Label + (r.Kb > 0 ? " (" + View.Size(r.Kb) + ")" : ""))), Ink.Red));
            var aside = steps.Where(r => r.Action == "set aside").ToList();
            if (aside.Count > 0)
                c.Lines.Add(("Sets aside", string.Join(", ", aside.Select(r => r.Label)), Ink.Yellow));
            if (downloadKb > 0)
                c.Lines.Add(("Downloads", View.Size(downloadKb) + ". " + View.Size(freeKb) + " free on " + app.Drive, Ink.Gray));
            if (downloadKb >= freeKb)
            {
                c.CanGo = false;
                c.Lines.Add(("", "Not enough space. Free some, or untick something large.", Ink.Red));
            }
            app.Confirm = c;
        }

        static void SetRealm(App app, Row row)
        {
            foreach (var r in app.Rows.Where(r => r.Tab == row.Tab && r.Kind == RowKind.Radio))
                r.Have = r == row ? Have.Current : Have.None;
            app.Flash = "realm set to " + row.Label;
        }

        static void Delete(App app, Row row)
        {
            if (row != null && row.Id == Display.PresetId) { Display.Remove(app, row); return; }
            if (row == null || row.Kind != RowKind.Radio) return;
            if (row.Have == Have.Current) { app.Flash = "that is the realm this client uses now"; return; }
            app.Rows.Remove(row);
            app.Flash = row.Label + " removed";
            // Out of the state file too, and seeded, so it stays gone.
            if (app.Client != null)
            {
                app.State["realms"] = app.State.Objs("realms").Where(r => !r.Str("realm").Equals(row.Address, StringComparison.OrdinalIgnoreCase)).Cast<object>().ToArray();
                app.SaveState();
            }
        }

        // Where a paste reads from. --clipboard <text> replaces it under
        // --keys, so a script never touches the real clipboard.
        public static Func<string> Clipboard = Terminal.ClipboardText;

        static void HandlePrompt(App app, ConsoleKeyInfo key)
        {
            var p = app.Prompt;
            string before = p.Text;
            // A SEARCH: the arrows move through the matches, and Enter takes
            // the one they are on. Typing starts again from the top.
            if (p.Search != null)
            {
                int count = p.Search(p.Text).Count;
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:   p.At = Math.Max(0, p.At - 1); return;
                    case ConsoleKey.DownArrow: p.At = Math.Min(Math.Max(0, count - 1), p.At + 1); return;
                    case ConsoleKey.PageUp:    p.At = Math.Max(0, p.At - 10); return;
                    case ConsoleKey.PageDown:  p.At = Math.Min(Math.Max(0, count - 1), p.At + 10); return;
                    case ConsoleKey.Enter:
                        var found = p.Search(p.Text);
                        if (found.Count == 0) { app.Flash = "no addon matches that"; return; }
                        app.Prompt = null;
                        p.Chose(app, found[Math.Min(p.At, found.Count - 1)]);
                        return;
                }
            }
            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    app.Prompt = null;
                    break;
                case ConsoleKey.Enter:
                    app.Prompt = null;
                    if (p.Text.Trim() != "") p.Done(app, p.Text.Trim());
                    break;
                case ConsoleKey.Backspace:
                    if (p.Text.Length > 0) p.Text = p.Text.Substring(0, p.Text.Length - 1);
                    break;
                // PASTE. Windows Terminal and a console with Ctrl key
                // shortcuts on type the clipboard in as characters, which the
                // default case takes. A legacy console hands over Ctrl+V
                // itself, as \x16, which it would drop: so the clipboard is
                // read here. Its first line only, since Enter submits.
                case ConsoleKey.V when (key.Modifiers & ConsoleModifiers.Control) != 0:
                case ConsoleKey.Insert when (key.Modifiers & ConsoleModifiers.Shift) != 0:
                    string line = Clipboard().Split('\r', '\n').Select(l => l.Trim()).FirstOrDefault(l => l != "") ?? "";
                    p.Text += new string(line.Where(c => !char.IsControl(c)).ToArray());
                    break;
                default:
                    if (!char.IsControl(key.KeyChar)) p.Text += key.KeyChar;
                    break;
            }
            if (p.Text != before) p.At = 0;
        }

        // A CATALOG ADDON: the mods it needs are ticked and go first, then it
        // is fetched as a link would be. One the list marks as not installable
        // opens its page instead, since its zip does not unpack into an addon.
        static void InstallFromCatalog(App app, CatalogEntry e)
        {
            if (!e.Installable)
            {
                if (!Program.Scripted)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Repo) { UseShellExecute = true });
                app.Flash = e.Name + " installs by hand. Its page is open in your browser.";
                return;
            }
            var steps = new List<Row>();
            foreach (var id in e.Needs)
            {
                var need = app.Rows.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (need == null || need.Kind != RowKind.Check) continue;
                if (!need.On) app.TickOn(need, need.Choice);
                if (need.Action != "") steps.Add(need);
            }
            steps.Add(Operations.AddFromLink(app, e.Link, e.Name));
            RunOrConfirm(app, steps, play: false);
        }

        static void AddRealm(App app, string text)
        {
            // PASTE TOLERANT, as a realmlist copied off a website arrives:
            // "set realmlist play.example.com", quoted, with a scheme or a
            // trailing slash.
            string address = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"^set\s+realmlist\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            address = System.Text.RegularExpressions.Regex.Replace(address.Trim().Trim('"'), @"^[a-z]+://", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).TrimEnd('/');
            if (address == "") return;
            var had = app.Rows.FirstOrDefault(r => r.Kind == RowKind.Radio && r.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            if (had != null) { app.Flash = address + " is already listed"; return; }
            int at = app.Rows.FindIndex(r => r.Tab == "Realm" && r.Kind == RowKind.Add);
            var row = new Row { Id = "Realm:" + address, Tab = "Realm", Kind = RowKind.Radio, Label = address, Note = address, Address = address, Patchlist = address };
            if (app.Client != null)
            {
                var list = app.State.Objs("realms");
                list.Add(new Dictionary<string, object> { { "name", address }, { "realm", address }, { "patch", address } });
                app.State["realms"] = list.Cast<object>().ToArray();
                app.SaveState();
            }
            app.Rows.Insert(at, row);
            app.OnBar = false;
            app.Cursor["Realm"] = app.Stops("Realm").IndexOf(row);
            app.Flash = address + " added";
        }

        static void AddAddon(App app, string link)
        {
            // A real client downloads it now: the prompt asked for something,
            // and the work shows at once. Every addon in the zip goes in.
            if (app.Client != null)
            {
                var add = Operations.AddFromLink(app, link);
                RunOrConfirm(app, new List<Row> { add }, play: false);
                return;
            }
            string name = link.TrimEnd('/').Split('/').Last();
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            int at = app.Rows.FindIndex(r => r.Tab == "Addons" && r.Label == "Your addons") + 1;
            var row = new Row { Id = "Addons:" + name, Tab = "Addons", Kind = RowKind.Check, Label = name,
                                Note = link, On = true, Have = Have.None, SizeKb = 300,
                                Unknown = true, Parts = @"Interface\AddOns\" + name };
            app.Rows.Insert(at, row);
            app.Work.Enqueue(row);
            app.OnBar = false;
            app.Cursor["Addons"] = app.Stops("Addons").IndexOf(row);
        }
    }
}
