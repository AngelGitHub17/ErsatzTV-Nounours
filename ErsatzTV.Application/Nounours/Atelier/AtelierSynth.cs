namespace ErsatzTV.Application.Nounours.Atelier;

/// <summary>
///     Petit studio « années 90 » pour les sons intégrés de l'atelier : boîte à rythmes façon 909, caisse claire
///     « gated », basse synthé, cuivres, piano électrique FM, cloches, coup d'orchestre, nappes, montée de souffle,
///     réverbération stéréo. Toutes les mélodies sont écrites ici (aucun emprunt) : seul l'esprit de l'époque est repris.
/// </summary>
internal sealed class AtelierSynth
{
    public const int Rate = 44100;

    private readonly float[] _left;
    private readonly float[] _right;
    private readonly float[] _send;
    private readonly Random _random;

    private AtelierSynth(double seconds, int seed)
    {
        int length = (int)(seconds * Rate);
        _left = new float[length];
        _right = new float[length];
        _send = new float[length];
        _random = new Random(seed);
    }

    // ================================================================== recettes

    public enum Drums
    {
        None,
        House,     // 909 quatre temps, caisse claire sur 2 et 4
        NewJack,   // chaloupé, caisse claire gated
        Break,     // breakbeat funky
        Heart      // battement de cœur (suspense)
    }

    public enum Lead
    {
        Brass,
        EPiano,
        Bell,
        Square,
        Pad
    }

    public enum Hit
    {
        Orchestra,
        Brass,
        Bell,
        Timpani,
        None
    }

    /// <summary>Une composition : tempo, tonalité, rythme, basse, accords et motif (degrés de la gamme).</summary>
    public sealed record Recipe(
        double Bpm,
        int Root,
        bool Minor,
        Drums Drums,
        Lead Lead,
        int[] Chords,           // un degré par mesure
        int[] Bass,             // 16 doubles croches par mesure : degré (0 = fondamentale) ou -99 = silence
        (int Step, int Degree, int Length)[] Motif, // sur 2 mesures, en doubles croches
        Hit Final,
        int Bars,
        bool Riser = false,
        double Swing = 0,
        bool Loop = false,
        int Seed = 1);

