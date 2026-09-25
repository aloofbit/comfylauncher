using System;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading.Tasks;

namespace ComfyLauncher
{
    // THE LAUNCHER'S TUNE, in the keygen style: a four channel chip song made
    // here in code, so there is nothing to download and nothing to license.
    // Written 2026-09-24. It is rendered once into a WAV in memory, about 1.7
    // MB, the first time Music is switched on, and SoundPlayer loops it.
    //
    // 16 bars in A minor at 140 BPM over Am F C G, twice round, the last bar
    // on E major so the loop leads back into A. Channels: a pulse bass that
    // jumps octaves on the eighths, a fast arpeggio of the chord, a square
    // lead with vibrato and a dotted-eighth echo, and drums made of a falling
    // sine (kick) and noise (snare, hat).
    static class Music
    {
        const int Rate = 32000;
        const double Bpm = 140;
        const int Bars = 16, StepsPerBar = 16;
        static readonly double Step = 60.0 / Bpm / 4;       // a sixteenth, in seconds

        // One chord per bar: its name, the bass root, and the arpeggio's notes.
        static readonly (string Name, int Root, int[] Arp)[] Chords =
        {
            ("Am", 45, new[] { 69, 72, 76 }), ("F", 41, new[] { 65, 69, 72 }),
            ("C", 48, new[] { 67, 72, 76 }),  ("G", 43, new[] { 67, 71, 74 }),
            ("E", 40, new[] { 68, 71, 76 }),
        };
        static readonly string[] Progression = { "Am", "Am", "F", "F", "C", "C", "G", "G", "Am", "Am", "F", "F", "C", "C", "G", "E" };

        // The lead, one token per eighth: a note, "." to hold, "-" to rest.
        static readonly string[] Melody =
        {
            "A5 . . . C6 . E6 .",   "D6 . C6 . B5 . C6 .",  "A5 . . . F5 . A5 .",   "C6 . . . B5 A5 G5 .",
            "G5 . . . C6 . E6 .",   "G6 . F6 . E6 . D6 .",  "D6 . . . B5 . G5 .",   "B5 . C6 . D6 . . .",
            "E6 . . . A6 . . .",    "G6 . E6 . C6 . E6 .",  "F6 . . . C6 . A5 .",   "C6 . D6 . E6 . F6 .",
            "E6 . . . G6 . E6 .",   "C6 . D6 . E6 . G6 .",  "D6 . . . B5 . D6 .",   "G#5 . B5 . E6 . G#6 .",
        };

        static SoundPlayer player;
        static readonly object gate = new object();
        static bool wanted;

        // On or off, from the setting. Rendering and loading happen off the
        // key loop; a key script never makes a sound.
        public static void Set(bool on)
        {
            if (Program.Scripted) return;
            wanted = on;
            Task.Run(() =>
            {
                lock (gate)
                {
                    try
                    {
                        if (!wanted) { player?.Stop(); return; }
                        if (player == null) { player = new SoundPlayer(new MemoryStream(Wav(Render()))); player.Load(); }
                        if (wanted) player.PlayLooping();
                    }
                    catch { }   // no sound device: the launcher goes on without music
                }
            });
        }

        static double Freq(int midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);

        static int Midi(string note)
        {
            int pc = "C D EF G A B".IndexOf(note[0]);
            int i = 1;
            if (note[i] == '#') { pc++; i++; }
            return 12 * (int.Parse(note.Substring(i)) + 1) + pc;
        }

        static double Pulse(double phase, double duty) => phase - Math.Floor(phase) < duty ? 1 : -1;

        // Attack, decay to a sustain level, and a release at the end.
        static double Envelope(double t, double length, double attack, double decay, double sustain, double release)
        {
            if (t < 0 || t > length + release) return 0;
            double a = t < attack ? t / attack : sustain + (1 - sustain) * Math.Exp(-(t - attack) / decay);
            return t > length ? a * (1 - (t - length) / release) : a;
        }

