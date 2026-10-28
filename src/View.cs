using System;
using System.Collections.Generic;
using System.Linq;

namespace ComfyLauncher
{
    // THE WHOLE SCREEN IS A FUNCTION OF THE APP STATE. Render reads App and
    // writes a Screen; it changes nothing and draws nothing on the console.
    // To change how something looks, change it here and nowhere else.
    static class View
    {
        const int LabelWidth = 24;        // "Maps and loading screens"
        const int SizeWidth = 9;          // "  4.3 GB" before the status column
        const int MarkX = 4;              // "  > " then the mark
        const int LabelX = 8;             // "  > [x] " then the label
        const int NoteX = LabelX + LabelWidth + 1;
        const int StatusWidth = 10;

        static readonly string[] Banner =
        {
            @" ,-----.                  ,---.          ,-----.                ,---.  ,--.",
            @"'  .--./ ,---. ,--,--,--./  .-',--. ,--.'  .--./,--.--. ,--,--./  .-',-'  '-. " + "♥",
            @"|  |    | .-. ||        ||  `-, \  '  / |  |    |  .--'' ,-.  ||  `-,'-.  .-'",
            @"'  '--'\' '-' '|  |  |  ||  .-'  \   '  '  '--'\|  |   \ '-'  ||  .-'  |  |",
            @" `-----' `---' `--`--`--'`--'  .-'  /    `-----'`--'    `--`--'`--'    `--'",
            @"                               `---'",
        };

        // <Version> in the .csproj.
        static string Release => SelfUpdate.Version;

        public static void Render(App app, Screen s)
        {
            int w = s.Width, h = s.Height;
            int statusX = Math.Max(NoteX + 4, w - StatusWidth - 1);
            int noteWidth = Math.Max(0, statusX - 2 - NoteX);
            int y = 0;

            // The art needs 84 columns and room left over for the list.
            // The release sits on the line under the art, with no gap.
            bool banner = w >= 84 && h >= 30;
            y++;
            if (banner)
                foreach (var line in Banner) s.Put(2, y++, line, Ink.Cyan);

            // The release. The banner already says ComfyCraft, so the name
            // is added only in a window too small for the banner. What is
            // behind is said by the Update available button, not here.
            s.Put(2, y, (banner ? "" : "ComfyCraft ") + "Launcher v" + Release, Ink.Gray);
            y += 2;

            var pinned = app.Pinned();
            var current = app.CurrentRow();
            foreach (var row in pinned)
                DrawRow(s, y++, row, row == current, noteWidth, statusX, app);
            y++;

            // The tab bar. The tab you are on is the only bright word, and a
            // tab with work waiting carries the count inside its brackets.
            int x = 2;
            for (int i = 0; i < app.Tabs.Count; i++)
            {
                bool sel = i == app.Tab;
                Ink ink = sel ? Ink.White : Ink.DarkGray;
                x = s.Put(x, y, (sel ? "[ " : "  ") + app.Tabs[i], ink);
                int n = app.PendingOn(app.Tabs[i]);
                if (n > 0) x = s.Put(x, y, $" ({n})", Ink.Yellow);
                x = s.Put(x, y, sel ? " ]" : "  ", ink);
            }
            y += 2;

            // The rows, or the prompt in their place. Four lines stay free at
            // the bottom: the scroll counter, a blank row, the controls, and
            // one blank row under the controls so they do not sit on the
            // window's edge.
            int viewRows = Math.Max(1, h - y - 4);
            if (app.Picker != null) DrawPicker(s, y, app.Picker, viewRows + 1, statusX);
            else if (app.Confirm != null) DrawConfirm(s, y, app.Confirm, viewRows + 1);
            else if (app.Prompt != null) DrawPrompt(s, y, app, app.Prompt, viewRows + 1, statusX);
            else DrawRows(s, y, viewRows, app, current, noteWidth, statusX);

            s.Put(0, h - 2, Controls(app, w), Ink.DarkGray);
        }

