using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ComfyLauncher
{
    // ONE FILE OPERATION: the words the dry run shows, and the code that does
    // exactly that. One list serves both, so what the dry run promised is
    // what a real run does, in the same order.
    sealed class Op
    {
        public string Text;
        public long Bytes;                   // a download's size, for the progress bar
        public Action<Action<long>> Run;     // reports bytes done
    }

    static class Operations
    {
        const string AsideDir = "set-aside";

        static string Rel(App app, string full) =>
            full.StartsWith(app.Client.Root + "\\", StringComparison.OrdinalIgnoreCase) ? full.Substring(app.Client.Root.Length + 1) : full;

        // A free name in a folder: "name", then "name (2)" and on. Setting
        // something aside never overwrites what was set aside before.
        static string Free(string dir, string name)
        {
            string path = Safe.Join(dir, Safe.Name(name));
            for (int i = 2; File.Exists(path) || Directory.Exists(path); i++)
                path = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
            return path;
        }

        public static List<Op> For(App app, Row row)
        {
            if (row.Custom != null) return row.Custom;
            var ops = new List<Op>();
            var c = app.Client;
            string action = row.Action;
            string aside = Path.Combine(app.Home, AsideDir);
            string downloads = Path.Combine(app.Home, "downloads");

            if (row.Kind == RowKind.Radio)
            {
                ops.Add(new Op { Text = "realmlist.wtf: set realmlist " + row.Address + ", set patchlist " + row.Patchlist,
                                 Run = _ => File.WriteAllText(c.PathOf("realmlist.wtf"), "set realmlist " + row.Address + "\r\nset patchlist " + row.Patchlist + "\r\n", Encoding.ASCII) });
                ops.Add(new Op { Text = @"WTF\Config.wtf: set realmList and patchList",
                                 Run = _ => SetConfig(c, new[] { ("realmList", row.Address), ("patchList", row.Patchlist) }) });
                ops.Add(new Op { Text = @"delete the cache in WDB\, which holds the last server's names", Run = _ => ClearWdb(c) });
                return ops;
            }

            if (row.Kind == RowKind.Config)
            {
                var sets = row.ChoiceSets[row.Choice].ToArray();
                if (sets.Length == 0)
                    ops.Add(new Op { Text = @"WTF\Config.wtf: leave " + row.Label.ToLowerInvariant() + " as it is", Run = _ => { } });
                else
                    ops.Add(new Op { Text = @"WTF\Config.wtf: " + string.Join(", ", sets.Select(v => "SET " + v.Key + " \"" + v.Value + "\"")),
                                     Run = _ => SetConfig(c, sets) });
                return ops;
            }

            // Parts that share one download (a mod's zip) fetch it once.
            var fetched = new Dictionary<string, string>();
            string Fetch(Part p, Action<long> progress)
            {
                if (fetched.TryGetValue(p.Url, out var had)) return had;
                string name = Path.GetFileName(new Uri(p.Url.StartsWith("http") ? p.Url : "file:///" + p.Url.Replace('\\', '/')).LocalPath);
                string file = Path.Combine(downloads, name);
                Net.Download(p.Url, 0, p.Size > 0 && p.Type != PartType.Patch ? p.Size : 0, file + ".part", p.Etag, progress);
                if (p.Sha256 != "" && !Client.Sha256(file + ".part").Equals(p.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file + ".part");
                    throw new IOException(name + " did not match its checksum, so nothing was changed");
                }
                if (File.Exists(file)) File.Delete(file);
                File.Move(file + ".part", file);
                fetched[p.Url] = file;
                return file;
            }

            if (row.Builds != null) return BuildOps(app, row, Fetch);

            foreach (var p in row.PartsNowList)
            {
                // Only a patch has a letter; an unset one is \0, which no path may hold.
                string patch = p.Type == PartType.Patch ? c.PathOf(@"Data\patch-" + p.Letter + ".mpq") : "";
                string addon = p.Type == PartType.Addon ? c.AddonPath(p.Name) : "";
                switch (action)
                {
                    case "install":
                    case "reinstall":
                    case "update":
                    case "change":
                        if (p.Type == PartType.Patch)
                        {
                            // A DIFFERENT FILE UNDER THE SAME LETTER GOES ASIDE
                            // FIRST, never under the new one: it may be the only
                            // copy of somebody's patch.
                            if (c.Patches.TryGetValue(p.Letter, out var old) && old.Size != p.Size)
                            {
                                string oldPath = old.Path;
                                ops.Add(new Op { Text = "move " + Rel(app, oldPath) + " to " + AsideDir + @"\Data",
                                    Run = _ => { Directory.CreateDirectory(Path.Combine(aside, "Data")); File.Move(oldPath, Free(Path.Combine(aside, "Data"), Path.GetFileName(oldPath))); } });
                            }
                            var part = p;
                            ops.Add(new Op
                            {
                                Text = part.ZipOffset >= 0
                                    ? "download " + View.Size(part.Size / 1024.0) + " from byte " + part.ZipOffset.ToString("N0") + " of the zip to " + Rel(app, patch)
                                    : "download " + View.Size(part.Size / 1024.0) + " from " + part.Url + " to " + Rel(app, patch),
                                Bytes = part.Size,
                                Run = progress =>
                                {
                                    string tmp = patch + ".part";
                                    Net.Download(part.Url, Math.Max(0, part.ZipOffset), part.Size, tmp, part.Etag, progress);
                                    if (part.Crc32 != "" && Net.Crc32(tmp) != part.Crc32.ToLowerInvariant())
                                    {
                                        File.Delete(tmp);
                                        throw new IOException("patch-" + part.Letter + " did not match its checksum, so nothing was changed");
                                    }
                                    if (part.Sha256 != "" && !Client.Sha256(tmp).Equals(part.Sha256, StringComparison.OrdinalIgnoreCase))
                                    {
                                        File.Delete(tmp);
                                        throw new IOException("patch-" + part.Letter + " did not match its checksum, so nothing was changed");
                                    }
                                    // The same file already there, live or off: replaced.
                                    // A file of another size under either name goes aside:
                                    // the reader keeps one file per letter, so a second
                                    // one was never on screen to be asked about.
                                    foreach (var there in new[] { patch, patch + ".off" })
                                    {
                                        if (!File.Exists(there)) continue;
                                        if (new FileInfo(there).Length == part.Size) { File.Delete(there); continue; }
                                        Directory.CreateDirectory(Path.Combine(aside, "Data"));
                                        File.Move(there, Free(Path.Combine(aside, "Data"), Path.GetFileName(there)));
                                    }
                                    File.Move(tmp, patch);
                                },
                            });
                        }
                        else if (p.Type == PartType.Addon)
                        {
                            var part = p;
                            ops.Add(new Op
                            {
                                Text = "download " + part.Url + (part.Extract != "" ? ", take " + part.Extract : "") + ", put it at " + Rel(app, addon),
                                Bytes = part.Size,
                                Run = progress => InstallAddon(app, Fetch(part, progress), part, addon),
                            });
                        }
                        else
                        {
                            var part = p;
                            string dest = c.PathOf(part.Name);
                            ops.Add(new Op
                            {
                                Text = "download " + part.Url + (part.Extract != "" ? ", take " + part.Extract : "") + ", write " + part.Name,
                                Bytes = part.Size,
                                Run = progress => InstallFile(Fetch(part, progress), part, dest),
                            });
                        }
                        if (p.Type == PartType.Dll && row.Toggle && !c.DllEnabled(p.Name))
                        {
                            string file = p.Name;
                            ops.Add(new Op { Text = "dlls.txt: add " + file, Run = _ => SetDlls(c, new[] { file }, new string[0]) });
                        }
                        break;

                    case "enable":
                        if (p.Type == PartType.Patch)
                            ops.Add(new Op { Text = "rename " + Rel(app, patch) + ".off to " + Rel(app, patch), Run = _ => File.Move(patch + ".off", patch) });
                        if (p.Type == PartType.Dll)
                        {
                            string file = p.Name;
                            ops.Add(new Op { Text = "dlls.txt: add " + file, Run = _ => SetDlls(c, new[] { file }, new string[0]) });
                        }
                        if (p.Type == PartType.Addon && p.OffWithMod && !Directory.Exists(addon))
                        {
                            string from = Safe.Join(Path.Combine(aside, "AddOns"), Safe.Name(p.Name));
                            ops.Add(new Op { Text = "move " + AsideDir + @"\AddOns\" + p.Name + " back to " + Rel(app, addon),
                                Run = _ => { if (Directory.Exists(from)) Directory.Move(from, addon); } });
                        }
                        break;

                    case "disable":
                        if (p.Type == PartType.Patch)
                            ops.Add(new Op { Text = "rename " + Rel(app, patch) + " to " + Rel(app, patch) + ".off", Run = _ => File.Move(patch, patch + ".off") });
                        if (p.Type == PartType.Dll)
                        {
                            string file = p.Name;
                            ops.Add(new Op { Text = "dlls.txt: take out " + file, Run = _ => SetDlls(c, new string[0], new[] { file }) });
                        }
                        // AN ADDON THAT COMPLAINS WITHOUT ITS DLL goes aside with
                        // it: SuperAPI shows a popup at every login otherwise.
                        if (p.Type == PartType.Addon && p.OffWithMod && Directory.Exists(addon))
                        {
                            var part = p;
                            ops.Add(new Op { Text = "move " + Rel(app, addon) + " to " + AsideDir + @"\AddOns",
                                Run = _ => { Directory.CreateDirectory(Path.Combine(aside, "AddOns")); Directory.Move(addon, Free(Path.Combine(aside, "AddOns"), part.Name)); } });
                        }
                        break;

                    case "set aside":
                        if (p.Type == PartType.Addon)
                            ops.Add(new Op { Text = "move " + Rel(app, addon) + " to " + AsideDir + @"\AddOns",
                                Run = _ => { Directory.CreateDirectory(Path.Combine(aside, "AddOns")); Directory.Move(addon, Free(Path.Combine(aside, "AddOns"), p.Name)); } });
                        break;

                    case "restore":
                        if (p.Type == PartType.Addon)
                        {
                            string from = Safe.Join(Path.Combine(aside, "AddOns"), Safe.Name(p.Name));
                            ops.Add(new Op { Text = "move " + AsideDir + @"\AddOns\" + p.Name + " back to " + Rel(app, addon), Run = _ => Directory.Move(from, addon) });
                        }
                        if (p.Type == PartType.Patch)
                        {
                            // Back under its letter, and whatever holds that
                            // letter now goes aside in its place.
                            var back = c.AsidePatches.FirstOrDefault(a => a.Letter == p.Letter && a.Size == p.Size);
                            if (back == null) break;
                            if (c.Patches.TryGetValue(p.Letter, out var now))
                            {
                                string nowPath = now.Path;
                                ops.Add(new Op { Text = "move " + Rel(app, nowPath) + " to " + AsideDir + @"\Data",
                                    Run = _ => { Directory.CreateDirectory(Path.Combine(aside, "Data")); File.Move(nowPath, Free(Path.Combine(aside, "Data"), Path.GetFileName(nowPath))); } });
                            }
                            string backPath = back.Path;
                            ops.Add(new Op { Text = "move " + AsideDir + @"\Data\" + Path.GetFileName(backPath) + " back to " + Rel(app, patch),
                                Run = _ => File.Move(backPath, patch) });
                        }
                        break;

                    case "remove":
                        if (p.Type == PartType.Addon) ops.Add(new Op { Text = "delete " + Rel(app, addon), Run = _ => { if (Directory.Exists(addon)) Directory.Delete(addon, true); } });
                        else if (p.Type == PartType.Patch) ops.Add(new Op { Text = "delete " + Rel(app, patch), Run = _ => { File.Delete(patch); File.Delete(patch + ".off"); } });
                        else { string f = c.PathOf(p.Name); ops.Add(new Op { Text = "delete " + p.Name, Run = _ => File.Delete(f) }); }
                        break;
                }
            }

            // BACK FROM A VARIANT, the other build's own files go: the 64 bit
            // Vanilla helpers brings TextureServer64.exe, and the standard
            // build has no use for it. Patches are not listed here, since their
            // letters already say what is replaced.
            if (action == "change" && row.ChoicePartList != null)
            {
                var keep = new HashSet<string>(row.PartsNowList.Where(p => p.Type != PartType.Patch).Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var other in row.ChoicePartList.Where(l => l != row.PartsNowList).SelectMany(l => l)
                                                         .Where(p => (p.Type == PartType.File || p.Type == PartType.Dll) && !keep.Contains(p.Name))
                                                         .Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string dead = c.PathOf(other);
                    if (File.Exists(dead)) ops.Add(new Op { Text = "delete " + other + ", which only the other build uses", Run = _ => File.Delete(dead) });
                }
            }

            // An addon that replaced others sets the old folders aside after
            // it is in place, never before: a failed download must leave the
            // old one working.
            if (action == "install" || action == "update" || action == "reinstall")
                foreach (var old in row.Supersedes)
                {
                    string dead = c.AddonPath(old);
                    if (!Directory.Exists(dead)) continue;
                    ops.Add(new Op { Text = "move " + Rel(app, dead) + " to " + AsideDir + @"\AddOns, now part of " + row.Label,
                        Run = _ => { Directory.CreateDirectory(Path.Combine(aside, "AddOns")); Directory.Move(dead, Free(Path.Combine(aside, "AddOns"), old)); } });
                }
            return ops;
        }

        // ---- THE DXVK ROW. The new build is fetched and checked FIRST, so a
        // failed download leaves the old file working. Then the file in place
        // goes to set-aside\dxvk\<build>, and the chosen one takes its name.
        // Nothing is deleted: Turtle's own build cannot be fetched again.
        static List<Op> BuildOps(App app, Row row, Func<Part, Action<long>, string> fetch)
        {
            var ops = new List<Op>();
            var c = app.Client;
            string action = row.Action;
            string live = c.PathOf(row.BuildFile);
            string asideDir = Path.Combine(app.Home, AsideDir, "dxvk");
            var want = row.Builds[row.Choice];
            bool bring = action == "install" || action == "change" || action == "enable";
            string fresh = Path.Combine(app.Home, "downloads", row.BuildFile + ".new");

            if (bring && want.Aside == null)
            {
                var part = new Part { Url = want.Url, Size = want.Size, Sha256 = want.ArchiveSha256 };
                ops.Add(new Op
                {
                    Text = "download " + want.Url + ", take " + want.Extract,
                    Bytes = want.Size,
                    Run = progress =>
                    {
                        ExtractTarGz(fetch(part, progress), want.Extract, fresh);
                        if (!Client.Sha256(fresh).Equals(want.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(fresh);
                            throw new IOException(want.Extract + " did not match its checksum, so nothing was changed");
                        }
                    },
                });
            }

            if ((action == "change" || action == "disable") && File.Exists(live))
            {
                string key = row.Builds[row.HaveChoice].Name.Replace("'", "").Replace(' ', '-');
                ops.Add(new Op { Text = "move " + row.BuildFile + " to " + AsideDir + @"\dxvk\" + key,
                    Run = _ =>
                    {
                        string dir = Free(asideDir, key);
                        Directory.CreateDirectory(dir);
                        File.Move(live, Path.Combine(dir, row.BuildFile));
                    } });
            }

            if (bring && want.Aside != null)
            {
                string from = want.Aside;
                ops.Add(new Op { Text = "move " + AsideDir + @"\dxvk\" + Path.GetFileName(Path.GetDirectoryName(from)) + @"\" + row.BuildFile + " back to " + row.BuildFile,
                    Run = _ =>
                    {
                        File.Move(from, live);
                        string dir = Path.GetDirectoryName(from);
                        if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
                    } });
            }
            else if (bring)
                ops.Add(new Op { Text = "write " + row.BuildFile + ", DXVK " + want.Name, Run = _ => File.Move(fresh, live) });
            return ops;
        }

        // ONE ENTRY OF A .tar.gz, which .NET Framework cannot open by itself.
        // A header is 512 bytes: the name at 0, the size in octal at 124, the
        // type at 156, and a ustar prefix at 345. A GNU 'L' entry or a pax
        // 'x' entry carries the next entry's long name.
        static void ExtractTarGz(string archive, string entry, string dest)
        {
            using (var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
            {
                var head = new byte[512];
                string longName = null;
                while (ReadFull(gz, head, 512) == 512 && head.Any(b => b != 0))
                {
                    string Ascii(int at, int len) { int n = Array.IndexOf(head, (byte)0, at, len); return Encoding.ASCII.GetString(head, at, (n < 0 ? at + len : n) - at); }
                    string name = Ascii(0, 100);
                    if (Ascii(257, 5) == "ustar" && Ascii(345, 155) != "") name = Ascii(345, 155) + "/" + name;
                    long size = Convert.ToInt64(Ascii(124, 12).Trim() == "" ? "0" : Ascii(124, 12).Trim(), 8);
                    char type = (char)head[156];
                    if (longName != null) { name = longName; longName = null; }
                    if (name.StartsWith("./")) name = name.Substring(2);

                    long padded = (size + 511) / 512 * 512;
                    if (type == 'L' || type == 'x')
                    {
                        var data = new byte[padded];
                        ReadFull(gz, data, (int)padded);
                        string text = Encoding.UTF8.GetString(data, 0, (int)size);
                        if (type == 'L') longName = text.TrimEnd('\0');
                        else
                        {
                            var path = System.Text.RegularExpressions.Regex.Match(text, @"\d+ path=([^\n]*)\n");
                            if (path.Success) longName = path.Groups[1].Value;
                        }
                        continue;
                    }
                    if ((type == '0' || type == '\0') && name.Equals(entry, StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        using (var o = File.Create(dest))
                        {
                            var buf = new byte[81920];
                            long left = size;
                            while (left > 0)
                            {
                                int n = gz.Read(buf, 0, (int)Math.Min(buf.Length, left));
                                if (n <= 0) throw new IOException(Path.GetFileName(archive) + " ends early");
                                o.Write(buf, 0, n);
                                left -= n;
                            }
                        }
                        return;
                    }
                    var skip = new byte[81920];
                    for (long left = padded; left > 0; )
                    {
                        int n = ReadFull(gz, skip, (int)Math.Min(skip.Length, left));
                        if (n == 0) break;
                        left -= n;
                    }
                }
            }
            throw new IOException(entry + " is not in " + Path.GetFileName(archive));
        }

        static int ReadFull(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) break;
                got += n;
            }
            return got;
        }

        // ---- AN ADDON FROM A LINK, ported from Read-NewAddon. A GitHub repo
        // link tries the master zip, then main; anything else is fetched as
        // it is. Every folder in the zip holding a .toc is an addon and goes
        // in, so a repo that ships several (Bagnon) gets all of them.
        // A GitLab or Codeberg repo link works the same way, through their own
        // archive links. `label` names the row, for a catalog entry whose link
        // ends in a tag zip rather than in the addon's name.
        public static Row AddFromLink(App app, string text, string label = null)
        {
            string u = text.Trim().Trim('"');
            if (u.StartsWith("git+")) u = u.Substring(4);
            if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) u = "https://" + u;
            u = u.TrimEnd('/');
            var urls = new List<string>();
            var repoLink = System.Text.RegularExpressions.Regex.Match(u, @"^https?://(github\.com|gitlab\.com|codeberg\.org)/([^/]+)/([^/]+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && repoLink.Success)
            {
                string host = repoLink.Groups[1].Value.ToLowerInvariant(), owner = repoLink.Groups[2].Value;
                string repo = repoLink.Groups[3].Value.EndsWith(".git") ? repoLink.Groups[3].Value.Substring(0, repoLink.Groups[3].Value.Length - 4) : repoLink.Groups[3].Value;
                foreach (var branch in new[] { "master", "main" })
                    urls.Add(host == "github.com" ? "https://github.com/" + owner + "/" + repo + "/archive/refs/heads/" + branch + ".zip"
                           : host == "gitlab.com" ? "https://gitlab.com/" + owner + "/" + repo + "/-/archive/" + branch + "/" + repo + "-" + branch + ".zip"
                           : "https://codeberg.org/" + owner + "/" + repo + "/archive/" + branch + ".zip");
            }
            else urls.Add(u);

            string name = label ?? u.Split('/').Last();
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            var row = new Row { Id = "add:" + u, Tab = "Addons", Kind = RowKind.Check, Label = name, On = true, Have = Have.None, Unknown = true };
            row.Custom = new List<Op>
            {
                new Op
                {
                    Text = "download " + urls[0] + (urls.Count > 1 ? " (or main.zip)" : "") + ", put every addon in it in Interface\\AddOns",
                    Run = progress => InstallFromZip(app, urls, progress),
                },
            };
            return row;
        }

        static void InstallFromZip(App app, List<string> urls, Action<long> progress)
        {
            string downloads = Path.Combine(app.Home, "downloads"), zip = Path.Combine(downloads, "added-addon.zip");
            Exception last = null;
            bool got = false;
            foreach (var url in urls)
            {
                try
                {
                    if (File.Exists(zip + ".part")) File.Delete(zip + ".part");
                    Net.Download(url, 0, 0, zip + ".part", "", progress);
                    got = true;
                    break;
                }
                catch (Exception e) { last = e; }
            }
            if (!got) throw new IOException("could not download that; check the link" + (last != null ? " (" + last.Message + ")" : ""));
            if (File.Exists(zip)) File.Delete(zip);
            File.Move(zip + ".part", zip);

            string work = Path.Combine(app.Home, "work", "added");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            // Entry by entry through Safe, not ZipFile.ExtractToDirectory:
            // this is a zip from any link a player pastes.
            ZipArchive archive;
            try { archive = ZipFile.OpenRead(zip); }
            catch { throw new IOException("that downloaded, but it is not a zip this can open"); }
            using (archive)
                foreach (var e in archive.Entries)
                {
                    string name = e.FullName.Replace('\\', '/');
                    if (name == "" || name.EndsWith("/")) continue;
                    string to = Safe.Join(work, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    e.ExtractToFile(to, true);
                }

            var found = FindAddonFolders(work);
            if (found.Count == 0) throw new IOException("no addon in there: nothing with a .toc file");
            foreach (var (path, name) in found)
            {
                string dest = app.Client.AddonPath(name);
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                try { Directory.Move(path, dest); } catch (IOException) { CopyTree(path, dest); }
            }
        }

        // The folders holding a .toc, and what to name each: the .toc whose
        // name is the folder's, less a -master or -main a GitHub zip adds,
        // and never one for another game version when a plain one is there.
        static List<(string Path, string Name)> FindAddonFolders(string root)
        {
            var found = new List<(string, string)>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                var tocs = Directory.GetFiles(dir, "*.toc");
                if (tocs.Length > 0)
                {
                    string leaf = System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(dir), "-(master|main|classic|vanilla)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var plain = tocs.Where(t => !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(t), "-(tbc|bcc|wotlk|wrath|cata|mop|sod|era|classic)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
                    var pool = plain.Count > 0 ? plain : tocs.ToList();
                    string pick = pool.FirstOrDefault(t => Path.GetFileNameWithoutExtension(t).Equals(leaf, StringComparison.OrdinalIgnoreCase))
                                  ?? pool.OrderBy(t => Path.GetFileName(t).Length).First();
                    // A .toc named ".toc" or "...toc" gives "" or "..", which
                    // would name Interface\AddOns or Interface itself. Such a
                    // folder is no addon, and is left behind.
                    string addonName = Path.GetFileNameWithoutExtension(pick);
                    if (Safe.IsName(addonName)) found.Add((dir, addonName));
                    continue;
                }
                foreach (var sub in Directory.GetDirectories(dir)) stack.Push(sub);
            }
            return found;
        }

        // What Start the game does after the work, as the last lines.
        public static List<Op> Start(App app)
        {
            var ops = new List<Op>();
            if (app.Setting("clear-cache")) ops.Add(new Op { Text = @"delete the cache in WDB\", Run = _ => ClearWdb(app.Client) });
            bool vf = File.Exists(app.Client.PathOf("VanillaFixes.exe"));
            ops.Add(new Op { Text = "start " + (vf ? "VanillaFixes.exe, which loads the mods in dlls.txt" : "WoW.exe"), Run = _ => Game.Start(app) });
            return ops;
        }

        // ---- The writes, ported from ComfyLauncher.ps1.

        // An addon from a zip, mirrored into place: unpacked beside the
        // client, then the old folder goes and the new one moves in. The zip
        // may hold the folder itself, or its files at the top.
        static void InstallAddon(App app, string zip, Part part, string dest)
        {
            string work = Safe.Join(Path.Combine(app.Home, "work"), Safe.Name(part.Name));
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            using (var z = ZipFile.OpenRead(zip))
            {
                string prefix = part.Extract != "" ? part.Extract.Replace('\\', '/') : "";
                foreach (var e in z.Entries)
                {
                    string name = e.FullName.Replace('\\', '/');
                    if (prefix != "") { if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue; name = name.Substring(prefix.Length); }
                    if (name == "" || name.EndsWith("/")) continue;
                    // ZIP SLIP: an entry named "a/../../VanillaFixes.exe" or
                    // "C:/x" would write outside the folder. Safe refuses it,
                    // and the whole install fails rather than half happening.
                    string to = Safe.Join(work, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    e.ExtractToFile(to, true);
                }
            }
            string inner = Path.Combine(work, part.Name);
            string source = Directory.Exists(inner) && Directory.GetFiles(work).Length == 0 ? inner : work;
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            try { Directory.Move(source, dest); }
            catch (IOException) { CopyTree(source, dest); }
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }

        // A single file: the download itself, or one entry of a zip.
        static void InstallFile(string download, Part part, string dest)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (part.Extract == "") { File.Copy(download, dest, true); return; }
            using (var z = ZipFile.OpenRead(download))
            {
                var e = z.Entries.FirstOrDefault(x => x.FullName.Replace('\\', '/').Equals(part.Extract, StringComparison.OrdinalIgnoreCase));
                if (e == null) throw new IOException(part.Extract + " is not in " + Path.GetFileName(download));
                e.ExtractToFile(dest, true);
            }
        }

        static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
        }

        // dlls.txt as a diff: every line not being turned off is kept where
        // it was, blank lines and all, and a new line goes on the end. The
        // order is the injection order and it is the player's, not ours.
        public static void SetDlls(Client c, IEnumerable<string> enable, IEnumerable<string> disable)
        {
            string path = c.PathOf("dlls.txt");
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            var output = lines.Where(l => !disable.Any(d => l.Trim().Equals(d, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var e in enable)
                if (!output.Any(l => l.Trim().Equals(e, StringComparison.OrdinalIgnoreCase))) output.Add(e);
            File.WriteAllText(path, string.Join("\r\n", output) + "\r\n", Encoding.ASCII);
            c.EnabledDlls = output.Select(l => l.Trim()).Where(t => t != "" && !t.StartsWith("#") && !t.StartsWith(";")).ToList();
        }

        // Config.wtf's SET lines: replaced where they are, added where they
        // are not, and every other line kept, because the file is the
        // client's own settings.
        static void SetConfig(Client c, (string Key, string Value)[] values)
        {
            string cfg = c.PathOf(@"WTF\Config.wtf");
            if (!File.Exists(cfg)) return;
            var output = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(cfg))
            {
                var hit = values.FirstOrDefault(v => System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*SET\s+" + System.Text.RegularExpressions.Regex.Escape(v.Key) + @"\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                if (hit.Key == null) output.Add(line);
                else { output.Add("SET " + hit.Key + " \"" + hit.Value + "\""); seen.Add(hit.Key); }
            }
            foreach (var v in values) if (!seen.Contains(v.Key)) output.Add("SET " + v.Key + " \"" + v.Value + "\"");
            File.WriteAllText(cfg, string.Join("\r\n", output) + "\r\n", Encoding.ASCII);
        }

        public static void ClearWdb(Client c)
        {
            string dir = c.PathOf("WDB");
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.wdb", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
        }
    }
}
