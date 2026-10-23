using System.Text.Json;

namespace ErsatzTV.Application.Nounours.Atelier;

/// <summary>
///     Les sons de l'atelier rangés par ambiance (énergique, calme, suspense…), plus quelques boucles
///     « maison » fabriquées par le programme lui-même (aucun droit d'auteur : elles sont synthétisées ici).
/// </summary>
public static class AtelierSounds
{
    public static readonly string[] Ambiances = ["Énergique", "Calme", "Suspense", "Jeunesse", "Cinéma", "Pub"];

    public const string BuiltinPrefix = "integre-";

    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string TagsFile => Path.Combine(AtelierService.SourcesFolder, "ambiances.json");

    /// <summary>Boucles intégrées : nom de fichier, nom affiché, ambiance, générateur.</summary>
    private static readonly (string File, string Name, string Ambiance, Func<float[]> Make)[] Builtins =
    [
        (BuiltinPrefix + "pulsation-95.wav", "Pulsation 95", "Énergique", Synth.Pulsation),
        (BuiltinPrefix + "vitamine.wav", "Vitamine", "Jeunesse", Synth.Vitamine),
        (BuiltinPrefix + "nuit-calme.wav", "Nuit calme", "Calme", Synth.NuitCalme),
        (BuiltinPrefix + "suspense.wav", "Suspense", "Suspense", Synth.Suspense),
        (BuiltinPrefix + "grand-ecran.wav", "Grand écran", "Cinéma", Synth.GrandEcran),
        (BuiltinPrefix + "coupure-pub.wav", "Coupure pub", "Pub", Synth.CoupurePub)
    ];

    /// <summary>Nom lisible d'un son (« Pulsation 95 (intégré) » pour les boucles maison).</summary>
    public static string DisplayName(string file)
    {
        foreach ((string f, string name, _, _) in Builtins)
        {
            if (string.Equals(f, file, StringComparison.OrdinalIgnoreCase))
            {
                return $"{name} · intégré";
            }
        }

        return file;
    }

    /// <summary>Fabrique les boucles intégrées manquantes (quelques dixièmes de seconde, une seule fois).</summary>
    public static void EnsureBuiltins()
    {
        lock (Lock)
        {
            Directory.CreateDirectory(AtelierService.SourcesFolder);
            Dictionary<string, string> tags = LoadTags();
            var changed = false;
            foreach ((string file, _, string ambiance, Func<float[]> make) in Builtins)
            {
                string path = Path.Combine(AtelierService.SourcesFolder, file);
                if (!File.Exists(path))
                {
                    Synth.WriteWav(path, make());
                }

                if (!tags.ContainsKey(file))
                {
                    tags[file] = ambiance;
                    changed = true;
                }
            }

            if (changed)
            {
                SaveTags(tags);
            }
        }
    }

    public static string AmbianceOf(string file)
    {
        lock (Lock)
        {
            return file is not null && LoadTags().TryGetValue(file, out string a) ? a : string.Empty;
        }
    }

    public static Dictionary<string, string> AllTags()
    {
        lock (Lock)
        {
            return LoadTags();
        }
    }