        static void DrawRows(Screen s, int top, int viewRows, App app, Row current, int noteWidth, int statusX)
        {
            var rows = app.TabRows(app.CurrentTab);
            var stops = app.Stops(app.CurrentTab);
            int cursorRow = rows.IndexOf(app.OnBar ? stops.ElementAtOrDefault(app.CursorOn(app.CurrentTab)) : current);

            // The window scrolls with the cursor. The frame never grows past
            // the console, whatever the tab holds. One row is kept blank
            // between the list and the detail line.
            int listRows = Math.Max(1, viewRows - 1);
            int start = 0;
            if (rows.Count > listRows)
                start = Math.Max(0, Math.Min(cursorRow - listRows / 2, rows.Count - listRows));

            int y = top;
            for (int i = start; i < Math.Min(start + listRows, rows.Count); i++, y++)
            {
                var row = rows[i];
                if (row.Kind == RowKind.Label) s.Put(2, y, row.Label, Ink.White);
                else DrawRow(s, y, row, row == current, noteWidth, statusX, app);
            }

            // THE DETAIL LINE: what the row under the cursor is made of, and
            // what it needs. File names stay out of the list and are still
            // one look away. The scroll counter shares the line, on the right.
            int line = top + viewRows;
            int counterX = statusX;
            if (rows.Count > listRows)
                s.Put(counterX, line, $"{app.CursorOn(app.CurrentTab) + 1} of {stops.Count}", Ink.DarkGray);
            if (!app.OnBar && current != null)
                Tooltip(s, line, Detail(app, current), counterX);
        }

        // A DETAIL LINE, marked (i) in the mark column so it reads as a note
        // about the row under the cursor and not as another row. Its text
        // starts where the labels do.
        static void Tooltip(Screen s, int y, string text, int right)
        {
            if (string.IsNullOrEmpty(text)) return;
            s.Put(MarkX, y, "(i)", Ink.Cyan);
            s.Put(LabelX, y, CutAtWord(text, right - LabelX - 2), Ink.DarkGray);
        }

        public static string Detail(App app, Row row)
        {
            if (row.Id == WoWSilicon.MacAppId || row.Id == Defender.Id || row.Id == Report.Id) return row.Advice;
            if (row.Kind == RowKind.Link) return "Opens " + row.Url + (Keys.IsFolder(row) ? " in Explorer." : " in your browser.");
            if (row.Kind == RowKind.Setting || row.Kind == RowKind.Config || row.Id == Display.SaveId) return row.Advice;
            if (row.Kind == RowKind.Part)
                return row.PartOf == "ComfyCraft"
                    ? row.Label + " comes with ComfyCraft and is always on."
                    : row.Label + " is turned on and off with " + row.PartOf + ", on the Mods tab.";
            if (row.Builds != null && !row.On)
                return "DXVK off: the game draws with Direct3D 9. Space picks a build.";
            // A hidden row it needs is part of it: its files are listed here.
            var hidden = row.AllNeeds.Where(n => n.Hidden).ToList();
            string parts = string.Join(", ", new[] { row.PartsNow }.Concat(hidden.Select(n => n.PartsNow)).Where(p => p != ""));
            if (parts == "") return "";
            string text = row.Label + ": " + parts;
            int kb = row.Kb + hidden.Sum(n => n.Kb);
            if (kb > 0) text += ", " + Size(kb);
            text += ".";
            var needs = row.AllNeeds.Where(n => !n.Hidden).Select(n => n.Label).ToList();
            // DXVK is a .dll too, but the game loads it from beside WoW.exe.
            if (row.Builds == null && (parts.Contains(".dll") || (row.ChoiceParts?.Any(p => p.Contains(".dll")) ?? false)))
                needs.Add("VanillaFixes");
            if (needs.Count > 0) text += " Needs " + string.Join(" and ", needs) + ".";
            // Before the advice: a narrow window cuts the end of this line, and
            // a conflict matters more than a tip.
            if (!row.Unknown)
            {
                // The name alone where both rows are in one pack, the pack
                // too where they are not: both packs have a Characters and gear.
                var clash = app.ClashesWith(row).Select(r => r.Pack == row.Pack ? r.Label : r.FullName).ToList();
                if (clash.Count > 0) text += " Cannot be on with " + string.Join(", ", clash) + ".";
            }
            if (row.Advice != "") text += " " + row.Advice;
            if (row.Failed != null) text += " The last try failed: " + row.Failed + ".";
            if (row.Have == Have.Aside) text += " Set aside, not deleted. Space brings it back.";
            else if (row.Unknown) text += " Unticking sets it aside; it is not deleted.";
            return text;
        }