        // TWO TIMES ROUND, KEEPING THE SECOND: the echo and the drum tails from
        // the end of the first pass run into the start of the second, so the
        // loop has no seam.
        public static float[] Render()
        {
            double loop = Bars * StepsPerBar * Step;
            int loopN = (int)(loop * Rate);
            var mix = new double[loopN * 2];
            var lead = new double[loopN * 2];
            uint seed = 22222;
            double Noise() { seed = seed * 1664525 + 1013904223; return (seed >> 8) / 8388608.0 - 1; }

            void Note(double[] into, double start, double length, double freq, double vol, double duty,
                      double attack, double decay, double sustain, double release, bool vibrato)
            {
                int from = (int)(start * Rate), to = Math.Min(into.Length, (int)((start + length + release) * Rate));
                double phase = 0;
                for (int n = from; n < to; n++)
                {
                    double t = (double)n / Rate - start;
                    double f = freq;
                    if (vibrato && t > 0.12) f *= Math.Pow(2, 0.25 / 12 * Math.Sin(2 * Math.PI * 5.5 * (t - 0.12)));
                    phase += f / Rate;
                    into[n] += vol * Pulse(phase, duty) * Envelope(t, length, attack, decay, sustain, release);
                }
            }

            void Drum(double start, char kind)
            {
                int from = (int)(start * Rate), to = Math.Min(mix.Length, from + (int)(0.2 * Rate));
                double phase = 0, last = 0;
                for (int n = from; n < to; n++)
                {
                    double t = (double)n / Rate - start;
                    if (kind == 'K')
                    {
                        phase += (45 + 75 * Math.Exp(-t / 0.03)) / Rate;
                        mix[n] += 0.45 * Math.Sin(2 * Math.PI * phase) * Math.Exp(-t / 0.09);
                    }
                    else if (kind == 'S')
                    {
                        phase += 190.0 / Rate;
                        mix[n] += 0.16 * Noise() * Math.Exp(-t / 0.06) + 0.10 * Math.Sin(2 * Math.PI * phase) * Math.Exp(-t / 0.03);
                    }
                    else
                    {
                        double x = Noise();
                        mix[n] += 0.06 * (x - last) * Math.Exp(-t / 0.015);
                        last = x;
                    }
                }
            }

            for (int pass = 0; pass < 2; pass++)
            {
                double at = pass * loop;
                for (int bar = 0; bar < Bars; bar++)
                {
                    var chord = Chords.First(c => c.Name == Progression[bar]);
                    double barStart = at + bar * StepsPerBar * Step;

                    // Bass: the root and its octave on the eighths.
                    for (int e = 0; e < 8; e++)
                        Note(mix, barStart + e * 2 * Step, 2 * Step * 0.85, Freq(chord.Root + (e % 2) * 12), 0.20, 0.25,
                             0.002, 0.12, 0.5, 0.02, false);

                    // Arpeggio: the chord's notes at 50 a second, pulsing on
                    // each sixteenth.
                    int from = (int)(barStart * Rate), to = (int)((barStart + StepsPerBar * Step) * Rate);
                    double phase = 0;
                    for (int n = from; n < to && n < mix.Length; n++)
                    {
                        double t = (double)n / Rate - barStart;
                        int tone = chord.Arp[(int)(t * 50) % chord.Arp.Length];
                        phase += Freq(tone) / Rate;
                        double inStep = t % Step;
                        mix[n] += 0.07 * Pulse(phase, 0.125) * (0.55 + 0.45 * Math.Exp(-inStep * 14));
                    }

                    // Lead, an octave below as written.
                    var tokens = Melody[bar].Split(' ');
                    for (int i = 0; i < tokens.Length; i++)
                    {
                        if (tokens[i] == "." || tokens[i] == "-") continue;
                        int hold = 1;
                        while (i + hold < tokens.Length && tokens[i + hold] == ".") hold++;
                        Note(lead, barStart + i * 2 * Step, hold * 2 * Step * 0.92, Freq(Midi(tokens[i]) - 12), 0.15, 0.5,
                             0.005, 0.2, 0.7, 0.04, true);
                    }

                    // Drums: kick on 1, the and of 2, and 3; snare on 2 and 4;
                    // hats on the off beats; a snare run to close the loop.
                    foreach (int s in new[] { 0, 7, 8 }) Drum(barStart + s * Step, 'K');
                    foreach (int s in bar == Bars - 1 ? new[] { 4, 12, 13, 14, 15 } : new[] { 4, 12 }) Drum(barStart + s * Step, 'S');
                    foreach (int s in new[] { 2, 6, 10, 14 }) Drum(barStart + s * Step, 'H');
                }
            }

            // The lead's echo: three sixteenths back, fed back into itself.
            int delay = (int)(3 * Step * Rate);
            for (int n = delay; n < lead.Length; n++) lead[n] += 0.35 * lead[n - delay];

            var output = new float[loopN];
            for (int n = 0; n < loopN; n++)
                output[n] = (float)Math.Tanh(0.9 * (mix[loopN + n] + lead[loopN + n]));
            return output;
        }

        // 16 bit mono PCM with its RIFF header.
        static byte[] Wav(float[] samples)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            int bytes = samples.Length * 2;
            w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVE".ToCharArray());
            w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data".ToCharArray()); w.Write(bytes);
            foreach (var s in samples) w.Write((short)Math.Round(Math.Max(-1, Math.Min(1, s)) * 32000));
            w.Flush();
            return ms.ToArray();
        }

        // For a test, or to listen outside the launcher: the loop as a file.
        public static void Save(string path) => File.WriteAllBytes(path, Wav(Render()));
    }
}
