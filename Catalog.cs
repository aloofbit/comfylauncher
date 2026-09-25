using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ComfyLauncher
{
    // ONE ADDON THE SEARCH CAN FIND: an entry of dist\addons.json, which
    // Build-Manifest.ps1 trims from the owner's launcher\addons.json.
    sealed class CatalogEntry
    {
        public string Name = "";
        public string By;                   // the owner, when two entries share a name
        public string Repo = "";
        public string Zip;                  // a pinned release, instead of the main branch
        public string Category = "";
        public string Note = "";
        public string Folder = "";
        public List<string> Needs = new List<string>();   // mod row ids
        public bool Recommended;
        public bool Installable = true;
        public int Downloads;

        public string Label => By == null ? Name : Name + ", " + By;
        public string Link => Zip ?? Repo;
    }

    static class Catalog
    {
        // THE CATALOG, fetched when the search first opens and not before: it
        // is 180 KB, and most starts never open it. A copy is kept beside the
        // state file for a run that cannot reach the server.
        public static List<CatalogEntry> Load(App app, out string error)
        {
            error = null;
            string cache = Path.Combine(app.Home, "addons.json");
            string text = null;
            try
            {
                text = Loader.Fetch(app.CatalogUrl);
                if (!app.DryRun) { Directory.CreateDirectory(app.Home); File.WriteAllText(cache, text); }
            }
            catch (Exception e)
            {
                if (File.Exists(cache)) text = File.ReadAllText(cache);
                else { error = "could not fetch the addon list: " + e.Message; return null; }
            }
            var list = Json.ParseValue(text) as object[];
            if (list == null) { error = "the addon list could not be read"; return null; }
            return list.OfType<Dictionary<string, object>>().Select(e => new CatalogEntry
            {
                Name = e.Str("name"), By = e.Str("by") == "" ? null : e.Str("by"), Repo = e.Str("repo"),
                Zip = e.Str("zip") == "" ? null : e.Str("zip"), Category = e.Str("category"), Note = e.Str("note"),
                Folder = e.Str("folder"), Needs = e.Strs("needs"), Recommended = e.Bool("recommended"),
                Installable = e.Bool("installable", true), Downloads = (int)e.Long("downloads"),
            }).ToList();
        }

        // EVERY WORD TYPED must be in the name, the owner, the category or the
        // note. A name that starts with what was typed comes first, then one
        // that holds it, then the rest; within each, the recommended ones, then
        // the most downloaded. Nothing typed lists them all in that order.
        public static List<CatalogEntry> Search(List<CatalogEntry> all, string query)
        {
            string q = query.Trim().ToLowerInvariant();
            var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int Score(CatalogEntry e)
            {
                string name = e.Name.ToLowerInvariant();
                if (q == "" || name.StartsWith(q)) return 0;
                if (name.Contains(q)) return 1;
                return words.All(w => name.Contains(w)) ? 2 : 3;
            }
            return all.Where(e =>
                {
                    string hay = (e.Name + " " + e.By + " " + e.Category + " " + e.Note).ToLowerInvariant();
                    return words.All(w => hay.Contains(w));
                })
                .OrderBy(Score).ThenBy(e => e.Recommended ? 0 : 1).ThenByDescending(e => e.Downloads).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