        static void DrawRow(Screen s, int y, Row row, bool selected, int noteWidth, int statusX, App app)
        {
            // COLOUR MEANS THE CURSOR IS HERE, and nothing else.
            Ink ink = selected ? Ink.White : Ink.DarkGray;
            s.Put(2, y, selected ? ">" : " ", ink);

            if (row == app.Update && app.IsBusy)
            {
                double p = app.BatchProgress;
                // NAMED FOR THE ROW AT WORK: adding an addon is not an update.
                string doing = app.Working?.Action;
                string verb = app.FirstInstall || doing == "install" || doing == "reinstall" ? "Installing"
                            : doing == "remove" ? "Removing" : "Updating";
                // A word the row shows in place of its percentage (unpacking,
                // removing) shows here too: the batch sits still meanwhile.
                string busy = app.Working?.Busy;
                s.Put(MarkX, y, app.StopRequested ? "Stopping" : verb, ink);
                s.Put(NoteX, y, Bar(p, app.BatchKb * p, app.BatchKb, noteWidth), Ink.Cyan);
                s.Put(statusX, y, busy != null && !busy.EndsWith("%") ? busy : $"{(int)(p * 100),3}%", Ink.Cyan);
                return;
            }

            if (row.Kind == RowKind.Button)
            {
                // A button takes the mark's columns back.
                //
                // THE ANSWER TO THE LAST KEY stands beside the button, for one
                // keypress: the button is where you look to see if you can
                // play. It takes the place of the list of what is behind.
                string label = row.Label;

                // THE COUNTDOWN stands where the flash would, in whole seconds.
                if (row == app.Play && app.CountdownMs != null)
                {
                    int left = (app.CountdownMs.Value + 999) / 1000;
                    s.Put(MarkX, y, label, ink);
                    s.Put(NoteX, y, "starts in " + left, Ink.Cyan);
                    return;
                }

                // Only the first button carries it, when there are two.
                if (app.Flash != "" && app.Pinned()[0] == row)
                {
                    int end = s.Put(MarkX, y, label, ink);
                    s.Put(Math.Max(NoteX, end + 2), y, Cut(app.Flash, statusX - NoteX - 2), Ink.Yellow);
                    return;
                }
                // What is behind, named; a first install needs no list, the
                // label says it.
                if (row == app.Update && !app.FirstInstall)
                    label += "  " + string.Join(", ", (app.SelfBehind ? new[] { "the launcher" } : new string[0])
                                                          .Concat(app.Rows.Where(app.NeedsUpdate).Select(app.Shown).Distinct()));
                int after = s.Put(MarkX, y, Cut(label, statusX - MarkX - 2), ink);
                // The game, when it is already running or stuck closing.
                if (row == app.Play && app.GameNote != "")
                    s.Put(Math.Max(NoteX, after + 2), y, app.GameNote, Ink.Yellow);
            }
            else
            {
                // A CHILD IS INDENTED under the row it needs. The mark moves
                // with the label; the note and status columns do not move.
                int indent = row.Parent != null ? 2 : 0;
                // A LINK IS A BUTTON and, like Start the game, takes the mark's
                // columns back: its label lines up with the checkboxes. So does
                // a row that opens a prompt (Find an addon, Add a realm), which
                // drew a + until 2026-09-24, and a < choice > setting, which has
                // no mark and sat four columns in beside them.
                if (row.Kind == RowKind.Link || row.Kind == RowKind.Add || row.Kind == RowKind.Config)
                    s.Put(MarkX, y, Cut(row.Label, LabelWidth + LabelX - MarkX), ink);
                else
                {
                    s.Put(MarkX + indent, y, Mark(row), ink);
                    s.Put(LabelX + indent, y, Cut(row.Label, LabelWidth - indent), ink);
                }

                if (row.Progress.HasValue)
                {
                    string bar = row.Action == "remove" ? Bar(row.Progress.Value, 0, 0, noteWidth)
                        : Bar(row.Progress.Value, row.Kb * row.Progress.Value, row.Kb, noteWidth);
                    s.Put(NoteX, y, bar, Ink.Cyan);
                }
                else
                {
                    // THE SIZE shows only for what is not on the disk yet:
                    // it is the cost of ticking, and an installed row costs
                    // nothing.
                    bool costs = row.Kb > 0 && row.Kind == RowKind.Check && row.Have == Have.None;
                    int room = noteWidth - SizeWidth;
                    int x = NoteX;

                    // A CHOICE LEADS THE NOTE while the row is on, so what
                    // Space changed is next to the label it belongs to.
                    //
                    // BEING ADJUSTED, the brackets turn into arrows: they say
                    // Left and Right change this now, not the tab.
                    //
                    // DXVK OFF IS A VALUE IN ITS LIST, the one after the last
                    // build: Space steps there and on to the first. Shown as
                    // nothing, players took the list to have no off.
                    if (row.Choices != null && (row.On || row.Builds != null))
                    {
                        bool editing = app.Editing == row;
                        string chosen = row.On ? row.Choices[row.Choice] : "off";
                        string value = editing ? "◄ " + chosen + " ►" : "< " + chosen + " >";
                        x = s.Put(x, y, value, editing ? Ink.White : selected ? Ink.Cyan : ink);
                        x += 2;
                    }
                    // A choice with notes of its own says the chosen one's.
                    string note = row.ChoiceNotes != null ? row.ChoiceNotes[row.Choice] : row.Note;
                    s.Put(x, y, CutAtWord(note, Math.Max(0, room - (x - NoteX))), ink);
                    if (costs)
                    {
                        string size = Size(row.Kb);
                        s.Put(statusX - 2 - size.Length, y, size, Ink.DarkGray);
                    }
                }
            }

            var (word, wordInk) = Status(row);
            s.Put(statusX, y, word, wordInk);
        }