    /// <summary>Indicatif : quelques mesures, un coup final et sa réverbération ; boucle : mesures entières, sans fin.</summary>
    public static (float[] Left, float[] Right) Render(Recipe r)
    {
        double beat = 60 / r.Bpm;
        double bar = beat * 4;
        double step = beat / 4;
        double tail = r.Loop ? 0 : 2.4;
        double finalAt = r.Bars * bar;
        double seconds = r.Loop ? r.Bars * bar : finalAt + tail;
        var s = new AtelierSynth(seconds + (r.Loop ? 3 : 0), r.Seed); // en boucle : on replie la réverb sur le début

        double Time(int barIndex, int sixteenth)
        {
            double t = barIndex * bar + sixteenth * step;
            return sixteenth % 2 == 1 ? t + r.Swing * step : t; // chaloupé : contretemps retardés
        }

        int Note(int degree, int octave) => r.Root + octave * 12 + Scale(degree, r.Minor);

        for (var b = 0; b < r.Bars; b++)
        {
            int chord = r.Chords[b % r.Chords.Length];
            bool lastBar = !r.Loop && b == r.Bars - 1;

            // ---------- batterie
            for (var i = 0; i < 16; i++)
            {
                double t = Time(b, i);
                switch (r.Drums)
                {
                    case Drums.House:
                        if (i % 4 == 0) s.Kick(t, 0.95);
                        if (i is 4 or 12) s.Clap(t, 0.55);
                        if (i % 4 == 2) s.HatOpen(t, 0.18);
                        else if (i % 2 == 1) s.HatClosed(t, 0.12);
                        break;
                    case Drums.NewJack:
                        if (i is 0 or 7 or 10) s.Kick(t, 0.9);
                        if (i is 4 or 12) s.GatedSnare(t, 0.6);
                        if (i % 2 == 0) s.HatClosed(t, i % 4 == 0 ? 0.16 : 0.1);
                        break;
                    case Drums.Break:
                        if (i is 0 or 6 or 10) s.Kick(t, 0.9);
                        if (i is 4 or 12) s.Snare(t, 0.6);
                        if (i is 15) s.Snare(t, 0.25);
                        s.HatClosed(t, i % 2 == 0 ? 0.13 : 0.07);
                        break;
                    case Drums.Heart:
                        if (i is 0) s.Kick(t, 0.8);
                        if (i is 3) s.Kick(t, 0.45);
                        if (i is 8) s.HatClosed(t, 0.05);
                        break;
                }
            }

            if (b == 0 && !r.Loop)
            {
                s.Crash(0, 0.25);
            }

            // ---------- basse
            for (var i = 0; i < 16; i++)
            {
                int degree = r.Bass[i % r.Bass.Length];
                if (degree == -99)
                {
                    continue;
                }

                s.Bass(Time(b, i), step * 1.6, Midi(Note(chord + degree, -2)), 0.42);
            }

            // ---------- accords
            int[] triad = [Note(chord, 0), Note(chord + 2, 0), Note(chord + 4, 0)];
            switch (r.Lead)
            {
                case Lead.Pad:
                case Lead.EPiano:
                    s.Pad(Time(b, 0), bar * 1.02, triad, r.Lead == Lead.Pad ? 0.1 : 0.05);
                    if (r.Lead == Lead.EPiano)
                    {
                        foreach (int i in new[] { 0, 6, 10 })
                        {
                            s.EPiano(Time(b, i), step * 3, triad, 0.16);
                        }
                    }

                    break;
                default:
                    // stabs de cuivres en rythme
                    foreach (int i in new[] { 0, 3, 6, 10 })
                    {
                        if (lastBar && i > 0)
                        {
                            break;
                        }

                        s.Brass(Time(b, i), step * (i == 0 ? 2.5 : 1.4), triad.Select(n => n + 12).ToArray(), 0.12);
                    }

                    break;
            }

            // ---------- motif (écrit sur deux mesures)
            foreach ((int stepIndex, int degree, int length) in r.Motif)
            {
                int barOfMotif = stepIndex / 16;
                if (barOfMotif != b % 2 || lastBar && stepIndex % 16 >= 8)
                {
                    continue;
                }

                double t = Time(b, stepIndex % 16);
                double len = step * length;
                double f = Midi(Note(degree, 1));
                switch (r.Lead)
                {
                    case Lead.Brass:
                        s.Brass(t, len, [Note(degree, 1)], 0.2);
                        break;
                    case Lead.EPiano:
                        s.EPiano(t, len * 1.5, [Note(degree, 1)], 0.22);
                        break;
                    case Lead.Bell:
                        s.Bell(t, len * 2, f, 0.22);
                        break;
                    case Lead.Square:
                        s.Square(t, len, f, 0.15);
                        break;
                    case Lead.Pad:
                        s.Bell(t, len * 2, f, 0.12);
                        break;
                }
            }
        }

        if (r.Riser && !r.Loop)
        {
            s.Riser(finalAt - bar, bar, 0.22);
        }

        // ---------- coup final
        if (!r.Loop)
        {
            int[] chord = [Note(0, 0), Note(2, 0), Note(4, 0), Note(0, 1)];
            s.Kick(finalAt, 1);
            s.Crash(finalAt, 0.35);
            switch (r.Final)
            {
                case Hit.Orchestra:
                    s.OrchestraHit(finalAt, chord, 0.5);
                    break;
                case Hit.Brass:
                    s.Brass(finalAt, 1.4, chord.Select(n => n + 12).ToArray(), 0.2);
                    s.Bass(finalAt, 1.2, Midi(Note(0, -2)), 0.5);
                    break;
                case Hit.Bell:
                    foreach (int n in chord)
                    {
                        s.Bell(finalAt, 2.2, Midi(n + 12), 0.14);
                    }

                    s.EPiano(finalAt, 1.6, chord, 0.15);
                    break;
                case Hit.Timpani:
                    s.Timpani(finalAt, 0.9);
                    s.Brass(finalAt, 1.6, chord, 0.16);
                    break;
            }
        }

        return s.Master(seconds, r.Loop);
    }

