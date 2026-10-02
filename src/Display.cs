using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ComfyLauncher
{
    // THE DISPLAY AND VIEW SETTINGS: lines in WTF\Config.wtf, drawn as
    // < choice > rows on the Settings tab. Ported from ComfyLauncher.ps1's
    // $DisplayModes, Get-ScreenResolutions, the saved displays and
    // $ViewSettings, whose CVar names were each checked against WoW.exe.
    //
    // A change is written with the other work, when Enter or Start the game
    // runs, and refused while the game runs: WoW rewrites Config.wtf when it
    // exits, so a value written under it is lost.
    static class Display
    {
        public const string ModeId = "display:mode", ResolutionId = "display:resolution", PresetId = "display:preset", SaveId = "display:save";

        // gxResolution is never touched by a mode: a mode is right on every
        // monitor, a resolution is not.
        static readonly (string Key, string Text, string Note, string Window, string Maximize)[] Modes =
        {
            ("fullscreen", "Fullscreen", "the whole screen", "0", "0"),
            ("windowed", "Windowed", "a window you can move", "1", "0"),
            ("borderless", "Windowed, maximized", "no border, fills the screen", "1", "1"),
        };

        // FoV is stored in radians and read in degrees. Ground clutter
        // distance and nameplate range are absent on purpose: this client has
        // no CVar for either.
        //
        // IN STEPS SMALL ENOUGH FOR LEFT AND RIGHT: every 5 degrees of field
        // of view, every 5 yards of camera, and render distance by 100 up to
        // vanilla's 777. Every value the .ps1 offered is still a step, and
        // the ones it named keep their notes.
        static readonly (string Id, string Label, (string Text, string Note, (string, string)[] Sets)[] Options)[] Views =
        {
            ("view:fov", "Field of view", Enumerable.Range(0, 11).Select(i => 70 + i * 5).Select(deg => (
                deg.ToString(),
                deg == 90 ? "vanilla" : deg == 110 ? "suits a widescreen monitor" : deg == 120 ? "very wide" : "",
                new[] { ("FoV", (deg * Math.PI / 180).ToString("0.0000", CultureInfo.InvariantCulture)) })).ToArray()),
            ("view:farclip", "Render distance", new[] { 177, 277, 377, 477, 577, 677, 777, 1000, 1250, 1500 }.Select(d => (
                d.ToString(),
                d == 777 ? "as far as vanilla goes" : d == 1500 ? "can crash on world entry" : d > 777 ? "further than vanilla" : "",
                new[] { ("farclip", d.ToString()) })).ToArray()),
            ("view:camera", "Camera distance", Enumerable.Range(0, 11).Select(i => 50 + i * 5).Select(y => (
                y.ToString(),
                y == 50 ? "vanilla" : y == 100 ? "the most this client allows" : "",
                new[] { ("cameraDistanceMax", "50"), ("cameraDistanceMaxFactor", (y / 50.0).ToString("0.0", CultureInfo.InvariantCulture)) })).ToArray()),
        };

        // Within about a degree: Turtle's own launcher writes FoV 1.9 for 110,
        // and a row that will not match over a rounding is a row that reads
        // as broken.
        static bool Same(string have, string want)
        {
            if (double.TryParse(have, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) &&
                double.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
                return Math.Abs(a - b) <= 0.03;
            return have.Equals(want, StringComparison.OrdinalIgnoreCase);
        }

        static bool Matches(Client c, IEnumerable<(string Key, string Value)> sets) =>
            sets.All(s => c.ConfigValue(s.Key) != "" && Same(c.ConfigValue(s.Key), s.Value));

        // One Config row. When the client's value is none of the choices it
        // is shown as it is, first, and choosing it writes nothing.
        static Row ConfigRow(Client c, string id, string label, List<(string Text, string Note, List<(string, string)> Sets)> options)
        {
            int now = options.FindIndex(o => o.Sets.Count > 0 && Matches(c, o.Sets));
            if (now < 0)
            {
                var keys = options.SelectMany(o => o.Sets.Select(s => s.Item1)).Distinct().ToList();
                string raw = string.Join(", ", keys.Select(c.ConfigValue).Where(v => v != ""));
                options.Insert(0, (raw != "" ? raw : "default", raw != "" ? "as the client has it" : "not set in Config.wtf", new List<(string, string)>()));
                now = 0;
            }
            return new Row
            {
                Id = id, Tab = "Settings", Kind = RowKind.Config, Label = label, On = true,
                Choices = options.Select(o => o.Text).ToArray(),
                ChoiceNotes = options.Select(o => o.Note).ToArray(),
                ChoiceSets = options.Select(o => o.Sets).ToArray(),
                Choice = now, HaveChoice = now,
                Advice = "Written to WTF\\Config.wtf with the other work. The game must be closed.",
            };
        }

        public static void AddRows(App app, Client c, Action<string> heading)
        {
            if (!c.HasConfig) return;

            heading("Display");
            var preset = new Row { Id = PresetId, Tab = "Settings", Kind = RowKind.Config, Label = "Saved display", On = true,
                                   Advice = "A window and resolution under a name. Del removes the one shown." };
            app.Rows.Add(preset);
            app.Rows.Add(new Row { Id = SaveId, Tab = "Settings", Kind = RowKind.Add, Label = "Save this display", Note = "save the window and resolution" });

            // ALWAYS ONE OF THE THREE: no gxWindow line is fullscreen, which
            // is the game's own default. So no "as the client has it" choice,
            // and the row's index is the table's.
            string mode = c.ConfigValue("gxWindow") != "1" ? "fullscreen" : c.ConfigValue("gxMaximize") == "1" ? "borderless" : "windowed";
            int at = Array.FindIndex(Modes, m => m.Key == mode);
            app.Rows.Add(new Row
            {
                Id = ModeId, Tab = "Settings", Kind = RowKind.Config, Label = "Window", On = true,
                Choices = Modes.Select(m => m.Text).ToArray(),
                ChoiceNotes = Modes.Select(m => m.Note).ToArray(),
                ChoiceSets = Modes.Select(m => new List<(string, string)> { ("gxWindow", m.Window), ("gxMaximize", m.Maximize) }).ToArray(),
                Choice = at, HaveChoice = at,
                Advice = "Written to WTF\\Config.wtf with the other work. The game must be closed.",
            });

            var sizes = c.Resolutions();
            app.Rows.Add(ConfigRow(c, ResolutionId, "Resolution",
                sizes.Select(s => (s, Client.Aspect(s), new List<(string, string)> { ("gxResolution", s) })).ToList()));

            heading("View");
            foreach (var v in Views)
                app.Rows.Add(ConfigRow(c, v.Id, v.Label, v.Options.Select(o => (o.Text, o.Note, o.Sets.ToList())).ToList()));

            RefreshPresets(app);
        }

        // ---- Saved displays, kept in comfylauncher.json for every client,
        // since a resolution belongs to the monitor.

        static List<Dictionary<string, object>> Saved(App app) => app.State.Objs("displays");

        static string ModeKey(Row modeRow) => Modes[Math.Max(0, Math.Min(Modes.Length - 1, modeRow.Choice))].Key;

        // The preset row's choices from the state file, and the one that
        // matches the window and resolution rows as they are chosen now.
        public static void RefreshPresets(App app)
        {
            var preset = app.Rows.FirstOrDefault(r => r.Id == PresetId);
            if (preset == null) return;
            var saved = Saved(app);
            preset.Choices = new[] { "none" }.Concat(saved.Select(d => d.Str("name"))).ToArray();
            preset.ChoiceNotes = new[] { "" }.Concat(saved.Select(d =>
                (Modes.FirstOrDefault(m => m.Key == d.Str("mode")).Text ?? d.Str("mode")) + ", " + d.Str("resolution"))).ToArray();
            preset.Choice = preset.HaveChoice = Matching(app);
        }

        static int Matching(App app)
        {
            var mode = app.Rows.FirstOrDefault(r => r.Id == ModeId);
            var size = app.Rows.FirstOrDefault(r => r.Id == ResolutionId);
            if (mode == null || size == null) return 0;
            var saved = Saved(app);
            int i = saved.FindIndex(d => d.Str("mode") == ModeKey(mode) && d.Str("resolution").Equals(size.Choices[size.Choice], StringComparison.OrdinalIgnoreCase));
            return i + 1;
        }

        // A choice changed. A saved display moves the window and resolution
        // rows; either of those moves the saved display to whichever matches.
        public static void Changed(App app, Row row)
        {
            if (row.Id == PresetId)
            {
                if (row.Choice == 0) return;
                var d = Saved(app)[row.Choice - 1];
                var mode = app.Rows.First(r => r.Id == ModeId);
                var size = app.Rows.First(r => r.Id == ResolutionId);
                int m = Array.FindIndex(Modes, x => x.Key == d.Str("mode"));
                int s = Array.FindIndex(size.Choices, x => x.Equals(d.Str("resolution"), StringComparison.OrdinalIgnoreCase));
                if (s < 0) { app.Flash = d.Str("resolution") + " is not a resolution this monitor offers"; row.Choice = Matching(app); return; }
                if (m >= 0) mode.Choice = m;
                size.Choice = s;
                return;
            }
            if (row.Id == ModeId || row.Id == ResolutionId)
            {
                var preset = app.Rows.FirstOrDefault(r => r.Id == PresetId);
                if (preset != null) preset.Choice = preset.HaveChoice = Matching(app);
            }
        }

        // Saved under a name; a name that exists is replaced.
        public static void Save(App app, string name)
        {
            var mode = app.Rows.First(r => r.Id == ModeId);
            var size = app.Rows.First(r => r.Id == ResolutionId);
            if (!System.Text.RegularExpressions.Regex.IsMatch(size.Choices[size.Choice], @"^\d+x\d+$"))
            {
                app.Flash = "choose a resolution first";
                return;
            }
            var saved = Saved(app);
            saved.RemoveAll(d => d.Str("name").Equals(name, StringComparison.OrdinalIgnoreCase));
            saved.Add(new Dictionary<string, object> { { "name", name }, { "mode", ModeKey(mode) }, { "resolution", size.Choices[size.Choice] } });
            app.State["displays"] = saved.Cast<object>().ToArray();
            app.SaveState();
            RefreshPresets(app);
            app.Flash = name + " saved";
        }

        public static void Remove(App app, Row preset)
        {
            if (preset.Choice == 0) return;
            string name = preset.Choices[preset.Choice];
            var saved = Saved(app);
            saved.RemoveAll(d => d.Str("name").Equals(name, StringComparison.OrdinalIgnoreCase));
            app.State["displays"] = saved.Cast<object>().ToArray();
            app.SaveState();
            RefreshPresets(app);
            app.Flash = name + " removed";
        }

        public static string Describe(App app)
        {
            var mode = app.Rows.First(r => r.Id == ModeId);
            var size = app.Rows.First(r => r.Id == ResolutionId);
            return Modes[mode.Choice].Text + ", " + size.Choices[size.Choice];
        }
    }
}
