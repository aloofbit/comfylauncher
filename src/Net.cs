using System;
using System.IO;
using System.Net;

namespace ComfyLauncher
{
    // DOWNLOADS THAT RESUME AND PROVE THEMSELVES. A file is written to
    // <dest>.part, continued from where it stopped with a Range request, and
    // checked before the caller renames it into place, so a dropped
    // connection at 1.9 GB of 2 GB costs 0.1 GB and a failed one never
    // leaves a half file under a real name.
    static class Net
    {
        const int Chunk = 256 * 1024;

        // url: http(s), or a local path (a manifest in a folder, for testing).
        // offset and size: a byte range of a bigger file, such as one stored
        //   patch inside the Faithful Upscale zip; offset 0 and size 0 for a
        //   whole file of unknown size.
        // etag: when given, the server's ETag must match, or the file changed
        //   upstream since its size was pinned and this refuses it.
        // total: for size 0, told the file's size once the host gives it, so
        //   a pasted link's bar can say "of 39 MB". A GitHub archive is sent
        //   with no length, and total is never called.
        public static void Download(string url, long offset, long size, string part, string etag, Action<long> progress, Action<long> total = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(part));
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (size > 0 && have > size) { File.Delete(part); have = 0; }
            if (size > 0 && have == size) { progress(have); return; }

            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                CopyLocal(url, offset, size, part, have, progress, total);
                return;
            }

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "ComfyLauncher";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 120000;
            req.AllowAutoRedirect = true;
            bool ranged = offset > 0 || have > 0 || (size > 0 && offset > 0);
            if (ranged)
            {
                if (size > 0) req.AddRange(offset + have, offset + size - 1);
                else req.AddRange(offset + have);
            }

            using (var res = (HttpWebResponse)req.GetResponse())
            {
                // Google Drive answers a refused download with a page, not an
                // error: a quota warning, or the large file notice.
                if ((res.ContentType ?? "").StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("the host sent a web page instead of the file; Drive may have hit its download limit, try later");
                if (etag != "" && res.Headers[HttpResponseHeader.ETag] is string got && got.Trim('"', ' ') != etag.Trim('"', ' '))
                    throw new IOException("the file changed on its host since this launcher's list was made");

                bool append = res.StatusCode == HttpStatusCode.PartialContent;
                if (ranged && !append)
                {
                    // Asked for a range and got the whole thing: fine for a
                    // whole file (start again), fatal for a slice of a zip.
                    if (offset > 0) throw new IOException("the host does not answer range requests");
                    have = 0;
                }
                if (size == 0 && total != null && res.ContentLength > 0) total(have + res.ContentLength);

                using (var input = res.GetResponseStream())
                using (var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[Chunk];
                    long done = have;
                    progress(done);
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (size > 0 && done + read > size) read = (int)(size - done);
                        output.Write(buffer, 0, read);
                        done += read;
                        progress(done);
                        if (size > 0 && done >= size) break;
                    }
                }
            }

            long got2 = new FileInfo(part).Length;
            if (size > 0 && got2 != size) throw new IOException("the download stopped at " + got2.ToString("N0") + " of " + size.ToString("N0") + " bytes; run it again to continue");
        }

        static void CopyLocal(string path, long offset, long size, string part, long have, Action<long> progress, Action<long> learned)
        {
            using (var input = File.OpenRead(path))
            using (var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create))
            {
                long total = size > 0 ? size : input.Length;
                if (size == 0 && learned != null) learned(total - offset);
                input.Seek(offset + have, SeekOrigin.Begin);
                var buffer = new byte[Chunk];
                long done = have;
                while (done < total)
                {
                    int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, total - done));
                    if (read <= 0) break;
                    output.Write(buffer, 0, read);
                    done += read;
                    progress(done);
                }
            }
        }

        // The zip format's CRC-32, for a slice of the Faithful zip.
        static uint[] table;
        public static string Crc32(string file)
        {
            if (table == null)
            {
                table = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[i] = c;
                }
            }
            uint crc = 0xFFFFFFFFu;
            var buffer = new byte[Chunk];
            using (var s = File.OpenRead(file))
            {
                int read;
                while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
                    for (int i = 0; i < read; i++) crc = table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
            }
            return (crc ^ 0xFFFFFFFFu).ToString("x8");
        }
    }
}