    private static readonly int[] MajorScale = [0, 2, 4, 5, 7, 9, 11];
    private static readonly int[] MinorScale = [0, 2, 3, 5, 7, 8, 10];

    private static int Scale(int degree, bool minor)
    {
        int[] scale = minor ? MinorScale : MajorScale;
        int octave = (int)Math.Floor(degree / 7.0);
        int index = degree - octave * 7;
        return scale[index] + octave * 12;
    }

    private static double Midi(int note) => 440 * Math.Pow(2, (note - 69) / 12.0);

    // ================================================================== instruments

    private static int Index(double t) => (int)(t * Rate);

    private void Put(int i, double value, double pan = 0, double send = 0)
    {
        if (i < 0 || i >= _left.Length)
        {
            return;
        }

        _left[i] += (float)(value * (1 - Math.Max(0, pan)));
        _right[i] += (float)(value * (1 + Math.Min(0, pan)));
        _send[i] += (float)(value * send);
    }

    private double Noise() => _random.NextDouble() * 2 - 1;

    private static double Saw(double phase) => 2 * (phase - Math.Floor(phase)) - 1;

    /// <summary>Filtre à variables d'état (passe-bas résonant), stable jusqu'à ~7 kHz.</summary>
    private struct Svf
    {
        private double _low;
        private double _band;

        public double Low(double input, double cutoff, double resonance)
        {
            double f = 2 * Math.Sin(Math.PI * Math.Clamp(cutoff, 20, 7000) / Rate);
            double q = Math.Clamp(1 - resonance, 0.08, 1);
            _low += f * _band;
            double high = input - _low - q * _band;
            _band += f * high;
            return _low;
        }

        public double Band(double input, double cutoff, double resonance)
        {
            Low(input, cutoff, resonance);
            return _band;
        }
    }

    public void Kick(double at, double gain)
    {
        int start = Index(at);
        double phase = 0;
        for (var i = 0; i < Rate * 0.42; i++)
        {
            double t = i / (double)Rate;
            phase += (46 + 150 * Math.Exp(-t * 32)) / Rate;
            double body = Math.Sin(2 * Math.PI * phase) * Math.Exp(-t * 6.5);
            double click = i < Rate * 0.003 ? Noise() * 0.4 : 0;
            Put(start + i, (Math.Tanh(body * 1.6) + click) * gain * 0.8);
        }
    }

    public void Snare(double at, double gain)
    {
        int start = Index(at);
        var filter = new Svf();
        double phase = 0;
        for (var i = 0; i < Rate * 0.25; i++)
        {
            double t = i / (double)Rate;
            phase += 185.0 / Rate;
            double tone = Math.Sin(2 * Math.PI * phase) * Math.Exp(-t * 28) * 0.5;
            double noise = filter.Band(Noise(), 2200, 0.2) * Math.Exp(-t * 18) * 1.4;
            Put(start + i, (tone + noise) * gain, 0.05, 0.35);
        }
    }

    /// <summary>Caisse claire « gated » des années 80-90 : un souffle plein qui s'arrête net.</summary>
    public void GatedSnare(double at, double gain)
    {
        Snare(at, gain * 0.8);
        int start = Index(at);
        var filter = new Svf();
        int length = (int)(Rate * 0.2);
        for (var i = 0; i < length; i++)
        {
            double gate = i > length - Rate * 0.015 ? (length - i) / (Rate * 0.015) : 1;
            double noise = filter.Band(Noise(), 1500, 0.1) * 0.9 * gate * (0.7 + 0.3 * Math.Exp(-i / (double)Rate * 10));
            Put(start + i, noise * gain, -0.05, 0.1);
        }
    }

