using System.Diagnostics;
using System.Numerics;

namespace ErsatzTV.Application.Nounours.Atelier;

/// <summary>
///     Énergie d'un son image par image : graves, médiums, aigus (0 → 1) et « temps forts » (attaques).
///     C'est ce qui fait réagir les rectangles au rythme de la musique.
/// </summary>
public sealed class AudioAnalysis
{
    public double DurationSeconds { get; init; }
    public float[] Bass { get; init; } = [];
    public float[] Mid { get; init; } = [];
    public float[] High { get; init; } = [];
    public bool[] Onset { get; init; } = [];

    public int Frames => Bass.Length;

    public float BassAt(int frame) => At(Bass, frame);
    public float MidAt(int frame) => At(Mid, frame);
    public float HighAt(int frame) => At(High, frame);
    public bool OnsetAt(int frame) => frame >= 0 && frame < Onset.Length && Onset[frame];

    /// <summary>La même analyse répétée jusqu'à <paramref name="seconds" /> (musique jouée en boucle).</summary>
    public AudioAnalysis Repeat(double seconds, int fps)
    {
        int frames = (int)Math.Ceiling(seconds * fps);
        if (Onset.Length == 0 || Onset.Length >= frames)
        {
            return this;
        }

        T[] Tile<T>(T[] source) => Enumerable.Range(0, frames).Select(i => source[i % source.Length]).ToArray();
        return new AudioAnalysis { DurationSeconds = seconds, Bass = Tile(Bass), Mid = Tile(Mid), High = Tile(High), Onset = Tile(Onset) };
    }

    /// <summary>Nombre de temps forts depuis le début (sert à faire « sauter » les rectangles).</summary>
    public int OnsetCount(int frame)
    {
        var count = 0;
        for (var i = 0; i <= Math.Min(frame, Onset.Length - 1); i++)
        {
            if (Onset[i])
            {
                count++;
            }
        }

        return count;
    }

    private static float At(float[] values, int frame) =>
        values.Length == 0 ? 0 : values[Math.Clamp(frame, 0, values.Length - 1)];

    /// <summary>Pas de son : une pulsation régulière à 120 BPM pour que l'animation vive quand même.</summary>
    public static AudioAnalysis Synthetic(double seconds, int fps)
    {
        int frames = Math.Max(1, (int)Math.Ceiling(seconds * fps));
        var bass = new float[frames];
        var mid = new float[frames];
        var high = new float[frames];
        var onset = new bool[frames];
        double beat = fps * 0.5; // 120 BPM
        for (var i = 0; i < frames; i++)
        {
            double phase = i % beat / beat;
            bass[i] = (float)Math.Pow(1 - phase, 2.2);
            mid[i] = (float)(0.45 + 0.35 * Math.Sin(i * 2 * Math.PI / (beat * 2)));
            high[i] = (float)(0.3 + 0.3 * Math.Sin(i * 2 * Math.PI / (beat / 2)));
            onset[i] = i % (int)Math.Round(beat) == 0;
        }

        return new AudioAnalysis { DurationSeconds = seconds, Bass = bass, Mid = mid, High = high, Onset = onset };
    }

    /// <summary>Décode le son avec FFmpeg (mono 22 050 Hz) et calcule les énergies par bande avec une FFT.</summary>
    public static async Task<AudioAnalysis> Analyze(
        string ffmpegPath,
        string audioPath,
        int fps,
        double startSeconds,
        double maxSeconds,
        CancellationToken cancellationToken)
    {
        const int sampleRate = 22050;
        const int window = 1024;

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in new[]
                 {
                     "-v", "error", "-ss", startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                     "-t", maxSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                     "-i", audioPath, "-vn", "-ac", "1", "-ar", sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-f", "f32le", "-"
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg ne démarre pas");
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
        using var memory = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(memory, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || memory.Length < 4)
        {
            throw new InvalidOperationException($"Impossible de lire le son : {(await errors).Trim()}");
        }

        byte[] bytes = memory.ToArray();
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);

        double duration = samples.Length / (double)sampleRate;
        int frames = Math.Max(1, (int)Math.Ceiling(duration * fps));
        var bass = new float[frames];
        var mid = new float[frames];
        var high = new float[frames];
        var buffer = new Complex[window];
        double binHz = sampleRate / (double)window;

        for (var f = 0; f < frames; f++)
        {
            int center = (int)(f / (double)fps * sampleRate);
            int start = center - window / 2;
            for (var i = 0; i < window; i++)
            {
                int index = start + i;
                double hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (window - 1));
                buffer[i] = new Complex(index >= 0 && index < samples.Length ? samples[index] * hann : 0, 0);
            }

            Fft(buffer);
            double b = 0, m = 0, h = 0;
            for (var k = 1; k < window / 2; k++)
            {
                double hz = k * binHz;
                double magnitude = buffer[k].Magnitude;
                if (hz < 160)
                {
                    b += magnitude;
                }
                else if (hz < 2000)
                {
                    m += magnitude;
                }
                else if (hz < 9000)
                {
                    h += magnitude;
                }
            }

            bass[f] = (float)b;
            mid[f] = (float)m;
            high[f] = (float)h;
        }

        Normalize(bass);
        Normalize(mid);
        Normalize(high);

        // temps forts : les graves dépassent nettement leur moyenne récente
        var onset = new bool[frames];
        int last = -100;
        int span = Math.Max(4, fps / 2);
        for (var f = 1; f < frames; f++)
        {
            double average = 0;
            int from = Math.Max(0, f - span);
            for (int i = from; i < f; i++)
            {
                average += bass[i];
            }

            average /= Math.Max(1, f - from);
            if (bass[f] > 0.35 && bass[f] > average * 1.35 && bass[f] >= bass[f - 1] && f - last >= fps / 6)
            {
                onset[f] = true;
                last = f;
            }
        }

        // lissage : montée rapide, descente douce (comme un vumètre)
        Smooth(bass, 0.85f, 0.18f);
        Smooth(mid, 0.7f, 0.15f);
        Smooth(high, 0.7f, 0.2f);

        return new AudioAnalysis { DurationSeconds = duration, Bass = bass, Mid = mid, High = high, Onset = onset };
    }

    private static void Normalize(float[] values)
    {
        if (values.Length == 0)
        {
            return;
        }

        float[] sorted = values.Order().ToArray();
        float reference = sorted[(int)Math.Clamp(sorted.Length * 0.95, 0, sorted.Length - 1)];
        if (reference <= 1e-6f)
        {
            Array.Clear(values);
            return;
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] = Math.Clamp(values[i] / reference, 0, 1);
        }
    }

    private static void Smooth(float[] values, float attack, float release)
    {
        float current = 0;
        for (var i = 0; i < values.Length; i++)
        {
            float target = values[i];
            current += (target - current) * (target > current ? attack : release);
            values[i] = current;
        }
    }

    private static void Fft(Complex[] data)
    {
        int n = data.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (data[i], data[j]) = (data[j], data[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            var wlen = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                for (var j = 0; j < len / 2; j++)
                {
                    Complex u = data[i + j];
                    Complex v = data[i + j + len / 2] * w;
                    data[i + j] = u + v;
                    data[i + j + len / 2] = u - v;
                    w *= wlen;
                }
            }
        }
    }
}