    public static void SetAmbiance(string file, string ambiance)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        lock (Lock)
        {
            Dictionary<string, string> tags = LoadTags();
            if (string.IsNullOrWhiteSpace(ambiance))
            {
                tags.Remove(file);
            }
            else
            {
                tags[file] = ambiance;
            }

            SaveTags(tags);
        }
    }

    /// <summary>Un son de l'ambiance demandée (le premier trouvé), pour l'habillage complet en un clic.</summary>
    public static string FirstOf(string ambiance)
    {
        EnsureBuiltins();
        Dictionary<string, string> tags = AllTags();
        return AtelierService.Sources()
            .Select(s => s.Name)
            .Where(n => tags.TryGetValue(n, out string a) && a == ambiance)
            .OrderBy(n => n.StartsWith(BuiltinPrefix, StringComparison.Ordinal)) // les sons de l'utilisateur d'abord
            .FirstOrDefault() ?? string.Empty;
    }

    private static Dictionary<string, string> LoadTags()
    {
        try
        {
            if (File.Exists(TagsFile))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TagsFile)) ?? [];
            }
        }
        catch (Exception)
        {
            // fichier abîmé : on repart de zéro
        }

        return [];
    }

    private static void SaveTags(Dictionary<string, string> tags)
    {
        Directory.CreateDirectory(AtelierService.SourcesFolder);
        File.WriteAllText(TagsFile, JsonSerializer.Serialize(tags, Indented));
    }

    // ================================================================== synthèse

    /// <summary>Un petit synthétiseur : batterie, basse, accords. Rendu en stéréo 44,1 kHz (gauche = droite).</summary>
    private static class Synth
    {
        private const int Rate = 44100;

        public static float[] Pulsation()
        {
            const double bpm = 124;
            double beat = 60 / bpm;
            var buffer = new float[(int)(beat * 32 * Rate)]; // 8 mesures
            int[] bass = [45, 45, 48, 43]; // la, la, do, sol (une note par mesure)
            for (var b = 0; b < 32; b++)
            {
                double t = b * beat;
                Kick(buffer, t, 0.9);
                Hat(buffer, t + beat / 2, 0.22);
                if (b % 2 == 1)
                {
                    Clap(buffer, t, 0.5);
                }

                int root = bass[b / 4 % 4];
                Tone(buffer, t + beat / 2, beat * 0.45, Midi(root - 12), 0.32, Wave.Square, 0.02, 6);
                if (b % 4 == 0)
                {
                    Chord(buffer, t, beat * 0.5, [root + 12, root + 15, root + 19], 0.12, Wave.Saw);
                }
            }

            return buffer;
        }

        public static float[] Vitamine()
        {
            const double bpm = 132;
            double beat = 60 / bpm;
            var buffer = new float[(int)(beat * 32 * Rate)];
            int[][] chords = [[60, 64, 67], [65, 69, 72], [67, 71, 74], [64, 67, 72]];
            for (var b = 0; b < 32; b++)
            {
                double t = b * beat;
                Kick(buffer, t, b % 2 == 0 ? 0.8 : 0.5);
                Hat(buffer, t + beat / 2, 0.18);
                int[] chord = chords[b / 4 % 4];
                for (var s = 0; s < 4; s++)
                {
                    // arpège en doubles croches, une octave au-dessus
                    Tone(buffer, t + s * beat / 4, beat / 4 * 0.8, Midi(chord[s % 3] + 12), 0.14, Wave.Square, 0.003, 18);
                }

                if (b % 4 == 0)
                {
                    Tone(buffer, t, beat * 1.8, Midi(chord[0] - 24), 0.35, Wave.Triangle, 0.01, 2);
                }
            }

            return buffer;
        }

        public static float[] NuitCalme()
        {
            const double bpm = 80;
            double beat = 60 / bpm;
            var buffer = new float[(int)(beat * 24 * Rate)];
            int[][] chords = [[57, 60, 64, 67], [53, 57, 60, 64], [55, 59, 62, 67], [52, 55, 60, 64]];
            for (var bar = 0; bar < 6; bar++)
            {
                double t = bar * beat * 4;
                Chord(buffer, t, beat * 4.2, chords[bar % 4], 0.11, Wave.Sine, 0.6, 0.6);
                for (var b = 0; b < 4; b++)
                {
                    Rim(buffer, t + b * beat, b == 0 ? 0.25 : 0.12);
                    Tone(buffer, t + b * beat + beat / 2, beat * 0.4, Midi(chords[bar % 4][(b + bar) % 4] + 12), 0.07, Wave.Sine, 0.01, 4);
                }

                Kick(buffer, t, 0.45);
            }

            return buffer;
        }

        public static float[] Suspense()
        {
            const double bpm = 100;
            double beat = 60 / bpm;
            var buffer = new float[(int)(beat * 24 * Rate)];
            Tone(buffer, 0, beat * 24, Midi(33), 0.22, Wave.Saw, 1.5, 0, 0.08); // bourdon grave
            for (var b = 0; b < 24; b++)
            {
                double t = b * beat;
                // battement de cœur : deux coups
                Kick(buffer, t, 0.75);
                Kick(buffer, t + beat * 0.28, 0.45);
                if (b % 2 == 1)
                {
                    Tone(buffer, t + beat / 2, beat * 0.3, Midi(b % 4 == 1 ? 81 : 82), 0.06, Wave.Sine, 0.005, 10);
                }
            }

            return buffer;
        }

        public static float[] GrandEcran()
        {
            const double bpm = 90;
            double beat = 60 / bpm;
            var buffer = new float[(int)(beat * 16 * Rate)];
            int[][] chords = [[50, 57, 62, 65], [46, 53, 58, 62], [48, 55, 60, 64], [45, 52, 57, 61]];
            for (var bar = 0; bar < 4; bar++)
            {
                double t = bar * beat * 4;
                double gain = 0.6 + bar * 0.15; // crescendo
                Chord(buffer, t, beat * 3.8, chords[bar], 0.1 * gain, Wave.Saw, 0.15, 0.5, 0.25);
                Timpani(buffer, t, 0.8 * gain);
                Timpani(buffer, t + beat * 2.5, 0.5 * gain);
                Timpani(buffer, t + beat * 3, 0.6 * gain);
            }

            Timpani(buffer, beat * 15.5, 1);
            return buffer;
        }

        public static float[] CoupurePub()
        {
            const double bpm = 140;
            double beat = 60 / bpm;
            var buffer = new float[(int)(beat * 16 * Rate)];
            int[] melody = [72, 76, 79, 84, 83, 79, 76, 79, 77, 81, 84, 89, 88, 84, 79, 84];
            for (var b = 0; b < 16; b++)
            {
                double t = b * beat;
                Kick(buffer, t, 0.7);
                Hat(buffer, t + beat / 2, 0.2);
                if (b % 2 == 1)
                {
                    Clap(buffer, t, 0.45);
                }

                Tone(buffer, t, beat * 0.85, Midi(melody[b]), 0.16, Wave.Square, 0.005, 5);
                Tone(buffer, t, beat * 0.9, Midi(melody[b / 4 * 4] - 24), 0.3, Wave.Triangle, 0.005, 3);
            }

            Chord(buffer, beat * 15, beat, [72, 76, 79, 84], 0.15, Wave.Square, 0.005, 3);
            return buffer;
        }

        // ------------------------------------------------------------------ instruments

        private enum Wave
        {
            Sine,
            Square,
            Saw,
            Triangle
        }

        private static double Midi(int note) => 440 * Math.Pow(2, (note - 69) / 12.0);

        private static double Osc(Wave wave, double phase)
        {
            double x = phase - Math.Floor(phase);
            return wave switch
            {
                Wave.Square => x < 0.5 ? 0.8 : -0.8,
                Wave.Saw => 2 * x - 1,
                Wave.Triangle => 1 - 4 * Math.Abs(x - 0.5),
                _ => Math.Sin(2 * Math.PI * x)
            };
        }

        /// <summary>Une note : attaque, puis décroissance exponentielle (<paramref name="decay" /> 0 = tenue) ; filtre passe-bas doux.</summary>
        private static void Tone(float[] buffer, double at, double length, double frequency, double gain, Wave wave, double attack, double decay, double lowpass = 0.35)
        {
            int start = (int)(at * Rate);
            int count = (int)(length * Rate);
            double filtered = 0;
            for (var i = 0; i < count && start + i < buffer.Length; i++)
            {
                double t = i / (double)Rate;
                double env = Math.Min(1, t / Math.Max(0.001, attack)) * (decay > 0 ? Math.Exp(-t * decay) : 1);
                double release = Math.Min(1, (length - t) / 0.03); // pas de clic à la fin
                double sample = Osc(wave, frequency * t) * env * release * gain;
                filtered += (sample - filtered) * lowpass;
                buffer[start + i] += (float)filtered;
            }
        }

        private static void Chord(float[] buffer, double at, double length, int[] notes, double gain, Wave wave, double attack = 0.01, double decay = 1.5, double lowpass = 0.2)
        {
            foreach (int note in notes)
            {
                Tone(buffer, at, length, Midi(note), gain, wave, attack, decay, lowpass);
            }
        }

        private static void Kick(float[] buffer, double at, double gain)
        {
            int start = (int)(at * Rate);
            double phase = 0;
            for (var i = 0; i < Rate * 0.35 && start + i < buffer.Length; i++)
            {
                double t = i / (double)Rate;
                double frequency = 48 + 110 * Math.Exp(-t * 30);
                phase += frequency / Rate;
                buffer[start + i] += (float)(Math.Sin(2 * Math.PI * phase) * Math.Exp(-t * 9) * gain);
            }
        }

        private static void Timpani(float[] buffer, double at, double gain)
        {
            int start = (int)(at * Rate);
            var random = new Random(start);
            double phase = 0;
            double noise = 0;
            for (var i = 0; i < Rate * 1.4 && start + i < buffer.Length; i++)
            {
                double t = i / (double)Rate;
                phase += (70 + 30 * Math.Exp(-t * 12)) / Rate;
                noise += (random.NextDouble() * 2 - 1 - noise) * 0.05;
                buffer[start + i] += (float)((Math.Sin(2 * Math.PI * phase) * 0.8 + noise * 1.5) * Math.Exp(-t * 2.8) * gain);
            }
        }

        private static void Noise(float[] buffer, double at, double length, double gain, double decay, bool bright)
        {
            int start = (int)(at * Rate);
            var random = new Random(start + 17);
            double previous = 0;
            for (var i = 0; i < length * Rate && start + i < buffer.Length; i++)
            {
                double t = i / (double)Rate;
                double white = random.NextDouble() * 2 - 1;
                double sample = bright ? white - previous : white; // passe-haut grossier pour les cymbales
                previous = white;
                buffer[start + i] += (float)(sample * Math.Exp(-t * decay) * gain);
            }
        }

        private static void Hat(float[] buffer, double at, double gain) => Noise(buffer, at, 0.06, gain, 60, true);

        private static void Clap(float[] buffer, double at, double gain)
        {
            Noise(buffer, at, 0.18, gain, 22, false);
            Noise(buffer, at + 0.012, 0.12, gain * 0.6, 30, false);
        }

        private static void Rim(float[] buffer, double at, double gain) =>
            Tone(buffer, at, 0.05, 1700, gain, Wave.Triangle, 0.0005, 70, 0.9);

        /// <summary>Normalise (−1 dB) et écrit un WAV 16 bits stéréo.</summary>
        public static void WriteWav(string path, float[] mono)
        {
            float peak = mono.Length == 0 ? 1 : Math.Max(0.0001f, mono.Max(Math.Abs));
            float scale = 0.89f / peak;
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            int dataBytes = mono.Length * 4;
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
            foreach (float sample in mono)
            {
                var value = (short)Math.Clamp(Math.Round(Math.Tanh(sample * scale * 1.1) / Math.Tanh(1.1) * 32767), short.MinValue, short.MaxValue);
                writer.Write(value);
                writer.Write(value);
            }
        }
    }
}