    public void Clap(double at, double gain)
    {
        int start = Index(at);
        var filter = new Svf();
        for (var i = 0; i < Rate * 0.22; i++)
        {
            double t = i / (double)Rate;
            double bursts = t < 0.03 ? Math.Exp(-(t % 0.01) * 300) : Math.Exp(-(t - 0.03) * 16);
            Put(start + i, filter.Band(Noise(), 1300, 0.3) * bursts * gain * 1.6, 0, 0.3);
        }
    }

    public void HatClosed(double at, double gain) => Hat(at, gain, 55, 0.25);

    public void HatOpen(double at, double gain) => Hat(at, gain, 12, -0.25);

    private void Hat(double at, double gain, double decay, double pan)
    {
        int start = Index(at);
        double previous = 0;
        double previous2 = 0;
        for (var i = 0; i < Rate * 0.3; i++)
        {
            double t = i / (double)Rate;
            double white = Noise();
            double high = white - 2 * previous + previous2; // passe-haut du 2e ordre
            previous2 = previous;
            previous = white;
            Put(start + i, high * 0.35 * Math.Exp(-t * decay) * gain, pan, 0.05);
        }
    }

    public void Crash(double at, double gain)
    {
        int start = Index(at);
        double previous = 0;
        for (var i = 0; i < Rate * 1.8; i++)
        {
            double t = i / (double)Rate;
            double white = Noise();
            Put(start + i, (white - previous) * 0.5 * Math.Exp(-t * 2.2) * gain, (i % 2 == 0 ? 0.2 : -0.2), 0.3);
            previous = white;
        }
    }

    public void Bass(double at, double length, double frequency, double gain)
    {
        int start = Index(at);
        var filter = new Svf();
        double phase = 0;
        for (var i = 0; i < length * Rate; i++)
        {
            double t = i / (double)Rate;
            phase += frequency / Rate;
            double cutoff = 260 + 1600 * Math.Exp(-t * 18);
            double release = Math.Min(1, (length - t) / 0.02);
            double sample = filter.Low(Saw(phase) + 0.5 * Math.Sin(2 * Math.PI * phase), cutoff, 0.55);
            Put(start + i, Math.Tanh(sample * 1.5) * Math.Exp(-t * 3) * release * gain);
        }
    }

    /// <summary>Section de cuivres synthé : trois dents de scie désaccordées par note, filtre qui s'ouvre.</summary>
    public void Brass(double at, double length, int[] notes, double gain)
    {
        int start = Index(at);
        double[] detune = [-0.006, 0, 0.007];
        foreach (int note in notes)
        {
            double f = Midi(note);
            var filter = new Svf();
            double[] phases = [Noise(), Noise(), Noise()];
            for (var i = 0; i < (length + 0.12) * Rate; i++)
            {
                double t = i / (double)Rate;
                double env = t < 0.012 ? t / 0.012 : t < length ? 0.85 + 0.15 * Math.Exp(-(t - 0.012) * 6) : 0.85 * Math.Exp(-(t - length) * 30);
                double cutoff = 500 + 3800 * Math.Min(1, t / 0.03) * Math.Exp(-t * 3) + 900;
                double sum = 0;
                for (var k = 0; k < 3; k++)
                {
                    phases[k] += f * (1 + detune[k]) / Rate;
                    sum += Saw(phases[k]);
                }

                Put(start + i, filter.Low(sum / 3, cutoff, 0.25) * env * gain, (note % 3 - 1) * 0.25, 0.25);
            }
        }
    }

    /// <summary>Piano électrique en synthèse FM (le son « DX » des années 80-90).</summary>
    public void EPiano(double at, double length, int[] notes, double gain)
    {
        int start = Index(at);
        foreach (int note in notes)
        {
            double f = Midi(note);
            for (var i = 0; i < (length + 0.3) * Rate; i++)
            {
                double t = i / (double)Rate;
                double index = 2.2 * Math.Exp(-t * 5) + 0.4;
                double modulator = Math.Sin(2 * Math.PI * f * t);
                double tine = Math.Sin(2 * Math.PI * f * 14 * t) * Math.Exp(-t * 40) * 0.6;
                double carrier = Math.Sin(2 * Math.PI * f * t + index * modulator + tine);
                double env = Math.Exp(-t * 1.8) * (t < length ? 1 : Math.Exp(-(t - length) * 12));
                Put(start + i, carrier * env * gain, (note % 2 == 0 ? 0.2 : -0.2), 0.2);
            }
        }
    }