        static string Mark(Row row)
        {
            switch (row.Kind)
            {
                case RowKind.Check:
                case RowKind.Setting: return row.On ? "[x]" : "[ ]";
                // Always ticked, and Space says why it cannot be unticked.
                case RowKind.Required: return "[x]";
                // The owner's tick, shown here and changed there.
                case RowKind.Part:     return row.Owner == null || row.Owner.On ? "[x]" : "[ ]";
                case RowKind.Radio: return row.On ? "(o)" : "( )";
                case RowKind.Add:   return " + ";
                default:            return "   ";
            }
        }

        // THE STATUS COLUMN'S WHOLE VOCABULARY, and its colour.
        public static (string, Ink) Status(Row row)
        {
            if (row.Busy != null) return (row.Busy, Ink.Cyan);
            if (row.Failed != null) return ("failed", Ink.Red);
            string action = row.Action;
            // A required row says so when there is nothing to do to it,
            // which is why it has no checkbox.
            if (row.Kind == RowKind.Required && action == "") return ("required", Ink.DarkGray);
            // On the disk and switched off: ticking it costs no download.
            if (row.Have == Have.Off && action == "") return ("off", Ink.DarkGray);
            if (row.Have == Have.Aside && action == "") return ("set aside", Ink.DarkGray);
            switch (action)
            {
                case "remove":    return (action, Ink.Red);
                case "":          return ("", Ink.DarkGray);
                default:          return (action, Ink.Yellow);
            }
        }

