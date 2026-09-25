using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ComfyLauncher
{
    // THE REAL ROWS: the manifest, packages.json, comfylauncher.json and the
    // client's disk, turned into the same App the sample builds. Reads only.
    //
    // The status rules are ComfyLauncher.ps1's (Build-Rows), so the two
    // launchers agree about a client while both exist: an installed version
    // from the state file first, then the disk, then a size or hash when the
    // state file has no record.
    static class Loader
    {
        public const string DefaultSource = "https://comfycraft.dedyn.io/launcher/manifest.json";

        // A URL, a manifest.json, or a folder holding one.
        public static string ManifestUrl(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) source = DefaultSource;
            if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return source.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? source : source.TrimEnd('/') + "/manifest.json";
            return Directory.Exists(source) ? Path.Combine(source, "manifest.json") : source;
        }

        public static string Fetch(string url)
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return File.ReadAllText(url);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var web = new WebClient { Encoding = Encoding.UTF8 })
            {
                web.Headers[HttpRequestHeader.UserAgent] = "ComfyLauncher";
                return web.DownloadString(url);
            }
        }

        // The manifest, or the copy the state file saved from the last run
        // that reached it.
        public static Dictionary<string, object> GetManifest(string url, Dictionary<string, object> state, List<string> notes)
        {
            try { return Json.Parse(Fetch(url)); }
            catch (Exception e)
            {
                var saved = state.Obj("manifest");
                notes.Add(saved != null ? "could not reach " + url + ", using the last manifest saved"
                                        : "could not reach " + url + ": " + e.Message);
                return saved;
            }
        }

        static string Absolute(string manifestUrl, string rel)
        {
            if (string.IsNullOrEmpty(rel) || rel.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return rel ?? "";
            if (manifestUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return new Uri(new Uri(manifestUrl), rel).ToString();
            return Path.Combine(Path.GetDirectoryName(manifestUrl) ?? "", rel.Replace('/', '\\'));
        }

        static string ShowParts(IEnumerable<Part> parts) => string.Join(", ", parts.Select(p => p.Show).Distinct());

        public static App Load(Client client, Dictionary<string, object> manifest, string manifestUrl,
                               Dictionary<string, object> state, Dictionary<string, object> packages, string home)
        {
            var app = new App { DryRun = true, Client = client, Source = manifestUrl, Home = home, State = state };
            var self = manifest?.Obj("launcher");
            app.SelfBehind = SelfUpdate.Behind(self.Str("release"));
            app.SelfUrl = Absolute(manifestUrl, self.Str("url"));
            client.ReadAside(home);
            manifest = manifest ?? new Dictionary<string, object>();
            var patchNames = manifest.Obj("patchNames");
            var patchLabels = state.Obj("patchLabels");

            Row Add(string tab, RowKind kind, string label, string note = "")
            {
                var row = new Row { Id = tab + ":" + label, Tab = tab, Kind = kind, Label = label, Note = note ?? "" };
                app.Rows.Add(row);
                return row;
            }
            void Heading(string tab, string text)
            {
                if (app.Rows.Any(r => r.Tab == tab)) Add(tab, RowKind.Label, "");
                Add(tab, RowKind.Label, text);
            }

            // What this copy of the launcher recorded as installed in this
            // client, by item name, or null when it never has.
            Dictionary<string, object> installed = null;
            var clients = state.Obj("clients");
            if (clients != null)
                foreach (var kv in clients)
                    if (kv.Key.Equals(client.Root, StringComparison.OrdinalIgnoreCase)) installed = kv.Value as Dictionary<string, object>;
            string Was(string key) => installed.Str(key);

            // ---- Realm. The state file's list, then the manifest's realms this
            // copy was never offered, then wherever the client points now.
            var seeded = state.Strs("seeded");
            var realms = new List<(string Name, string Realm, string Patch)>();
            void Realm(string name, string realm, string patch)
            {
                if (realm == "" || realms.Any(r => r.Realm.Equals(realm, StringComparison.OrdinalIgnoreCase))) return;
                realms.Add((name == "" ? realm : name, realm, patch == "" ? realm : patch));
            }
            foreach (var r in state.Objs("realms")) Realm(r.Str("name"), r.Str("realm"), r.Str("patch"));
            // ONCE PER ADDRESS: a manifest realm this copy was offered and the
            // player deleted stays deleted, since `seeded` remembers the offer.
            foreach (var r in manifest.Objs("realms"))
                if (!seeded.Any(s => s.Equals(r.Str("realm"), StringComparison.OrdinalIgnoreCase)))
                {
                    Realm(r.Str("name"), r.Str("realm"), r.Str("patch"));
                    seeded.Add(r.Str("realm"));
                }
            // Where the client points now is always listed, and not seeded:
            // it describes the present, it is not an offer.
            Realm(client.Realm, client.Realm, client.Patchlist);
            state["realms"] = realms.Select(r => (object)new Dictionary<string, object> { { "name", r.Name }, { "realm", r.Realm }, { "patch", r.Patch } }).ToArray();
            state["seeded"] = seeded.Cast<object>().ToArray();
            // A FIRST RUN TICKS COMFYCRAFT, the manifest's first realm, so the
            // install that follows points the client at us. After that the
            // tick is where the client points, as the player left it.
            string first = installed == null ? manifest.Objs("realms").Select(r => r.Str("realm")).FirstOrDefault() ?? "" : "";
            foreach (var r in realms)
            {
                var row = Add("Realm", RowKind.Radio, r.Name, r.Realm);
                row.Address = r.Realm;
                row.Patchlist = r.Patch;
                bool now = r.Realm.Equals(client.Realm, StringComparison.OrdinalIgnoreCase);
                row.On = first != "" ? r.Realm.Equals(first, StringComparison.OrdinalIgnoreCase) : now;
                row.Have = now ? Have.Current : Have.None;
            }
            Add("Realm", RowKind.Add, "Add a realm");

            // ---- Mods: ComfyCraft's own items. All required, per the design.
            Heading("Mods", "ComfyCraft");
            var claimedLetters = new HashSet<char>();
            var claimedAddons = new List<(string Folder, Row Owner, string OwnerName)>();
            foreach (var item in manifest.Objs("items"))
            {
                string name = item.Str("name"), kind = item.Str("kind");
                var part = new Part { Url = Absolute(manifestUrl, item.Str("url")), Sha256 = item.Str("sha256"), Size = item.Long("size") };
                string target;
                bool offOnDisk = false;
                string label = name;
                if (kind == "addon")
                {
                    part.Type = PartType.Addon; part.Name = name;
                    target = client.PathOf(@"Interface\AddOns\" + name);
                }
                else
                {
                    string path = item.Str("path");
                    target = client.PathOf(path);
                    var m = Regex.Match(path, @"patch-([A-Za-z])\.mpq$", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        part.Type = PartType.Patch; part.Letter = char.ToUpperInvariant(m.Groups[1].Value[0]);
                        claimedLetters.Add(part.Letter);
                        string pretty = patchNames.Str(part.Letter.ToString());
                        if (pretty != "") label = pretty;
                        offOnDisk = File.Exists(target + ".off");
                    }
                    else { part.Type = PartType.File; part.Name = path; }
                }

                bool onDisk = kind == "addon" ? Directory.Exists(target) : File.Exists(target);
                string have = Was(name);
                Have h;
                if (!onDisk && offOnDisk) h = Have.Off;
                else if (have == "" && !onDisk) h = Have.None;
                else if (!onDisk) h = Have.Missing;
                else if (have == "")
                {
                    // Installed by hand, or by a launcher that did not record
                    // it: current when the files and bytes match the manifest.
                    h = Have.Behind;
                    if (kind == "addon" && item.Long("files") > 0)
                    {
                        var print = Client.DiskPrint(target);
                        if (print.Files == item.Long("files") && print.Bytes == item.Long("bytes")) h = Have.Current;
                    }
                }
                else h = have == item.Str("version") ? Have.Current : Have.Behind;

                // A note that only repeats the label says nothing.
                string note = item.Str("note").Equals(label, StringComparison.OrdinalIgnoreCase) ? "" : item.Str("note");
                var row = Add("Mods", RowKind.Required, label, note);
                row.Id = "item:" + name;
                row.StateKey = name;
                row.Version = item.Str("version");
                row.Supersedes.AddRange(item.Strs("supersedes"));
                row.Have = h;
                row.On = true;
                row.Ours = true;
                row.PartList.Add(part);
                row.Parts = ShowParts(row.PartList);
                row.SizeKb = (int)(part.Size / 1024);
                if (part.Type == PartType.Addon) claimedAddons.Add((name, row, "ComfyCraft"));
                foreach (var old in item.Strs("supersedes")) claimedAddons.Add((old, row, "ComfyCraft, which replaced it"));
            }

            // ---- Mods: client mods, ours in the ComfyCraft section and the
            // rest under Game fixes. A variant is a choice on its base row.
            var mods = manifest.Objs("mods");
            var accounted = new List<string>();
            var gameFixes = new List<Row>();
            var ourMods = new List<Row>();
            // A mod with a `section` gets a heading of its own after Game
            // fixes, in the order the sections first appear.
            var sections = new List<(string title, List<Row> rows)>();
            foreach (var mod in mods.Where(m => !m.Bool("variant")))
            {
                string file = mod.Str("file");
                if (file == "") continue;
                accounted.Add(file);
                bool present = File.Exists(client.PathOf(file));
                bool enabled = client.DllEnabled(file);
                bool inject = mod.Get("inject") is bool b ? b : true;
                string url = mod.Str("url");

                // VanillaFixes starts the game and loads the rest: a row only
                // when the client is missing it, since it is not a switch.
                if (!inject && present) continue;
                if (inject && !present && !enabled && url == "") continue;

                var row = new Row
                {
                    Id = "mod:" + file, Tab = "Mods", Kind = RowKind.Check, Label = mod.Str("name"), Note = mod.Str("note"),
                    Toggle = inject, Ours = mod.Str("from").IndexOf("github.com/aloofbit/", StringComparison.OrdinalIgnoreCase) >= 0,
                };
                row.PartList = ModParts(mod);
                row.StateKey = "mod:" + file;
                row.Version = mod.Str("version");

                if (!inject) { row.Have = Have.None; row.On = false; }
                else
                {
                    row.On = enabled;
                    row.Have = present ? (enabled ? Have.Current : Have.Off) : (enabled ? Have.Missing : Have.None);
                    if (present && url != "" && mod.Str("version") != "")
                    {
                        string was = Was("mod:" + file);
                        bool behind = was != "" ? was != mod.Str("version")
                                    : mod.Str("file_sha256") != "" && !Client.Sha256(client.PathOf(file)).Equals(mod.Str("file_sha256"), StringComparison.OrdinalIgnoreCase);
                        if (behind && row.Have == Have.Current) row.Have = Have.Behind;
                    }
                }

                var variant = mods.FirstOrDefault(v => v.Bool("variant") && v.Str("file").Equals(file, StringComparison.OrdinalIgnoreCase));
                if (variant != null)
                {
                    string baseName = mod.Str("name"), vName = variant.Str("name");
                    string choice = variant.Str("choice") != "" ? variant.Str("choice")
                                  : vName.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) ? vName.Substring(baseName.Length).Trim() : vName;
                    row.Choices = new[] { "standard", choice };
                    row.ChoicePartList = new[] { row.PartList, ModParts(variant) };
                    row.ChoiceParts = row.ChoicePartList.Select(ShowParts).ToArray();
                    row.ChoiceKb = new[] { 0, 0 };
                    row.ChoiceVersion = new[] { mod.Str("version"), variant.Str("version") };
                    bool isVariant = present && Client.Sha256(client.PathOf(file)).Equals(variant.Str("file_sha256"), StringComparison.OrdinalIgnoreCase);
                    row.HaveChoice = row.Choice = isVariant ? 1 : 0;
                }
                else row.Parts = ShowParts(row.PartList);

                // THE DLL IS NOT THE WHOLE MOD. One on the disk with its addon
                // or ini gone reads reinstall, so ticking it fetches the rest:
                // a client that had SuperWoW before SuperAPI came with it.
                if (inject && present && url != "")
                {
                    var list = row.ChoicePartList != null ? row.ChoicePartList[row.HaveChoice] : row.PartList;
                    // An addon that went aside with its switched-off mod is
                    // not gone: switching the mod on moves it back.
                    bool gone = list.Any(p => p.Type == PartType.Addon
                                              ? !Directory.Exists(client.PathOf(@"Interface\AddOns\" + p.Name))
                                                && !(p.OffWithMod && Directory.Exists(Path.Combine(home, "set-aside", "AddOns", p.Name)))
                                            : p.Type == PartType.File && !File.Exists(client.PathOf(p.Name)));
                    if (gone) row.Have = Have.Missing;
                }

                foreach (var p in row.PartList.Where(p => p.Type == PartType.Addon)) claimedAddons.Add((p.Name, row, row.Label));
                string section = mod.Str("section");
                if (row.Ours) ourMods.Add(row);
                else if (section == "") gameFixes.Add(row);
                else
                {
                    int s = sections.FindIndex(x => x.title == section);
                    if (s < 0) { sections.Add((section, new List<Row>())); s = sections.Count - 1; }
                    sections[s].rows.Add(row);
                }
            }
            var dxvk = DxvkRow(client, packages.Obj("dxvk"), home, Was("dxvk"));
            if (dxvk != null) gameFixes.Add(dxvk);
            app.Rows.AddRange(ourMods);
            if (gameFixes.Count > 0)
            {
                Heading("Mods", "Game fixes");
                app.Rows.AddRange(gameFixes);
            }
            foreach (var (title, rows) in sections)
            {
                Heading("Mods", title);
                app.Rows.AddRange(rows);
            }

            // ---- Mods: the HD packs, from packages.json. A file on disk
            // belongs to a package only when its size is the package's.
            var needIds = new Dictionary<Row, List<string>>();
            var parentIds = new Dictionary<Row, string>();
            foreach (var sec in packages.Objs("sections"))
            {
                Heading("Mods", sec.Str("title"));
                string pack = sec.Str("pack"), zip = sec.Str("_zip");
                foreach (var link in sec.Objs("links"))
                    Add("Mods", RowKind.Link, link.Str("name"), link.Str("note")).Url = link.Str("url");

                foreach (var pkg in sec.Objs("packages"))
                {
                    var row = Add("Mods", RowKind.Check, pkg.Str("name"), pkg.Str("note"));
                    row.Id = pkg.Str("id");
                    row.Pack = pack;
                    row.Toggle = true;
                    row.Advice = pkg.Str("advice");
                    row.Conflicts.AddRange(pkg.Strs("conflicts"));
                    needIds[row] = pkg.Strs("needs");
                    if (pkg.Str("parent") != "") parentIds[row] = pkg.Str("parent");

                    List<Part> PartsOf(Dictionary<string, object> holder) => holder.Objs("parts").Select(p => new Part
                    {
                        Type = PartType.Patch, Letter = char.ToUpperInvariant(p.Str("letter")[0]), Size = p.Long("size"),
                        Url = p.Str("url") != "" ? p.Str("url") : zip, ZipOffset = p.Get("zip") != null ? p.Long("zip") : -1,
                        Etag = p.Str("etag"), Crc32 = p.Str("crc32"), Sha256 = p.Str("sha256"),
                    }).ToList();
                    string By(Dictionary<string, object> holder, List<Part> parts) =>
                        ShowParts(parts) + (holder.Str("by") != "" ? ", by " + holder.Str("by") : "");

                    var choices = pkg.Objs("choices");
                    var lists = choices.Count > 0 ? choices.Select(PartsOf).ToList() : new List<List<Part>> { PartsOf(pkg) };
                    if (choices.Count > 0)
                    {
                        row.Choices = choices.Select(c => c.Str("name")).ToArray();
                        row.ChoicePartList = lists.ToArray();
                        row.ChoiceParts = choices.Select((c, i) => By(c, lists[i])).ToArray();
                        row.ChoiceKb = lists.Select(l => (int)(l.Sum(p => p.Size) / 1024)).ToArray();
                    }
                    else
                    {
                        row.PartList = lists[0];
                        row.Parts = By(pkg, lists[0]);
                        row.SizeKb = (int)(lists[0].Sum(p => p.Size) / 1024);
                    }

                    // Which list, if any, is on the disk: every part there with
                    // its exact size. A partial match is a pack missing a file.
                    // Failing that, one the launcher set aside: ticking it
                    // moves the files back.
                    row.Have = Have.None;
                    for (int i = 0; i < lists.Count; i++)
                    {
                        if (!lists[i].All(p => client.AsidePatches.Any(a => a.Letter == p.Letter && a.Size == p.Size))) continue;
                        if (lists[i].Any(p => client.Patches.TryGetValue(p.Letter, out var live) && live.Size == p.Size)) continue;
                        row.Have = Have.Aside; row.On = false;
                        row.Choice = row.HaveChoice = i;
                        break;
                    }
                    for (int i = 0; i < lists.Count; i++)
                    {
                        var found = lists[i].Where(p => client.Patches.TryGetValue(p.Letter, out var d) && d.Size == p.Size)
                                            .Select(p => client.Patches[p.Letter]).ToList();
                        if (found.Count == 0) continue;
                        foreach (var d in found) claimedLetters.Add(d.Letter);
                        row.Choice = row.HaveChoice = i;
                        if (found.Count < lists[i].Count) { row.Have = Have.Missing; row.On = found.Any(d => d.Enabled); }
                        else if (found.All(d => d.Enabled)) { row.Have = Have.Current; row.On = true; }
                        else { row.Have = Have.Off; row.On = false; }
                        break;
                    }
                }
            }

            // ---- Mods: what no package claims. Patches keep the name this
            // copy was told, or the manifest's name for the letter.
            var oddPatches = client.Patches.Values.Where(p => !claimedLetters.Contains(p.Letter)).OrderBy(p => p.Letter).ToList();
            var oddDlls = client.EnabledDlls.Where(f => !accounted.Any(a => a.Equals(f, StringComparison.OrdinalIgnoreCase))).ToList();
            // A set-aside patch no package knows by size is somebody's own,
            // and it comes back the way it went.
            var allParts = app.Rows.SelectMany(r => r.PartList.Concat(r.ChoicePartList?.SelectMany(l => l) ?? Enumerable.Empty<Part>()))
                                   .Where(p => p.Type == PartType.Patch).ToList();
            var oddAside = client.AsidePatches.Where(a => !allParts.Any(p => p.Letter == a.Letter && p.Size == a.Size)).ToList();
            if (oddPatches.Count > 0 || oddDlls.Count > 0 || oddAside.Count > 0)
            {
                Heading("Mods", "Also in this client");
                foreach (var a in oddAside)
                {
                    string l = a.Letter.ToString();
                    string name = patchLabels.Str(l) != "" ? patchLabels.Str(l) : patchNames.Str(l) != "" ? patchNames.Str(l) : "patch-" + l;
                    var row = Add("Mods", RowKind.Check, name, "patch-" + l + ", set aside");
                    row.Id = "aside:" + Path.GetFileName(a.Path);
                    row.Unknown = row.Toggle = true;
                    row.Have = Have.Aside;
                    row.PartList.Add(new Part { Type = PartType.Patch, Letter = a.Letter, Size = a.Size });
                    row.Parts = @"ComfyLauncher\set-aside\Data\" + Path.GetFileName(a.Path);
                }
                foreach (var p in oddPatches)
                {
                    string l = p.Letter.ToString();
                    string name = patchLabels.Str(l) != "" ? patchLabels.Str(l) : patchNames.Str(l) != "" ? patchNames.Str(l) : "patch-" + l;
                    var row = Add("Mods", RowKind.Check, name, "patch-" + l + ", not from ComfyCraft");
                    row.Id = "patch:" + l;
                    row.Unknown = row.Toggle = true;
                    row.On = p.Enabled;
                    row.Have = p.Enabled ? Have.Current : Have.Off;
                    row.PartList.Add(new Part { Type = PartType.Patch, Letter = p.Letter, Size = p.Size });
                    row.Parts = @"Data\" + Path.GetFileName(p.Path);
                }
                foreach (var f in oddDlls)
                {
                    var row = Add("Mods", RowKind.Check, Path.GetFileNameWithoutExtension(f), "a line in dlls.txt, not from ComfyCraft");
                    row.Id = "dll:" + f;
                    row.Unknown = row.Toggle = true;
                    row.On = true;
                    row.Have = Have.Current;
                    row.PartList.Add(new Part { Type = PartType.Dll, Name = f });
                    row.Parts = f;
                }
            }

            // Needs and parents, now every row exists.
            Row ById(string id) => app.Rows.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            foreach (var kv in needIds)
                foreach (var id in kv.Value) { var need = ById(id); if (need != null) kv.Key.Needs.Add(need); }
            foreach (var kv in parentIds) kv.Key.Parent = ById(kv.Value);

            // ---- Addons: the ones a package owns, then the player's own,
            // then what this launcher set aside.
            var catalog = manifest.Obj("catalog");
            if (catalog.Str("url") != "")
            {
                app.CatalogUrl = Absolute(manifestUrl, catalog.Str("url"));
                Add("Addons", RowKind.Add, "Find an addon", "search addon list").Id = "find-addon";
            }
            Add("Addons", RowKind.Add, "Add an addon", "paste link to GitHub or to a .zip");
            // An addon a package owns shows when it is on the disk, or when its
            // package is on and will put it there. A name ComfyUI replaced
            // shows only when an old copy is still on the disk. One that went
            // aside with its switched-off mod shows here too, under the mod's
            // tick, and not as the player's own: ticking it alone would bring
            // it back without the DLL it needs.
            string aside = Path.Combine(home, @"set-aside\AddOns");
            var setAside = Directory.Exists(aside) ? new DirectoryInfo(aside).GetDirectories().Select(d => d.Name).ToList() : new List<string>();
            bool OwnedAside(string folder) => app.Rows.Any(r => r.PartList.Any(p => p.OffWithMod && p.Name.Equals(folder, StringComparison.OrdinalIgnoreCase)));
            var owned = claimedAddons.Where(c => client.AddOns.Any(a => a.Equals(c.Folder, StringComparison.OrdinalIgnoreCase))
                                                 || (c.Owner.On && !c.OwnerName.Contains("replaced"))
                                                 || (setAside.Contains(c.Folder, StringComparer.OrdinalIgnoreCase) && OwnedAside(c.Folder))).ToList();
            if (owned.Count > 0)
            {
                Heading("Addons", "Part of a mod");
                foreach (var c in owned.GroupBy(c => c.Folder, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
                {
                    var row = Add("Addons", RowKind.Part, c.Folder, "part of " + c.OwnerName);
                    row.PartOf = c.OwnerName;
                    row.Owner = c.Owner;
                }
            }
            var theirs = client.AddOns.Where(a => !claimedAddons.Any(c => c.Folder.Equals(a, StringComparison.OrdinalIgnoreCase))).ToList();
            setAside = setAside.Where(s => !OwnedAside(s)).ToList();
            if (theirs.Count > 0 || setAside.Count > 0)
            {
                Heading("Addons", "Your addons");
                foreach (var a in theirs)
                {
                    var row = Add("Addons", RowKind.Check, a);
                    row.Unknown = true; row.On = true; row.Have = Have.Current;
                    row.PartList.Add(new Part { Type = PartType.Addon, Name = a });
                    row.Parts = @"Interface\AddOns\" + a;
                }
                foreach (var a in setAside.Where(s => !theirs.Contains(s, StringComparer.OrdinalIgnoreCase)))
                {
                    var row = Add("Addons", RowKind.Check, a);
                    row.Unknown = true; row.On = false; row.Have = Have.Aside;
                    row.PartList.Add(new Part { Type = PartType.Addon, Name = a });
                    row.Parts = @"Interface\AddOns\" + a;
                }
            }

            // ---- Settings: the launcher's own, from the state file, then the
            // client's display and view, from its Config.wtf.
            Heading("Settings", "Launcher");
            var settings = state.Obj("settings");
            var cache = Add("Settings", RowKind.Setting, "Clear the cache", "clears WDB folder on startup");
            cache.Id = "clear-cache";
            cache.On = settings.Bool("clearCache", true);
            cache.Advice = "Deletes the WDB folder first, so item, creature and quest changes on the server show at once.";
            var auto = Add("Settings", RowKind.Setting, "Autostart", "launch game after 8 seconds automatically");
            auto.Id = "auto-start";
            auto.On = settings.Bool("autoStart", false);
            var music = Add("Settings", RowKind.Setting, "Music", "play tune while the launcher is open");
            music.Id = "music";
            music.On = settings.Bool("music", false);
            auto.Advice = "Any key stops the countdown.";
            Display.AddRows(app, client, t => Heading("Settings", t));

            App.DeriveWrites(app);
            return app;
        }

        // THE DXVK ROW, from packages.json's `dxvk`. Each build is known by the
        // hash of its file, on the disk or in set-aside\dxvk. A build with no
        // url is offered only while its file is here, since it cannot be
        // fetched again; a file no build knows is the player's own, and is
        // offered the same way. Unticked with a file set aside is Off, and
        // the choice is the build that was last on.
        static Row DxvkRow(Client client, Dictionary<string, object> spec, string home, string was)
        {
            string file = spec.Str("file");
            if (file == "") return null;
            var known = spec.Objs("builds").Select(b => new Build
            {
                Name = b.Str("name"), Note = b.Str("note"), Sha256 = b.Str("sha256"), Url = b.Str("url"), Size = b.Long("size"),
                ArchiveSha256 = b.Str("archive_sha256"), Extract = b.Str("extract"),
            }).ToList();
            var own = new List<Build>();
            Build Match(string sha)
            {
                var b = known.FirstOrDefault(k => k.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase))
                        ?? own.FirstOrDefault(k => k.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase));
                if (b != null) return b;
                own.Add(b = new Build { Name = own.Count == 0 ? "your own" : "your own " + (own.Count + 1), Sha256 = sha });
                return b;
            }

            string live = client.PathOf(file);
            Build onDisk = File.Exists(live) ? Match(Client.Sha256(live)) : null;
            string asideDir = Path.Combine(home, "set-aside", "dxvk");
            if (Directory.Exists(asideDir))
                foreach (var a in Directory.GetFiles(asideDir, file, SearchOption.AllDirectories))
                {
                    var b = Match(Client.Sha256(a));
                    if (b.Aside == null) b.Aside = a;
                }

            var builds = known.Where(b => b.Url != "" || b == onDisk || b.Aside != null).Concat(own).ToList();
            if (builds.Count == 0) return null;
            var row = new Row
            {
                Id = "dxvk", Tab = "Mods", Kind = RowKind.Check, Label = "DXVK", Note = spec.Str("note"), Advice = spec.Str("advice"),
                Toggle = true, StateKey = "dxvk", Builds = builds, BuildFile = file,
            };
            row.Choices = builds.Select(b => b.Name).ToArray();
            row.ChoiceVersion = row.Choices;
            row.ChoiceKb = builds.Select(b => b == onDisk || b.Aside != null ? 0 : (int)(b.Size / 1024)).ToArray();
            row.ChoiceParts = builds.Select(b => file
                + (b.Note != "" ? ", " + b.Note : "")
                + (b == onDisk ? "" : b.Aside != null ? ", set aside" : b.Url != "" ? ", from " + Path.GetFileName(b.Url) : "")).ToArray();

            if (onDisk != null)
            {
                row.Have = Have.Current; row.On = true;
                row.Choice = row.HaveChoice = builds.IndexOf(onDisk);
            }
            else
            {
                var back = builds.FirstOrDefault(b => b.Aside != null && b.Name == was) ?? builds.FirstOrDefault(b => b.Aside != null);
                row.Have = back != null ? Have.Off : Have.None;
                row.On = false;
                row.Choice = row.HaveChoice = back != null ? builds.IndexOf(back) : 0;
            }
            return row;
        }

        // A mod's files: the extract map when it has one (the DLL, an ini, an
        // addon folder), otherwise the download is the DLL itself.
        //
        // `also` is more downloads that come with the mod, each with its own
        // url, sha256 and extract map: an addon kept in a repo of its own.
        static List<Part> ModParts(Dictionary<string, object> mod)
        {
            string file = mod.Str("file");
            var parts = new List<Part>();
            var extract = mod.Obj("extract");
            if (extract == null || extract.Count == 0)
                parts.Add(new Part { Type = PartType.Dll, Name = file, Url = mod.Str("url"), Sha256 = mod.Str("sha256") });
            else Extracted(parts, file, mod.Str("url"), mod.Str("sha256"), extract);
            foreach (var also in mod.Objs("also"))
                Extracted(parts, file, also.Str("url"), also.Str("sha256"), also.Obj("extract"), also.Bool("off_with_mod"));
            return parts;
        }

        static void Extracted(List<Part> parts, string file, string url, string sha, Dictionary<string, object> extract, bool offWithMod = false)
        {
            if (extract == null) return;
            foreach (var kv in extract)
            {
                string to = (kv.Value as string ?? "").Replace('\\', '/');
                var addon = Regex.Match(to, @"^Interface/AddOns/([^/]+)/?$", RegexOptions.IgnoreCase);
                if (addon.Success) parts.Add(new Part { Type = PartType.Addon, Name = addon.Groups[1].Value, Url = url, Sha256 = sha, Extract = kv.Key, OffWithMod = offWithMod });
                else if (to.Equals(file, StringComparison.OrdinalIgnoreCase)) parts.Add(new Part { Type = PartType.Dll, Name = file, Url = url, Sha256 = sha, Extract = kv.Key });
                else parts.Add(new Part { Type = PartType.File, Name = to.Replace('/', '\\'), Url = url, Sha256 = sha, Extract = kv.Key });
            }
        }
    }
}
