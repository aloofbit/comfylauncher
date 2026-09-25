using System;
using System.Text;

namespace ComfyLauncher
{
    // The console's sixteen colours, by the names ComfyLauncher.ps1 uses.
    enum Ink { Gray, DarkGray, White, Cyan, Yellow, Green, Red }

    // One frame: a grid of characters, each with a colour.
    //
    // THE VIEW WRITES HERE AND NOWHERE ELSE. Nothing in View.cs touches the
    // console, so a frame is a value that can be built, compared and printed as
    // text. That is what the PowerShell launcher could not do: it drew with
    // Write-Host as it went, so a frame existed only on the screen, and every
    // "the UI duplicated" bug was the cursor being somewhere other than where
    // the next Write-Host assumed.
    sealed class Screen
    {
        public readonly int Width;
        public readonly int Height;
        readonly char[] chars;
        readonly Ink[] inks;

        public Screen(int width, int height)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            chars = new char[Width * Height];
            inks = new Ink[Width * Height];
            for (int i = 0; i < chars.Length; i++) chars[i] = ' ';
        }

        // Writes text at x, y and returns the column after it. Anything past
        // the right edge or outside the grid is dropped, never wrapped.
        public int Put(int x, int y, string text, Ink ink)
        {
            if (text == null || y < 0 || y >= Height) return x;
            foreach (char c in text)
            {
                if (x >= Width) break;
                if (x >= 0)
                {
                    chars[y * Width + x] = c;
                    inks[y * Width + x] = ink;
                }
                x++;
            }
            return x;
        }

        public char CharAt(int x, int y) => chars[y * Width + x];
        public Ink InkAt(int x, int y) => inks[y * Width + x];

        public bool RowEquals(Screen other, int y)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = y * Width + x;
                if (chars[i] != other.chars[i] || inks[i] != other.inks[i]) return false;
            }
            return true;
        }

        // The frame as plain text, trailing spaces cut. For --dump.
        public string Dump()
        {
            var sb = new StringBuilder();
            for (int y = 0; y < Height; y++)
            {
                sb.Append(new string(chars, y * Width, Width).TrimEnd());
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