        static void DrawPrompt(Screen s, int y, App app, Prompt p, int rows, int statusX)
        {
            int top = y;
            s.Put(2, y++, p.Title, Ink.Cyan);
            if (!string.IsNullOrEmpty(p.Hint)) s.Put(2, y, p.Hint, Ink.DarkGray);
            y += 2;
            int x = s.Put(2, y, "> ", Ink.White);
            x = s.Put(x, y, p.Text, Ink.White);
            s.Put(x, y, "_", Ink.Cyan);
            if (p.Search == null) return;

            // THE MATCHES, as the picker draws its values. The right column
            // says installed, recommended, or the category. The last line is
            // the detail of the one the cursor is on, as under the rows.
            var found = p.Search(p.Text);
            int at = Math.Min(p.At, Math.Max(0, found.Count - 1));
            y += 2;
            int detail = top + rows - 1;
            int view = Math.Max(1, detail - y - 1);
            int start = found.Count > view ? Math.Max(0, Math.Min(at - view / 2, found.Count - view)) : 0;
            for (int i = start; i < Math.Min(start + view, found.Count); i++, y++)
            {
                var e = found[i];
                bool sel = i == at;
                Ink ink = sel ? Ink.White : Ink.DarkGray;
                bool have = app.Client != null && app.Client.AddOns.Any(a => a.Equals(e.Folder, StringComparison.OrdinalIgnoreCase));
                s.Put(2, y, sel ? ">" : " ", ink);
                s.Put(LabelX, y, Cut(e.Label, LabelWidth), ink);
                s.Put(NoteX, y, CutAtWord(e.Note, statusX - NoteX - 2), ink);
                if (have) s.Put(statusX, y, "installed", Ink.Green);
                else if (e.Recommended) s.Put(statusX, y, "recommended", Ink.Cyan);
                else s.Put(statusX, y, e.Category, Ink.DarkGray);
            }
            if (found.Count == 0) { s.Put(4, y, "No addon matches that.", Ink.DarkGray); return; }
            var cur = found[at];
            string text = cur.Link.Replace("https://", "");
            var needs = cur.Needs.Select(id => app.Rows.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Label).Where(l => l != null).ToList();
            if (needs.Count > 0) text += ". Needs " + string.Join(" and ", needs);
            if (!cur.Installable) text += ". Installs by hand";
            Tooltip(s, detail, text + ".", statusX);
            s.Put(statusX, detail, (at + 1) + " of " + found.Count, Ink.DarkGray);
        }

        // A short head ("Removes") shares its line; a long one (a row's name)
        // gets a line of its own, with its lines indented under it. Lines past
        // the space there is are counted, not drawn.
        // The picker: the values, the one the cursor is on bright, and a
        // window that scrolls with it.
        static void DrawPicker(Screen s, int y, Picker p, int rows, int statusX)
        {
            s.Put(2, y, p.Title, Ink.Cyan);
            y += 2;
            int view = Math.Max(1, rows - 3);
            int start = p.Options.Count > view ? Math.Max(0, Math.Min(p.At - view / 2, p.Options.Count - view)) : 0;
            for (int i = start; i < Math.Min(start + view, p.Options.Count); i++, y++)
            {
                bool sel = i == p.At;
                Ink ink = sel ? Ink.White : Ink.DarkGray;
                s.Put(2, y, sel ? ">" : " ", ink);
                s.Put(LabelX, y, Cut(p.Options[i].Label, LabelWidth), ink);
                s.Put(NoteX, y, CutAtWord(p.Options[i].Note, statusX - NoteX - 2), ink);
            }
            if (p.Options.Count > view) s.Put(statusX, y, (p.At + 1) + " of " + p.Options.Count, Ink.DarkGray);
        }

        static void DrawConfirm(Screen s, int y, Confirm c, int rows)
        {
            s.Put(2, y, c.Title, Ink.Cyan);
            y += 2;
            int last = y + rows - 3;
            for (int i = 0; i < c.Lines.Count; i++)
            {
                if (y > last) { s.Put(4, last, "and " + (c.Lines.Count - i) + " more", Ink.DarkGray); break; }
                var (head, text, ink) = c.Lines[i];
                if (head.Length > 10)
                {
                    s.Put(4, y++, head, Ink.White);
                    if (y > last) { s.Put(4, last, "and " + (c.Lines.Count - i) + " more", Ink.DarkGray); break; }
                    s.Put(6, y++, text, ink);
                }
                else
                {
                    // A continuation line sits under the nearest head above it.
                    string owner = head != "" ? head : c.Lines.Take(i).Select(l => l.Head).LastOrDefault(h => h != "") ?? "";
                    s.Put(4, y, head, Ink.White);
                    s.Put(owner.Length > 10 ? 6 : 16, y++, text, ink);
                }
            }
        }