    public void Bell(double at, double length, double frequency, double gain)
    {
        int start = Index(at);
        for (var i = 0; i < length * Rate; i++)
        {
            double t = i / (double)Rate;
            double modulator = Math.Sin(2 * Math.PI * frequency * 3.5 * t) * 3 * Math.Exp(-t * 3);
            double sample = Math.Sin(2 * Math.PI * frequency * t + modulator) * Math.Exp(-t * 2.2) * Math.Min(1, (length - t) / 0.05);
            Put(start + i, sample * gain, 0.3, 0.35);
        }
    }

    public void Square(double at, double length, double frequency, double gain)
    {
        int start = Index(at);
        var filter = new Svf();
        double phase = 0;
        for (var i = 0; i < length * Rate; i++)
        {
            double t = i / (double)Rate;
            phase += frequency * (1 + 0.004 * Math.Sin(2 * Math.PI * 5.5 * t) * Math.Min(1, t * 4)) / Rate;
            double square = phase - Math.Floor(phase) < 0.5 ? 0.7 : -0.7;
            double env = Math.Min(1, t / 0.005) * (0.8 + 0.2 * Math.Exp(-t * 8)) * Math.Min(1, (length - t) / 0.02);
            Put(start + i, filter.Low(square, 3500, 0.1) * env * gain, -0.15, 0.2);
        }
    }

    public void Pad(double at, double length, int[] notes, double gain)
    {
        int start = Index(at);
        foreach (int note in notes)
        {
            double f = Midi(note);
            var filter = new Svf();
            double a = Noise();
            double b = Noise();
            for (var i = 0; i < (length + 0.4) * Rate; i++)
            {
                double t = i / (double)Rate;
                a += f * 0.997 / Rate;
                b += f * 1.004 / Rate;
                double env = Math.Min(1, t / 0.35) * (t < length ? 1 : Math.Exp(-(t - length) * 6));
                Put(start + i, filter.Low((Saw(a) + Saw(b)) / 2, 1100, 0.1) * env * gain, (note % 2 == 0 ? 0.35 : -0.35), 0.4);
            }
        }
    }

    /// <summary>Coup d'orchestre : accord plein, attaque brutale, tout dans la réverbération.</summary>
    public void OrchestraHit(double at, int[] notes, double gain)
    {
        int start = Index(at);
        var noiseFilter = new Svf();
        for (var i = 0; i < Rate * 0.9; i++)
        {
            double t = i / (double)Rate;
            Put(start + i, noiseFilter.Band(Noise(), 2500, 0.2) * Math.Exp(-t * 30) * gain * 0.6, 0, 0.5);
        }

        foreach (int note in notes.Concat(notes.Select(n => n - 12)))
        {
            double f = Midi(note);
            var filter = new Svf();
            double phase = Noise();
            double phase2 = Noise();
            for (var i = 0; i < Rate * 0.9; i++)
            {
                double t = i / (double)Rate;
                phase += f / Rate;
                phase2 += f * 1.008 / Rate;
                double env = Math.Min(1, t / 0.004) * Math.Exp(-t * 4.5);
                double cutoff = 900 + 5000 * Math.Exp(-t * 6);
                Put(start + i, filter.Low((Saw(phase) + Saw(phase2)) * 0.5, cutoff, 0.2) * env * gain * 0.35, (note % 2 == 0 ? 0.3 : -0.3), 0.6);
            }
        }
    }

