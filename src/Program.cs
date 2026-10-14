using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ComfyLauncher
{
    // ComfyLauncher.exe                             the launcher, on the client it sits in
    // ComfyLauncher.exe <folder>                    on that client (a drag and drop, or the .ps1's restart)
    // ComfyLauncher.exe --sample                    the fake data, for the key script tests
    // ComfyLauncher.exe --size 90x30 --keys right,down,space --dump
    //                                               no console: play the keys, print the frame
    //
    // --keys is the test seam. The same Keys.Handle and View.Render run as in
    // the real loop, into a Screen that is printed instead of shown, so a UI
    // bug can be reproduced as a line of text and checked by a script.
    // --each prints the frame after every key, not only the last one.
    //
    //   --client <folder>    the client (default: found by walking up from the exe)
    //   --home <folder>      where comfylauncher.json is (default: beside the exe)
    //   --source <url>       the manifest (default: the state file's, then the live one)
    //   --packages <file>    packages.json, for a manifest without packages
    //   --dry-run            Enter shows the file operations and does none
    //   --running            print each game process and its path, and quit
    //   --classic-console    draw with the console calls Wine uses, on Windows too
    //
    // The .ps1 launcher's own arguments are accepted, because the last .ps1
    // launcher restarts into this exe with them: a client folder, -Source <url>,
    // and -ThenUpdate, -All and -Quiet, which change nothing here.
    static class Program
    {
        // True under --keys, so nothing reaches outside the process.
        public static bool Scripted;

        static readonly string[] Valued = { "--client", "--home", "--source", "-Source", "--packages", "--keys", "--size", "--free", "--clipboard" };

        static int Main(string[] args)
        {
            string keys = null;
            int width = 100, height = 34;
            bool each = false;
            string Arg(string name) { int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

            // What Game.Running sees: each game process and the path it reads.
            if (args.Contains("--running"))
            {
                foreach (var line in Game.Probe()) Console.WriteLine(line);
                return 0;
            }

            // The launcher's tune, written to a .wav to listen to.
            if (Arg("--music-wav") != null)
            {
                Music.Save(Arg("--music-wav"));
                Console.WriteLine("wrote " + Arg("--music-wav"));
                return 0;
            }

            // A folder with no flag in front: a drag and drop onto the exe, or
            // the .ps1 launcher restarting into this one.
            string positional = args.Where((a, i) => !a.StartsWith("-") && (i == 0 || !Valued.Contains(args[i - 1], StringComparer.OrdinalIgnoreCase)))
                                    .FirstOrDefault(System.IO.Directory.Exists);

            App app;
            if (args.Contains("--sample")) app = App.Sample();
            else
            {
                // A start that shows a window says what it is doing while the
                // window is still blank, and logs how long each step took.
                bool live = !args.Contains("--keys") && !args.Contains("--dump");
                StartLog.Begin(live);
                StartLog.Say("ComfyCraft Launcher " + SelfUpdate.Version + ": loading");
                if (!args.Contains("--keys")) SelfUpdate.CleanUp();
                string error;
                app = LoadReal(Arg("--client") ?? positional, Arg("--home"), Arg("--source") ?? Arg("-Source"), Arg("--packages"), out error);
                if (app == null)
                {
                    Console.Error.WriteLine(error);
                    if (!args.Contains("--keys")) { Console.Error.WriteLine("Press a key to close."); try { Console.ReadKey(true); } catch { } }
                    return 1;
                }
                app.DryRun = args.Contains("--dry-run");
                // The realms the loader merged, and the manifest it fetched,
                // written down once, as the .ps1 does on its first run.
                app.SaveState();
            }

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--keys": keys = args[++i]; break;
                    case "--each": each = true; break;
                    case "--dump": keys = keys ?? ""; break;
                    case "--size":
                        var wh = args[++i].Split('x');
                        width = int.Parse(wh[0]);
                        height = int.Parse(wh[1]);
                        break;
                    // The fake client with nothing behind, so the auto start
                    // has something to count down on.
                    case "--current":
                        foreach (var r in app.Rows.Where(r => r.Have == Have.Behind)) r.Have = Have.Current;
                        break;
                    case "--autostart":
                        app.Rows.First(r => r.Id == "auto-start").On = true;
                        break;
                    // Every download fails at 30 percent.
                    case "--offline":
                        app.Offline = true;
                        break;
                    // Free disk space in GB, for the confirm step.
                    case "--free":
                        app.FreeKbOverride = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture) * 1024 * 1024;
                        break;
                    // What ctrl+v and shift+insert paste, under --keys.
                    case "--clipboard":
                        string pasted = args[++i];
                        Keys.Clipboard = () => pasted;
                        break;
                }
            }

            app.OnCountdownEnd = Keys.StartGame;
            app.Opened();
            // What loading had to say, beside the button for the first key.
            if (app.Notes.Count > 0) app.Flash = string.Join("; ", app.Notes);

            Scripted = keys != null;
            if (keys != null) return Script(app, keys, width, height, each);
            int code = Live(app);
            // THE NEW LAUNCHER, after a self update: started in this console
            // with the same arguments, and this one goes.
            if (app.RestartInto != null) SelfUpdate.Restart(app.RestartInto, args);
            return code;
        }

        // The client, the state file, the manifest and the packages, read.
        static App LoadReal(string clientArg, string homeArg, string sourceArg, string packagesArg, out string error)
        {
            error = null;
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string home = homeArg ?? exeDir;

            var state = new Dictionary<string, object>();
            string stateFile = System.IO.Path.Combine(home, "comfylauncher.json");
            try { if (System.IO.File.Exists(stateFile)) state = Json.Parse(System.IO.File.ReadAllText(stateFile)) ?? state; }
            catch (Exception e) { error = stateFile + " could not be read: " + e.Message; return null; }
            StartLog.Step("read " + stateFile);

            StartLog.Say("Reading the client.");

            string root = clientArg != null ? Client.Resolve(clientArg) : Client.FindNear(exeDir) ?? Client.Resolve(state.Str("last"));
            if (root == null)
            {
                error = "No WoW client found. Put the launcher inside the client's folder, or pass --client <folder>.";
                return null;
            }
            var client = Client.Read(root);
            StartLog.Step("found and read the client in " + root);

            var notes = new List<string>();
            string url = Loader.ManifestUrl(sourceArg ?? state.Str("source"));
            StartLog.Say("Getting the file list from " + (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http") ? u.Host : url) + ".");
            var manifest = Loader.GetManifest(url, state, notes);
            // A fresh manifest is kept for the next run that cannot reach
            // the server, as the .ps1 does.
            if (manifest != null && !ReferenceEquals(manifest, state.Obj("manifest"))) state["manifest"] = manifest;

            Dictionary<string, object> packages = manifest.Obj("packages");
            if (packages == null)
            {
                string file = packagesArg ?? new[] { home, exeDir }.Select(d => System.IO.Path.Combine(d, "packages.json")).FirstOrDefault(System.IO.File.Exists);
                if (file != null)
                    try { packages = Json.Parse(System.IO.File.ReadAllText(file)); }
                    catch (Exception e) { notes.Add("packages.json could not be read: " + e.Message); }
            }

            packages = packages ?? new Dictionary<string, object>();
            StartLog.Say("Comparing the client with the file list.");
            var app = Loader.Load(client, manifest, url, state, packages, home);
            StartLog.Step("compared the client with the file list");
            // After a batch: the same manifest and state, the disk read again.
            app.Reloader = () => Loader.Load(Client.Read(root), manifest, url, state, packages, home);
            app.Notes.AddRange(notes);
            return app;
        }

        static int Live(App app)
        {
            Terminal.ForceClassic = Environment.GetCommandLineArgs().Contains("--classic-console");
            if (!Terminal.Enter())
            {
                Console.Error.WriteLine("ComfyLauncher needs a console window.");
                return 1;
            }
            if (app.Rows.Any(r => r.Id == "music" && r.On)) Music.Set(true);
            try
            {
                bool dirty = true;
                int w = 0, h = 0;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!app.Quit)
                {
                    if (Terminal.Width != w || Terminal.Height != h) { w = Terminal.Width; h = Terminal.Height; dirty = true; }

                    // The countdown runs on the real clock. The frame only
                    // changes when the whole second does, and Present sends
                    // only changed rows, so redrawing every pass costs little.
                    int ms = (int)clock.ElapsedMilliseconds;
                    clock.Restart();
                    if (app.CountdownMs != null) { app.Elapse(ms); dirty = true; }
                    if (dirty)
                    {
                        var frame = new Screen(w, h);
                        View.Render(app, frame);
                        Terminal.Present(frame);
                        StartLog.Finish(app.Client?.Root);
                        dirty = false;
                    }

                    if (Console.KeyAvailable)
                    {
                        Keys.Handle(app, Console.ReadKey(true));
                        dirty = true;
                    }
                    else if (app.IsBusy || app.PlayAfterWork || app.HasJob)
                    {
                        Thread.Sleep(50);
                        app.Tick();
                        dirty = true;
                    }
                    else Thread.Sleep(20);
                }
            }
            finally
            {
                Terminal.Leave();
            }
            return 0;
        }

        static int Script(App app, string keys, int width, int height, bool each)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            // Set by the first tick: token. From then on only ticks run work.
            bool manual = keys.Contains("tick:");
            foreach (var token in keys.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // "tick:N" runs N steps of the work and stops there, to see it
                // part done. Otherwise work runs to the end between keys.
                if (token.StartsWith("tick:"))
                {
                    for (int n = int.Parse(token.Substring(5)); n > 0; n--) app.Tick();
                    manual = true;
                }
                // "sec:N" lets N seconds of the auto start countdown pass.
                else if (token.StartsWith("sec:"))
                {
                    app.Elapse(int.Parse(token.Substring(4)) * 1000);
                    if (!manual)
                        RunWork(app);
                }
                else
                {
                    if (token.StartsWith("type:"))
                        foreach (char c in token.Substring(5)) Keys.Handle(app, new ConsoleKeyInfo(c, 0, false, false, false));
                    else
                        Keys.Handle(app, Parse(token.Trim()));
                    if (!manual)
                        RunWork(app);
                }
                if (each) Print(app, width, height, token.Trim());
                if (app.Quit) break;
            }
            if (!each) Print(app, width, height, null);
            return 0;
        }

        // Work runs to the end between keys, so a script sees results. Fake
        // work is ticks; real work is a thread, waited for, ten minutes at most.
        static void RunWork(App app)
        {
            var giveUp = DateTime.Now.AddMinutes(10);
            while ((app.IsBusy || app.PlayAfterWork || app.HasJob) && DateTime.Now < giveUp)
            {
                app.Tick();
                if (app.HasJob) Thread.Sleep(20);
            }
        }

        static void Print(App app, int width, int height, string after)
        {
            var frame = new Screen(width, height);
            View.Render(app, frame);
            if (after != null) Console.WriteLine("---- after " + after + " " + new string('-', Math.Max(0, width - 12 - after.Length)));
            Console.Write(frame.Dump());
        }

        static readonly Dictionary<string, ConsoleKey> Named = new Dictionary<string, ConsoleKey>(StringComparer.OrdinalIgnoreCase)
        {
            { "up", ConsoleKey.UpArrow }, { "down", ConsoleKey.DownArrow },
            { "left", ConsoleKey.LeftArrow }, { "right", ConsoleKey.RightArrow },
            { "space", ConsoleKey.Spacebar }, { "enter", ConsoleKey.Enter },
            { "esc", ConsoleKey.Escape }, { "del", ConsoleKey.Delete },
            { "home", ConsoleKey.Home }, { "end", ConsoleKey.End },
            { "backspace", ConsoleKey.Backspace },
        };

        // A named key. "type:some text" is handled by Script, one key per character.
        static ConsoleKeyInfo Parse(string token)
        {
            // Pastes as a legacy console hands them over: the key, not the text.
            if (token.Equals("ctrl+v", StringComparison.OrdinalIgnoreCase)) return new ConsoleKeyInfo('\x16', ConsoleKey.V, false, false, true);
            if (token.Equals("shift+insert", StringComparison.OrdinalIgnoreCase)) return new ConsoleKeyInfo('\0', ConsoleKey.Insert, true, false, false);
            if (Named.TryGetValue(token, out var key))
                return new ConsoleKeyInfo(key == ConsoleKey.Spacebar ? ' ' : key == ConsoleKey.Enter ? '\r' : '\0',
                                          key, false, false, false);
            throw new ArgumentException("Unknown key: " + token);
        }
    }
}
