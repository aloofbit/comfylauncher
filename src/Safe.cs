using System;
using System.IO;

namespace ComfyLauncher
{
    // EVERY PATH THAT COMES FROM OUTSIDE goes through here: a name or a path
    // from the manifest, from lists\, or from inside a downloaded zip. Any of
    // them could be "..\..\something" or "C:\something", and the launcher
    // deletes, moves and overwrites what they name. A path that would land
    // outside its folder is refused with an IOException, which the work shows
    // as a failed step, rather than followed.
    static class Safe
    {
        // root\relative, made absolute, and refused unless it is strictly
        // inside root: never root itself, never beside or above it.
        public static string Join(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) throw new IOException("refused an empty path inside " + root);
            string rel = relative.Replace('/', '\\');
            // Rooted ("C:\x", "\x", "\\server\x") or a drive or stream colon.
            if (Path.IsPathRooted(rel) || rel.IndexOf(':') >= 0) throw new IOException("refused a path outside " + root + ": " + relative);
            string top = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            string full = Path.GetFullPath(Path.Combine(top, rel));
            if (!full.StartsWith(top, StringComparison.OrdinalIgnoreCase) || full.Length == top.Length)
                throw new IOException("refused a path outside " + root + ": " + relative);
            return full;
        }

        // One folder or file name, such as an addon's: not empty, not "." or
        // "..", no separators and nothing Windows refuses in a name. A .toc
        // file named ".toc" or "...toc" once made an addon named "" or "..",
        // which would have replaced Interface\AddOns or Interface itself.
        public static bool IsName(string name) =>
            !string.IsNullOrWhiteSpace(name) && name.Trim('.', ' ') != ""
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

        // Whether Join would take this relative path, for the loader, which
        // leaves a bad entry out rather than stopping on it.
        public static bool IsRelative(string relative)
        {
            try { Join(@"C:\root", relative); return true; }
            catch (IOException) { return false; }
        }

        public static string Name(string name)
        {
            if (!IsName(name)) throw new IOException("refused a name that is not a plain folder or file name: \"" + name + "\"");
            return name;
        }
    }
}