        // The keys line, per tab: a key that does nothing here is not named.
        static string Controls(App app, int width)
        {
            if (app.Prompt?.Search != null) return "  type to search   up/down move   enter install   esc cancel";
            if (app.Prompt != null) return "  type   enter " + (app.Prompt.Text == "" ? "cancel" : "add") + "   esc cancel";
            if (app.Editing != null) return "  left/right change   SPACE done   esc undo";
            if (app.Picker != null) return "  up/down move   enter choose   esc back";
            if (app.Confirm != null) return app.Confirm.CanGo ? "  enter go ahead   esc back" : "  esc back";
            if (app.CountdownMs != null) return "  any key stops it   enter play now   esc quit";
            // Ticks are locked during work, so SPACE is not named then.
            if (app.StopRequested) return "  stopping after this file";
            if (app.IsBusy) return "  left/right tabs   up/down move   esc stop after this file";
            string extra = app.CurrentTab == "Realm" || app.CurrentRow()?.Id == Display.PresetId ? "   del remove" : "";
            // Enter is not named where it does nothing: while work runs, and
            // on a row with nothing to do.
            string verb = app.IsBusy ? "" : EnterVerb(app);
            string enter = verb == "" ? "" : "   enter " + verb;
            // On a setting, Space starts adjusting rather than choosing.
            string space = app.CurrentRow()?.Kind == RowKind.Config ? "   SPACE adjust" : "   SPACE choose";
            if (width >= 88)
                return "  left/right tabs   up/down move" + space + extra + enter + "   esc quit";
            return "  arrows move" + space + extra + enter + "   esc quit";
        }

        // ENTER SAYS WHAT IT WILL DO to the row under the cursor.
        public static string EnterVerb(App app)
        {
            var row = app.CurrentRow();
            if (row == null) return "";
            if (row == app.Play)
            {
                if (row.Label == "Start anyway") return "play";
                string work = Summary(app.Pending().ToList());
                return work == "" ? "play" : work + " and play";
            }
            if (row == app.Update) return app.FirstInstall ? "install" : "update";
            if (row.Id == Display.SaveId) return "save";
            if (row.Kind == RowKind.Add) return "add";
            if (row.Kind == RowKind.Config) return row.Choices != null && row.Choices.Length > 1 ? "choose" : "";
            if (row.Id == WoWSilicon.MacAppId) return "make";
            if (row.Id == Defender.Id) return "add";
            if (row.Id == Report.Id) return "check";
            if (row.Kind == RowKind.Link) return "open";
            var plan = Keys.Plan(row);
            return plan.Count > 1 ? Summary(plan) : row.Action;
        }

        // ENTER COUNTS WHAT IT DOES: "remove 1, install 2", so a removal
        // three tabs away is on screen when Enter is about to do it.
        static string Summary(List<Row> rows)
        {
            var order = new[] { "remove", "set aside", "install", "reinstall", "update", "change", "restore", "switch", "set" };
            var parts = order.Select(a => (a, n: rows.Count(r => r.Action == a)))
                             .Where(p => p.n > 0)
                             .Select(p => p.a == "switch" ? "switch realm" : p.a + " " + p.n);
            return string.Join(", ", parts);
        }

        // The bar, then how much of how much. A total of 0 draws no amount,
        // which is what a removal is.
        static string Bar(double progress, double doneKb, double totalKb, int width)
        {
            string size = totalKb > 0 ? "  " + Size(doneKb) + " of " + Size(totalKb) : "";
            int inner = Math.Max(4, Math.Min(30, width - 2 - size.Length));
            int filled = (int)Math.Round(progress * inner);
            return "[" + new string('#', filled) + new string('.', inner - filled) + "]" + size;
        }

        // One decimal below ten of a unit, none above: 3.3 MB, 418 MB, 4.3 GB.
        public static string Size(double kb)
        {
            if (kb >= 1000.0 * 1024 * 1024) return $"{kb / 1024 / 1024 / 1024:0.0} TB";
            if (kb >= 100.0 * 1024 * 1024) return $"{kb / 1024 / 1024:0} GB";
            if (kb >= 1000 * 1024) return $"{kb / 1024 / 1024:0.0} GB";
            if (kb >= 10 * 1024)   return $"{kb / 1024:0} MB";
            if (kb >= 1000)        return $"{kb / 1024:0.0} MB";
            return $"{kb:0} KB";
        }

        static string Cut(string text, int width)
        {
            if (width <= 0) return "";
            return text.Length <= width ? text : text.Substring(0, width - 1);
        }

        // Cut at a word rather than mid word, so a long note reads as a
        // sentence that ran out of room and not as a typo.
        static string CutAtWord(string text, int width)
        {
            if (width <= 0) return "";
            if (text.Length <= width) return text;
            string cut = text.Substring(0, width);
            int space = cut.LastIndexOf(' ');
            if (space > width / 2) cut = cut.Substring(0, space);
            return cut.TrimEnd(' ', ',', '.', ':', ';');
        }
    }
}
