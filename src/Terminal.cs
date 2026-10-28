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
    //
    // UNDER WINE (Proton, Lutris, Bottles) THE CLASSIC CONSOLE CALLS DRAW
    // INSTEAD. Wine's conhost accepts the virtual terminal flag and then does
    // not parse the output: every escape sequence shows as text. It does
    // support a second screen buffer and WriteConsoleOutputW, which give the
    // same alternate buffer and the same frames. --classic-console forces
    // this path on Windows, to test it there.
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateConsoleScreenBuffer(uint access, uint share, IntPtr security, uint flags, IntPtr reserved);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetConsoleActiveScreenBuffer(IntPtr handle);
        [DllImport("kernel32.dll")]
        static extern bool SetConsoleCursorInfo(IntPtr handle, ref CursorInfo info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool WriteConsoleOutputW(IntPtr handle, CharInfo[] buffer, Coord size, Coord from, ref SmallRect region);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)] struct Coord { public short X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SmallRect { public short Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct CursorInfo { public uint Size; public bool Visible; }
        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)] struct CharInfo { [FieldOffset(0)] public char Char; [FieldOffset(2)] public short Attributes; }

        // Wine's ntdll exports wine_get_version; Windows' does not.
        public static readonly bool UnderWine = GetProcAddress(GetModuleHandle("ntdll.dll"), "wine_get_version") != IntPtr.Zero;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr WineGetVersion();

        // "9.0", or "?": for the client report.
        public static string WineVersion()
        {
            try
            {
                IntPtr f = GetProcAddress(GetModuleHandle("ntdll.dll"), "wine_get_version");
                if (f == IntPtr.Zero) return "?";
                return Marshal.PtrToStringAnsi(Marshal.GetDelegateForFunctionPointer<WineGetVersion>(f)()) ?? "?";
            }
            catch { return "?"; }
        }

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll")]
        static extern bool FreeConsole();
        [DllImport("kernel32.dll")]
        static extern bool AllocConsole();

        // A CONSOLE WITH NO WINDOW, UNDER WINE. A Mac launcher that runs
        // `wine ComfyLauncher.exe` with its output thrown away (WoWSilicon's
        // launcher slot) gives the exe a console that Wine never shows, so
        // the launcher ran and nobody saw it. It opens a window of its own,
        // at the launcher's size. Before anything reads Console, which keeps
        // the handles it first finds.
        public static void EnsureWindow()
        {
            if (!UnderWine || GetConsoleWindow() != IntPtr.Zero) return;
            FreeConsole();
            if (!AllocConsole()) return;
            try { Console.SetBufferSize(100, 34); Console.SetWindowSize(100, 34); } catch { }
        }

        // Set by --classic-console, before Enter.
        public static bool ForceClassic;

        // The second screen buffer, when the classic calls draw. Zero otherwise.
        static IntPtr classic;

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
        [DllImport("user32.dll")]
        static extern bool EmptyClipboard();
        [DllImport("user32.dll")]
        static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")]
        static extern bool DestroyWindow(IntPtr hWnd);

        // Text onto the clipboard, with the same tries as ClipboardText. Once
        // SetClipboardData takes the memory, Windows owns it.
        //
        // A WINDOW OF OUR OWN OPENS THE CLIPBOARD. With none, EmptyClipboard
        // leaves no owner, and MSDN says SetClipboardData then fails. A
        // message-only window (parent HWND_MESSAGE) is never drawn.
        public static bool SetClipboardText(string text)
        {
            const uint UnicodeText = 13, Moveable = 0x0002;
            IntPtr owner = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            try { return SetClipboardText(text, owner, UnicodeText, Moveable); }
            finally { if (owner != IntPtr.Zero) DestroyWindow(owner); }
        }

        static bool SetClipboardText(string text, IntPtr owner, uint UnicodeText, uint Moveable)
        {
            for (int i = 0; i < 5; i++)
            {
                if (!OpenClipboard(owner)) { System.Threading.Thread.Sleep(20); continue; }
                try
                {
                    EmptyClipboard();
                    var bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
                    IntPtr mem = GlobalAlloc(Moveable, (UIntPtr)bytes.Length);
                    if (mem == IntPtr.Zero) return false;
                    IntPtr p = GlobalLock(mem);
                    if (p == IntPtr.Zero) { GlobalFree(mem); return false; }
                    Marshal.Copy(bytes, 0, p, bytes.Length);
                    GlobalUnlock(mem);
                    if (SetClipboardData(UnicodeText, mem) != IntPtr.Zero) return true;
                    GlobalFree(mem);
                    return false;
                }
                finally { CloseClipboard(); }
            }
            return false;
        }

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
            if (UnderWine || ForceClassic) return EnterClassic();
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

        // The classic path: a screen buffer of its own, made active, with the
        // cursor hidden. Leaving makes the original buffer active again.
        static bool EnterClassic()
        {
            const uint GenericReadWrite = 0x80000000 | 0x40000000, ShareReadWrite = 1 | 2, TextModeBuffer = 1;
            classic = CreateConsoleScreenBuffer(GenericReadWrite, ShareReadWrite, IntPtr.Zero, TextModeBuffer, IntPtr.Zero);
            if (classic == IntPtr.Zero || classic == new IntPtr(-1)) { classic = IntPtr.Zero; return false; }
            if (!SetConsoleActiveScreenBuffer(classic)) { CloseHandle(classic); classic = IntPtr.Zero; return false; }
            var cursor = new CursorInfo { Size = 25, Visible = false };
            SetConsoleCursorInfo(classic, ref cursor);

            try { oldTitle = Console.Title; } catch { oldTitle = null; }
            Console.Title = "ComfyCraft";
            Console.TreatControlCAsInput = true;
            last = null;
            return true;
        }

        public static void Leave()
        {
            if (classic != IntPtr.Zero)
            {
                SetConsoleActiveScreenBuffer(GetStdHandle(StdOutput));
                CloseHandle(classic);
                classic = IntPtr.Zero;
                if (oldTitle != null) Console.Title = oldTitle;
                return;
            }
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
            if (classic != IntPtr.Zero) { PresentClassic(frame); return; }
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

        // The same rows, as character cells with console attributes.
        static void PresentClassic(Screen frame)
        {
            bool full = last == null || last.Width != frame.Width || last.Height != frame.Height;
            var cells = new CharInfo[frame.Width];
            for (int y = 0; y < frame.Height; y++)
            {
                if (!full && frame.RowEquals(last, y)) continue;
                for (int x = 0; x < frame.Width; x++)
                    cells[x] = new CharInfo { Char = frame.CharAt(x, y), Attributes = Attribute(frame.InkAt(x, y)) };
                var region = new SmallRect { Left = 0, Top = (short)y, Right = (short)(frame.Width - 1), Bottom = (short)y };
                WriteConsoleOutputW(classic, cells, new Coord { X = (short)frame.Width, Y = 1 }, new Coord(), ref region);
            }
            last = frame;
        }

        // Foreground bits: 1 blue, 2 green, 4 red, 8 bright. Black behind.
        static short Attribute(Ink ink)
        {
            switch (ink)
            {
                case Ink.DarkGray: return 8;
                case Ink.White:    return 15;
                case Ink.Cyan:     return 11;
                case Ink.Yellow:   return 14;
                case Ink.Green:    return 10;
                case Ink.Red:      return 12;
                default:           return 7;
            }
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
