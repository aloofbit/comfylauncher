using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ComfyLauncher
{
    // The only code that writes to the console.
    //
    // THE ALTERNATE SCREEN BUFFER is what removes the PowerShell launcher's
    // worst class of bug. That one drew into the normal buffer, below whatever
    // was already there, so it had to reserve rows, remember an anchor, and
    // repair the anchor when the buffer scrolled. The alternate buffer is the
    // size of the window and does not scroll: row 0 is the top of the window,
    // always. Leaving it puts back the console as it was before the launcher
    // started, which the old one could not do either.
    //
    // Needs virtual terminal processing: Windows 10 1809 and later, and every
    // Windows Terminal.
    static class Terminal
    {
        const string Esc = "\x1b[";

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
        [DllImport("kernel32.dll")]
        static extern uint GetConsoleOutputCP();
        [DllImport("kernel32.dll")]
        static extern bool SetConsoleOutputCP(uint codePage);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")]
        static extern bool CloseClipboard();
        [DllImport("user32.dll")]
        static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll")]
        static extern bool GlobalUnlock(IntPtr hMem);

        // THE CLIPBOARD'S TEXT, or "". Some consoles paste Ctrl+V as typed
        // characters, and others hand it over as the key itself, which the
        // prompt then has to answer by reading the clipboard. A few tries,
        // since another program may hold the clipboard open for a moment.
        public static string ClipboardText()
        {
            const uint UnicodeText = 13;
            for (int i = 0; i < 5; i++)
            {
                if (!OpenClipboard(IntPtr.Zero)) { System.Threading.Thread.Sleep(20); continue; }
                try
                {
                    IntPtr data = GetClipboardData(UnicodeText);
                    if (data == IntPtr.Zero) return "";
                    IntPtr text = GlobalLock(data);
                    if (text == IntPtr.Zero) return "";
                    try { return Marshal.PtrToStringUni(text) ?? ""; }
                    finally { GlobalUnlock(data); }
                }
                finally { CloseClipboard(); }
            }
            return "";
        }

        const int StdOutput = -11;
        const uint EnableVirtualTerminalProcessing = 0x0004;

        static Stream stdout;
        static uint oldMode;
        static uint oldCodePage;
        static string oldTitle;
        static bool modeChanged;
        static Screen last;

        public static bool Enter()
        {
            IntPtr handle = GetStdHandle(StdOutput);
            if (!GetConsoleMode(handle, out oldMode)) return false;
            if (!SetConsoleMode(handle, oldMode | EnableVirtualTerminalProcessing)) return false;
            modeChanged = true;

            // Frames go out as UTF-8 bytes. A console on code page 437 draws
            // the heart after the banner as three wrong characters.
            oldCodePage = GetConsoleOutputCP();
            SetConsoleOutputCP(65001);

            // The title bar otherwise shows the exe's full path. Put back on
            // exit, for a launcher started from somebody's own cmd window.
            try { oldTitle = Console.Title; } catch { oldTitle = null; }
            Console.Title = "ComfyCraft";

            stdout = Console.OpenStandardOutput();
            Console.TreatControlCAsInput = true;
            // Alternate buffer, hide the cursor, no auto wrap (a character in
            // the last column must never scroll the window), clear.
            Write(Esc + "?1049h" + Esc + "?25l" + Esc + "?7l" + Esc + "2J");
            last = null;
            return true;
        }

        public static void Leave()
        {
            if (stdout == null) return;
            Write(Esc + "0m" + Esc + "?7h" + Esc + "?25h" + Esc + "?1049l");
            if (modeChanged)
            {
                SetConsoleMode(GetStdHandle(StdOutput), oldMode);
                SetConsoleOutputCP(oldCodePage);
            }
            if (oldTitle != null) Console.Title = oldTitle;
            stdout = null;
        }

        public static int Width
        {
            get { try { return Console.WindowWidth; } catch { return 100; } }
        }

        public static int Height
        {
            get { try { return Console.WindowHeight; } catch { return 30; } }
        }

        // Writes the rows that differ from the last frame, in one write.
        // A new window size repaints everything.
        public static void Present(Screen frame)
        {
            bool full = last == null || last.Width != frame.Width || last.Height != frame.Height;
            var sb = new StringBuilder();
            if (full) sb.Append(Esc + "0m" + Esc + "2J");

            for (int y = 0; y < frame.Height; y++)
            {
                if (!full && frame.RowEquals(last, y)) continue;
                sb.Append(Esc).Append(y + 1).Append(";1H");
                Ink? current = null;
                for (int x = 0; x < frame.Width; x++)
                {
                    Ink ink = frame.InkAt(x, y);
                    if (ink != current)
                    {
                        sb.Append(Esc).Append(Sgr(ink)).Append('m');
                        current = ink;
                    }
                    sb.Append(frame.CharAt(x, y));
                }
            }
            sb.Append(Esc + "0m");
            Write(sb.ToString());
            last = frame;
        }

        static string Sgr(Ink ink)
        {
            switch (ink)
            {
                case Ink.DarkGray: return "90";
                case Ink.White:    return "97";
                case Ink.Cyan:     return "96";
                case Ink.Yellow:   return "93";
                case Ink.Green:    return "92";
                case Ink.Red:      return "91";
                default:           return "37";
            }
        }

        static void Write(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        }
    }
}