/// <summary>Signalétique jeunesse : niveau (-10, -12, -16, -18) d'après la classification d'un programme, et le carton qui va avec.</summary>
public static class Signaletique
{
    /// <summary>0 = tous publics / non classé ; sinon 10, 12, 16 ou 18. Mêmes règles que le rond de l'habillage.</summary>
    public static int Level(string rating)
    {
        string r = (rating ?? string.Empty).Trim().ToLowerInvariant();
        if (r.Length == 0)
        {
            return 0;
        }

        if (r.Contains("18", StringComparison.Ordinal) || r == "nc-17")
        {
            return 18;
        }

        if (r.Contains("16", StringComparison.Ordinal) || r is "tv-ma" or "r")
        {
            return 16;
        }

        if (r.Contains("12", StringComparison.Ordinal) || r.Contains("13", StringComparison.Ordinal) || r.Contains("14", StringComparison.Ordinal))
        {
            return 12;
        }

        if (r.Contains("10", StringComparison.Ordinal) || r is "tv-pg" or "pg")
        {
            return 10;
        }

        return 0;
    }

    /// <summary>« SÉRIE DÉCONSEILLÉE AUX MOINS DE 12 ANS », « FILM INTERDIT AUX MOINS DE 18 ANS »… (vide si tous publics).</summary>
    public static string Message(int level, string label)
    {
        if (level <= 0)
        {
            return string.Empty;
        }

        string word = string.IsNullOrWhiteSpace(label) ? "PROGRAMME" : label.Trim().ToUpperInvariant();
        bool feminine = word is "SÉRIE" or "ÉMISSION" or "SÉRIE ANIMÉE" or "FICTION" or "TÉLÉRÉALITÉ";
        if (level >= 18)
        {
            return $"{word} INTERDIT{(feminine ? "E" : "")} AUX MOINS DE 18 ANS";
        }

        return $"{word} DÉCONSEILLÉ{(feminine ? "E" : "")} AUX MOINS DE {level} ANS";
    }
}