    public void Timpani(double at, double gain)
    {
        int start = Index(at);
        var filter = new Svf();
        double phase = 0;
        for (var i = 0; i < Rate * 1.6; i++)
        {
            double t = i / (double)Rate;
            phase += (68 + 26 * Math.Exp(-t * 10)) / Rate;
            double sample = Math.Sin(2 * Math.PI * phase) * 0.9 + filter.Low(Noise(), 300, 0.1) * 0.8;
            Put(start + i, sample * Math.Exp(-t * 2.6) * gain, 0, 0.4);
        }
    }

    /// <summary>Montée de souffle avant le coup final.</summary>
    public void Riser(double at, double length, double gain)
    {
        int start = Index(at);
        var filter = new Svf();
        for (var i = 0; i < length * Rate; i++)
        {
            double p = i / (length * Rate);
            Put(start + i, filter.Band(Noise(), 300 + 5500 * p * p, 0.5) * p * p * gain * 1.5, Math.Sin(p * 20) * 0.3, 0.3);
        }
    }

    // ================================================================== mixage

    /// <summary>Réverbération (peignes + passe-tout, à la Schroeder), compression douce, normalisation à −1 dB.</summary>
    private (float[] Left, float[] Right) Master(double seconds, bool loop)
    {
        float[] reverbL = Reverb(_send, 0);
        float[] reverbR = Reverb(_send, 23);
        int length = (int)(seconds * Rate);
        var left = new float[length];
        var right = new float[length];
        for (var i = 0; i < _left.Length; i++)
        {
            // en boucle, ce qui dépasse la fin (queue de réverb) revient au début : la boucle se raccorde sans trou
            int target = loop ? i % length : i;
            if (target >= length)
            {
                break;
            }

            left[target] += _left[i] + reverbL[i] * 0.55f;
            right[target] += _right[i] + reverbR[i] * 0.55f;
        }

        float peak = 0.0001f;
        for (var i = 0; i < length; i++)
        {
            left[i] = (float)Math.Tanh(left[i] * 1.2);
            right[i] = (float)Math.Tanh(right[i] * 1.2);
            peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        }

        float scale = 0.89f / peak;
        int fade = loop ? 0 : (int)(Rate * 0.3);
        for (var i = 0; i < length; i++)
        {
            float f = fade > 0 && i > length - fade ? (length - i) / (float)fade : 1;
            left[i] *= scale * f;
            right[i] *= scale * f;
        }

        return (left, right);
    }

    private static float[] Reverb(float[] input, int spread)
    {
        int[] combs = [1116, 1188, 1277, 1356];
        int[] allpasses = [556, 441];
        var output = new float[input.Length];
        foreach (int baseLength in combs)
        {
            int size = baseLength + spread;
            var buffer = new float[size];
            var index = 0;
            float filtered = 0;
            for (var i = 0; i < input.Length; i++)
            {
                float delayed = buffer[index];
                filtered = delayed * 0.75f + filtered * 0.25f;
                buffer[index] = input[i] * 0.015f * 10 + filtered * 0.82f;
                output[i] += delayed;
                index = (index + 1) % size;
            }
        }

        foreach (int baseLength in allpasses)
        {
            int size = baseLength + spread;
            var buffer = new float[size];
            var index = 0;
            for (var i = 0; i < output.Length; i++)
            {
                float delayed = buffer[index];
                float value = output[i];
                buffer[index] = value + delayed * 0.5f;
                output[i] = delayed - value;
                index = (index + 1) % size;
            }
        }

        for (var i = 0; i < output.Length; i++)
        {
            output[i] *= 0.25f;
        }

        return output;
    }

    /// <summary>WAV 16 bits stéréo 44,1 kHz.</summary>
    public static void WriteWav(string path, float[] left, float[] right)
    {
        using FileStream stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        int dataBytes = left.Length * 4;
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(Rate);
        writer.Write(Rate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);
        for (var i = 0; i < left.Length; i++)
        {
            writer.Write((short)Math.Clamp(Math.Round(left[i] * 32767), short.MinValue, short.MaxValue));
            writer.Write((short)Math.Clamp(Math.Round(right[i] * 32767), short.MinValue, short.MaxValue));
        }
    }
}
