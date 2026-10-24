using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace ErsatzTV.Application.Nounours.Atelier;

/// <summary>
///     L'atelier vidéo : fabrique des jingles, génériques, cartons pub et bandes-annonces « Rétro 95 »
///     avec FFmpeg, puis les range dans les bibliothèques d'habillage (Jingles, Bandes-annonces, Pubs…)
///     pour qu'ils soient tout de suite utilisables dans les programmes.
/// </summary>
public sealed class AtelierService(
    IDbContextFactory<TvContext> dbContextFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<AtelierService> logger) : IDisposable
{
    public const int Fps = 25;
    public const string FilePrefix = "atelier-";
    public static readonly string[] AudioExtensions = [".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".opus", ".wma"];

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly ConcurrentDictionary<string, AtelierJob> _jobs = new();
    private readonly SemaphoreSlim _render = new(1, 1);

    public static string SourcesFolder => Path.Combine(FileSystemLayout.AtelierFolder, "sources");
    private static string WorkFolder => Path.Combine(FileSystemLayout.AtelierFolder, "travail");

    public IReadOnlyList<AtelierJob> Jobs => _jobs.Values.OrderByDescending(j => j.StartedAt).ToList();

    public AtelierJob GetJob(string id) => id is not null && _jobs.TryGetValue(id, out AtelierJob job) ? job : null;

    // ------------------------------------------------------------------ sons

    public void Dispose() => _render.Dispose();

    public static List<AtelierSource> Sources()
    {
        Directory.CreateDirectory(SourcesFolder);
        return new DirectoryInfo(SourcesFolder)
            .EnumerateFiles()
            .Where(f => AudioExtensions.Contains(f.Extension.ToLowerInvariant()))
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new AtelierSource(f.Name, f.FullName, f.Length))
            .ToList();
    }

    public static async Task<string> SaveSource(string fileName, Stream content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(SourcesFolder);
        string name = Path.GetFileName(fileName);
        string path = Path.Combine(SourcesFolder, name);
        await using FileStream output = File.Create(path);
        await content.CopyToAsync(output, cancellationToken);
        return name;
    }

    public static string SourcePath(string name) =>
        string.IsNullOrWhiteSpace(name) ? null : Path.Combine(SourcesFolder, Path.GetFileName(name));

    // ------------------------------------------------------------------ créations

    public static IEnumerable<ImportRole> OutputRoles => [ImportRole.Jingles, ImportRole.BandesAnnonces, ImportRole.Pubs, ImportRole.Habillage];

    public static List<AtelierFile> Creations()
    {
        var result = new List<AtelierFile>();
        foreach (ImportRole role in OutputRoles)
        {
            string folder = Path.Combine(FileSystemLayout.ImportsFolder, FolderImportService.Label(role));
            if (!Directory.Exists(folder))
            {
                continue;
            }

            result.AddRange(
                new DirectoryInfo(folder).EnumerateFiles($"{FilePrefix}*.mp4")
                    .Select(f => new AtelierFile(f.Name, f.FullName, role, f.Length, f.LastWriteTime)));
        }

        return result.OrderByDescending(f => f.Modified).ToList();
    }

    public static void DeleteCreation(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(Path.GetFullPath(FileSystemLayout.ImportsFolder), StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(full).StartsWith(FilePrefix, StringComparison.Ordinal))
        {
            File.Delete(full);
        }
    }

    // ------------------------------------------------------------------ aperçu (image fixe)

    /// <summary>Aperçu d'un jingle à l'instant <paramref name="seconds" />, en JPEG 960×540 (data URI).</summary>
    public async Task<string> PreviewJingle(JingleSpec spec, double seconds, CancellationToken cancellationToken)
    {
        double duration = Math.Clamp(spec.Duration <= 0 ? 6 : spec.Duration, 1, 120);
        AudioAnalysis audio = await AnalysisFor(spec.AudioFile, spec.AudioStart, duration, cancellationToken);
        double end = string.IsNullOrWhiteSpace(spec.EndMessage) ? 0 : Math.Clamp(spec.EndSeconds, 0.5, 10);
        int frames = Math.Max(1, (int)Math.Ceiling((audio.DurationSeconds + end) * Fps));
        using var renderer = new RetroRenderer(spec.Style, audio, Fps, frames);
        using var bitmap = new SKBitmap(960, 540, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Scale(0.5f);
            renderer.DrawJingle(canvas, spec, Math.Clamp((int)(seconds * Fps), 0, frames - 1));
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return "data:image/jpeg;base64," + Convert.ToBase64String(data.ToArray());
    }

    private readonly ConcurrentDictionary<string, AudioAnalysis> _analysisCache = new();

    private async Task<AudioAnalysis> AnalysisFor(string audioFile, double start, double maxSeconds, CancellationToken cancellationToken)
    {
        string path = SourcePath(audioFile);
        if (path is null || !File.Exists(path))
        {
            return AudioAnalysis.Synthetic(maxSeconds, Fps);
        }

        string key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{start.ToString(Inv)}|{maxSeconds.ToString(Inv)}";
        if (_analysisCache.TryGetValue(key, out AudioAnalysis cached))
        {
            return cached;
        }

        (string ffmpeg, _) = await Tools(cancellationToken);
        AudioAnalysis analysis = await AudioAnalysis.Analyze(ffmpeg, path, Fps, start, maxSeconds, cancellationToken);
        _analysisCache[key] = analysis;
        return analysis;
    }

    // ------------------------------------------------------------------ jingles

    public AtelierJob StartJingle(JingleSpec spec, ImportRole role)
    {
        string what = spec.Kind switch
        {
            AtelierKind.Jingle => $"Jingle {spec.Label} · {spec.Title}",
            AtelierKind.DebutPub or AtelierKind.FinPub when string.IsNullOrWhiteSpace(spec.Title) => KindName(spec.Kind),
            _ => $"{KindName(spec.Kind)} · {spec.Title}"
        };
        var job = new AtelierJob { Name = what };
        _jobs[job.Id] = job;
        _ = Task.Run(() => Run(job, ct => RenderJingle(job, spec, role, ct)));
        return job;
    }

    private async Task RenderJingle(AtelierJob job, JingleSpec spec, ImportRole role, CancellationToken cancellationToken)
    {
        (string ffmpeg, _) = await Tools(cancellationToken);
        string audioPath = SourcePath(spec.AudioFile);
        bool hasAudio = audioPath is not null && File.Exists(audioPath);
        double max = spec.Duration <= 0 ? 120 : Math.Clamp(spec.Duration, 1, 120);

        job.Step = hasAudio ? "Analyse du son…" : "Préparation…";
        AudioAnalysis audio = await AnalysisFor(spec.AudioFile, spec.AudioStart, max, cancellationToken);
        double duration = Math.Min(max, audio.DurationSeconds);
        double end = string.IsNullOrWhiteSpace(spec.EndMessage) ? 0 : Math.Clamp(spec.EndSeconds, 0.5, 10);
        double total = duration + end;
        int frames = Math.Max(Fps, (int)Math.Ceiling(total * Fps));

        string output = string.IsNullOrWhiteSpace(spec.OutputName)
            ? OutputPath(role, spec.Kind == AtelierKind.Jingle ? $"{spec.Label} {spec.Title}" : spec.Title)
            : Path.Combine(FileSystemLayout.ImportsFolder, FolderImportService.Label(role), FilePrefix + Path.GetFileName(spec.OutputName));
        string temp = TempPath(".mp4");

        var args = new List<string> { "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{RetroRenderer.Width}x{RetroRenderer.Height}", "-r", Fps.ToString(Inv), "-i", "-" };
        if (hasAudio)
        {
            args.AddRange(["-ss", spec.AudioStart.ToString("0.###", Inv), "-t", duration.ToString("0.###", Inv), "-i", audioPath]);
        }
        else
        {
            args.AddRange(["-f", "lavfi", "-t", duration.ToString("0.###", Inv), "-i", "anullsrc=r=48000:cl=stereo"]);
        }

        // la musique s'éteint à la fin du jingle ; le carton final (s'il y en a un) est silencieux
        double fadeStart = Math.Max(0, duration - 0.4);
        // niveau homogène d'un jingle à l'autre (−16 LUFS, crêtes sous −1,5 dB), quelle que soit la musique
        args.AddRange(["-map", "0:v", "-map", "1:a", "-af", $"aresample=48000,aformat=channel_layouts=stereo,{(hasAudio ? Loudness + "," : "")}afade=t=out:st={fadeStart.ToString("0.###", Inv)}:d=0.4,apad"]);
        args.AddRange(EncodeArgs());
        args.AddRange(["-t", total.ToString("0.###", Inv), temp]);

        job.Step = "Fabrication des images…";
        using (var renderer = new RetroRenderer(spec.Style, audio, Fps, frames))
        {
            await Pipe(ffmpeg, args, frames, (canvas, frame) => renderer.DrawJingle(canvas, spec, frame), p => job.Progress = p * 0.85, cancellationToken);
        }

        if (spec.Voice is { Engine: not VoiceEngine.None })
        {
            job.Step = "Voix off…";
            string voice = await MakeVoice(spec.Voice, cancellationToken);
            string mixed = TempPath(".mp4");
            await MixVoice(ffmpeg, temp, voice, spec.Voice, mixed, cancellationToken);
            File.Delete(temp);
            if (spec.Voice.Engine != VoiceEngine.File)
            {
                File.Delete(voice);
            }

            temp = mixed;
            job.Progress = 0.95;
        }

        await Finish(job, temp, output, role, cancellationToken);
    }

    // ------------------------------------------------------------------ habillage complet

    /// <summary>
    ///     Tous les jingles d'une chaîne d'un coup : un jingle par étiquette (FILM, SÉRIE…), les cartons
    ///     début / fin de pub et la signalétique. Noms de fichiers fixes : refaire le pack remplace les vidéos.
    /// </summary>
    public List<AtelierJob> StartPack(PackSpec pack)
    {
        // sans musique choisie : un indicatif différent par jingle (joué en entier, jusqu'à son coup final)
        List<string> labels = pack.Labels.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct().ToList();
        bool autoMusic = string.IsNullOrWhiteSpace(pack.Music);
        List<string> musics = autoMusic ? AtelierSounds.Pick("Indicatif", Math.Max(1, labels.Count), "Énergique", "Cinéma") : [pack.Music];
        bool autoPub = string.IsNullOrWhiteSpace(pack.PubMusic);
        List<string> pubMusics = autoPub ? AtelierSounds.Pick("Pub", 2, "Jeunesse", "Indicatif") : [pack.PubMusic, pack.PubMusic];
        string slug = HabillageService.Slugify(string.IsNullOrWhiteSpace(pack.ChannelName) ? "chaine" : pack.ChannelName);
        double seconds = Math.Clamp(pack.Seconds, 3, 30);
        var specs = new List<(JingleSpec Spec, ImportRole Role)>();

        int seed = pack.Style.Seed;
        for (var i = 0; i < labels.Count; i++)
        {
            string label = labels[i];
            AtelierStyle style = Clone(pack.Style);
            style.Seed = seed++; // une chorégraphie différente par jingle
            specs.Add((new JingleSpec
            {
                Kind = AtelierKind.Jingle, Label = label.Trim().ToUpperInvariant(), Title = pack.ChannelName, AudioFile = musics.Count == 0 ? string.Empty : musics[i % musics.Count],
                Duration = autoMusic ? 0 : seconds, Style = style, OutputName = $"pack-{slug}-{HabillageService.Slugify(label)}.mp4"
            }, ImportRole.Jingles));
        }

        if (pack.Pubs)
        {
            foreach (AtelierKind kind in new[] { AtelierKind.DebutPub, AtelierKind.FinPub })
            {
                specs.Add((new JingleSpec
                {
                    Kind = kind, Title = string.Empty, ChannelName = pack.ChannelName,
                    AudioFile = pubMusics.Count == 0 ? string.Empty : pubMusics[kind == AtelierKind.DebutPub ? 0 : pubMusics.Count - 1],
                    Duration = autoPub ? 0 : Math.Min(seconds, 5),
                    Style = Clone(pack.Style), OutputName = $"pack-{slug}-{(kind == AtelierKind.DebutPub ? "debut-pub" : "fin-pub")}.mp4"
                }, ImportRole.Pubs));
            }
        }

        if (pack.Ratings)
        {
            foreach (string label in new[] { "FILM", "SÉRIE" })
            {
                foreach (int level in new[] { 10, 12, 16, 18 })
                {
                    specs.Add((new JingleSpec
                    {
                        Kind = AtelierKind.Carton, Title = Signaletique.Message(level, label), Duration = 3, Style = Clone(pack.Style),
                        OutputName = $"pack-{slug}-signaletique-{HabillageService.Slugify(label)}-{level}.mp4"
                    }, ImportRole.Jingles));
                }
            }
        }

        return specs.Select(s => StartJingle(s.Spec, s.Role)).ToList();
    }

    private static AtelierStyle Clone(AtelierStyle style) =>
        System.Text.Json.JsonSerializer.Deserialize<AtelierStyle>(System.Text.Json.JsonSerializer.Serialize(style));

    // ------------------------------------------------------------------ bandes-annonces

    public AtelierJob StartTrailer(TrailerSpec spec)
    {
        var job = new AtelierJob { Name = $"Bande-annonce · {spec.Title}" };
        _jobs[job.Id] = job;
        _ = Task.Run(() => Run(job, ct => RenderTrailer(job, spec, ct)));
        return job;
    }

    private async Task RenderTrailer(AtelierJob job, TrailerSpec spec, CancellationToken cancellationToken)
    {
        (string ffmpeg, string ffprobe) = await Tools(cancellationToken);
        List<TrailerExtract> extracts = spec.Extracts.Where(e => File.Exists(e.Path) && e.Length > 0.3).ToList();
        if (extracts.Count == 0)
        {
            throw new InvalidOperationException("Aucun extrait lisible : choisissez au moins un programme dont le fichier est accessible.");
        }

        const double intro = 2.4;
        const double outro = 3.2;
        double total = intro + extracts.Sum(e => e.Length) + outro;
        string musicPath = SourcePath(spec.MusicFile);
        bool hasMusic = musicPath is not null && File.Exists(musicPath);

        job.Step = hasMusic ? "Analyse de la musique…" : "Préparation…";
        AudioAnalysis audio = hasMusic
            ? await AnalysisFor(spec.MusicFile, 0, total, cancellationToken)
            : AudioAnalysis.Synthetic(total, Fps);
        int totalFrames = (int)Math.Ceiling(total * Fps);
        using var renderer = new RetroRenderer(spec.Style, audio, Fps, totalFrames);

        string work = Path.Combine(WorkFolder, job.Id);
        Directory.CreateDirectory(work);
        try
        {
            // calque cadre + bandeaux posé sur les extraits (un par bandeau différent : bande-annonce de la semaine)
            var overlays = new Dictionary<string, string>();
            async Task<string> Overlay(TrailerExtract extract)
            {
                string title = string.IsNullOrWhiteSpace(extract.OverlayTitle) ? spec.Title : extract.OverlayTitle;
                string line = string.IsNullOrWhiteSpace(extract.OverlayLine) ? spec.DateLine : extract.OverlayLine;
                string key = title + "|" + line;
                if (overlays.TryGetValue(key, out string existing))
                {
                    return existing;
                }

                string file = Path.Combine(work, $"calque-{overlays.Count}.png");
                using (var bitmap = new SKBitmap(RetroRenderer.Width, RetroRenderer.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
                {
                    using (var canvas = new SKCanvas(bitmap))
                    {
                        renderer.DrawTrailerOverlay(canvas, title, line);
                    }

                    using SKImage image = SKImage.FromBitmap(bitmap);
                    using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
                    await File.WriteAllBytesAsync(file, data.ToArray(), cancellationToken);
                }

                overlays[key] = file;
                return file;
            }

            var parts = new List<string>();
            double done = 0;
            void Advance(double seconds) => job.Progress = Math.Min(0.9, (done + seconds) / total * 0.9);

            // ouverture : étiquette + titre sur les rectangles
            job.Step = "Ouverture…";
            string introFile = Path.Combine(work, "00-ouverture.mp4");
            await RenderCard(ffmpeg, introFile, intro, 0, renderer, (canvas, frame, p) =>
            {
                canvas.Clear(SKColors.Black);
                renderer.DrawBlocks(canvas, frame);
                SKRect w = renderer.Window;
                renderer.DrawBar(canvas, spec.Label, w.Left, w.Top + w.Height * 0.38f, w.Height * 0.16f, w.Width * 0.75f, p * 6);
                renderer.DrawBar(canvas, spec.Title, w.Left, w.Bottom - w.Height * 0.22f, w.Height * 0.1f, w.Width * 0.9f, (p - 0.35f) * 6);
                renderer.DrawFrameBorder(canvas);
            }, s => Advance(s * intro), cancellationToken);
            parts.Add(introFile);
            done += intro;

            // les extraits dans le cadre, avec le bandeau titre / date
            SKRect window = renderer.Window;
            int ww = (int)window.Width / 2 * 2;
            int wh = (int)window.Height / 2 * 2;
            int wx = (int)window.Left;
            int wy = (int)window.Top;
            string frameColor = "0x" + (spec.Style.FrameColor ?? "#E1E8CD").TrimStart('#')[..6];
            for (var i = 0; i < extracts.Count; i++)
            {
                TrailerExtract extract = extracts[i];
                job.Step = $"Extrait {i + 1} / {extracts.Count} : {extract.Title}";
                string part = Path.Combine(work, $"{i + 1:00}-extrait.mp4");
                string overlay = await Overlay(extract);
                bool audioStream = await HasAudio(ffprobe, extract.Path, cancellationToken);
                string len = extract.Length.ToString("0.###", Inv);
                string fadeOut = Math.Max(0, extract.Length - 0.25).ToString("0.###", Inv);
                var args = new List<string>
                {
                    "-y", "-v", "error",
                    "-ss", extract.Start.ToString("0.###", Inv), "-t", len, "-i", extract.Path,
                    "-loop", "1", "-t", len, "-i", overlay,
                    "-f", "lavfi", "-t", len, "-i", "anullsrc=r=48000:cl=stereo",
                    "-filter_complex",
                    $"[0:v]scale={ww}:{wh}:force_original_aspect_ratio=increase,crop={ww}:{wh},setsar=1,fps={Fps},"
                    + $"pad={RetroRenderer.Width}:{RetroRenderer.Height}:{wx}:{wy}:color={frameColor}[v];"
                    + "[v][1:v]overlay=0:0:shortest=1,format=yuv420p[vo];"
                    + (audioStream
                        ? $"[0:a]aresample=48000,aformat=channel_layouts=stereo,{Loudness},afade=t=in:d=0.15,afade=t=out:st={fadeOut}:d=0.25[ao]"
                        : "[2:a]anull[ao]"),
                    "-map", "[vo]", "-map", "[ao]", "-t", len
                };
                args.AddRange(EncodeArgs());
                args.Add(part);
                await RunFfmpeg(ffmpeg, args, cancellationToken);
                parts.Add(part);
                done += extract.Length;
                Advance(0);
            }

            // fermeture : titre + date + chaîne
            job.Step = "Fermeture…";
            string outroFile = Path.Combine(work, "99-fermeture.mp4");
            int outroOffset = (int)(done * Fps);
            await RenderCard(ffmpeg, outroFile, outro, outroOffset, renderer, (canvas, frame, p) =>
            {
                canvas.Clear(SKColors.Black);
                renderer.DrawBlocks(canvas, frame, Math.Clamp((1 - p) * 4, 0.35f, 1));
                SKRect w = renderer.Window;
                renderer.DrawBar(canvas, spec.Label, w.Left, w.Top, w.Height * 0.1f, w.Width * 0.6f, p * 8);
                renderer.DrawBar(canvas, spec.Title, w.Left, w.Top + w.Height * 0.55f, w.Height * 0.12f, w.Width * 0.9f, (p - 0.1f) * 8);
                renderer.DrawBar(canvas, spec.DateLine, w.Left, w.Top + w.Height * 0.55f + w.Height * 0.13f, w.Height * 0.08f, w.Width * 0.8f, (p - 0.2f) * 8, false);
                renderer.DrawFrameBorder(canvas);
                if (!string.IsNullOrWhiteSpace(spec.ChannelName))
                {
                    renderer.DrawBar(canvas, spec.ChannelName, w.Left, w.Bottom - w.Height * 0.09f, w.Height * 0.075f, w.Width * 0.4f, (p - 0.45f) * 8);
                }
            }, s => Advance(s * outro), cancellationToken);
            parts.Add(outroFile);

            // assemblage
            job.Step = "Assemblage…";
            string list = Path.Combine(work, "liste.txt");
            await File.WriteAllLinesAsync(list, parts.Select(p => $"file '{p.Replace("'", "'\\''", StringComparison.Ordinal)}'"), cancellationToken);
            string joined = Path.Combine(work, "assemble.mp4");
            await RunFfmpeg(ffmpeg, ["-y", "-v", "error", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", joined], cancellationToken);

            string temp = TempPath(".mp4");
            if (hasMusic)
            {
                job.Step = "Mixage de la musique…";
                string t = total.ToString("0.###", Inv);
                string fade = Math.Max(0, total - 1.5).ToString("0.###", Inv);
                // la musique plein pot à l'ouverture et à la fermeture ; sous les extraits, elle s'efface
                // (fond discret si on garde leur son, sinon simplement un peu plus bas)
                double duck = spec.KeepOriginalAudio ? 0.16 : 0.7;
                string a = intro.ToString("0.###", Inv);
                string b = (total - outro).ToString("0.###", Inv);
                string envelope = $"volume='1-{(1 - duck).ToString("0.###", Inv)}*clip(min((t-{a})/0.35,({b}-t)/0.35),0,1)':eval=frame";
                string music = $"[1:a]aresample=48000,aformat=channel_layouts=stereo,{Loudness},apad,atrim=0:{t},{envelope},afade=t=out:st={fade}:d=1.5[m]";
                string filter = spec.KeepOriginalAudio
                    ? music + ";[0:a]anull[o];[m][o]amix=inputs=2:duration=first:normalize=0,alimiter=limit=0.89[a]"
                    : music + ";[m]anull[a]";
                await RunFfmpeg(
                    ffmpeg,
                    ["-y", "-v", "error", "-i", joined, "-i", musicPath, "-filter_complex", filter, "-map", "0:v", "-map", "[a]",
                        "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2", "-movflags", "+faststart", temp],
                    cancellationToken);
            }
            else
            {
                File.Move(joined, temp, true);
            }

            string output = string.IsNullOrWhiteSpace(spec.OutputName)
                ? OutputPath(ImportRole.BandesAnnonces, spec.Title)
                : Path.Combine(FileSystemLayout.ImportsFolder, FolderImportService.Label(ImportRole.BandesAnnonces), FilePrefix + Path.GetFileName(spec.OutputName));
            await Finish(job, temp, output, ImportRole.BandesAnnonces, cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(work, true);
            }
            catch (IOException)
            {
                // fichiers encore ouverts : nettoyés au prochain démarrage
            }
        }
    }

    // ------------------------------------------------------------------ carton « Ce soir »

    public const string TonightFile = FilePrefix + "ce-soir.mp4";

    /// <summary>Fabrique (ou refait) le carton « Ce soir » d'après le guide ; toujours le même fichier, donc la même vidéo dans les programmes.</summary>
    public AtelierJob StartTonight(TonightSettings settings, IReadOnlyList<(string Time, string Title, string Channel)> rows)
    {
        var job = new AtelierJob { Name = "Carton « Ce soir »" };
        _jobs[job.Id] = job;
        _ = Task.Run(() => Run(job, ct => RenderTonight(job, settings, rows, ct)));
        return job;
    }

    private async Task RenderTonight(AtelierJob job, TonightSettings settings, IReadOnlyList<(string Time, string Title, string Channel)> rows, CancellationToken cancellationToken)
    {
        (string ffmpeg, _) = await Tools(cancellationToken);
        double seconds = Math.Clamp(settings.Seconds, 4, 60);
        string music = SourcePath(settings.Music);
        bool hasMusic = music is not null && File.Exists(music);
        AudioAnalysis audio = await AnalysisFor(settings.Music, 0, seconds, cancellationToken);
        int frames = (int)Math.Ceiling(seconds * Fps);
        string temp = TempPath(".mp4");
        var args = new List<string> { "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{RetroRenderer.Width}x{RetroRenderer.Height}", "-r", Fps.ToString(Inv), "-i", "-" };
        args.AddRange(hasMusic
            ? ["-t", seconds.ToString("0.###", Inv), "-i", music]
            : ["-f", "lavfi", "-t", seconds.ToString("0.###", Inv), "-i", "anullsrc=r=48000:cl=stereo"]);
        args.AddRange(["-map", "0:v", "-map", "1:a", "-af", $"aresample=48000,aformat=channel_layouts=stereo,{(hasMusic ? Loudness + "," : "")}apad,afade=t=out:st={Math.Max(0, seconds - 1).ToString("0.###", Inv)}:d=1"]);
        args.AddRange(EncodeArgs());
        args.AddRange(["-t", seconds.ToString("0.###", Inv), temp]);

        job.Step = "Fabrication des images…";
        var style = new AtelierStyle();
        using (var renderer = new RetroRenderer(style, audio, Fps, frames))
        {
            List<(string, string, string)> shown = rows.Take(6).ToList();
            await Pipe(ffmpeg, args, frames, (canvas, frame) => renderer.DrawTonight(canvas, frame, settings.Title, shown, settings.ChannelName), p => job.Progress = p * 0.9, cancellationToken);
        }

        string output = Path.Combine(FileSystemLayout.ImportsFolder, FolderImportService.Label(ImportRole.BandesAnnonces), TonightFile);
        await Finish(job, temp, output, ImportRole.BandesAnnonces, cancellationToken);
    }

    // ------------------------------------------------------------------ météo et interlude

    public const string WeatherFile = FilePrefix + "meteo.mp4";
    public const string InterludeFile = FilePrefix + "interlude.mp4";

    /// <summary>Carte météo (toujours le même fichier, rangé dans « Bandes-annonces »).</summary>
    public AtelierJob StartWeather(WeatherSettings settings, IReadOnlyList<WeatherRow> rows)
    {
        var job = new AtelierJob { Name = "Carte météo" };
        _jobs[job.Id] = job;
        string output = Path.Combine(FileSystemLayout.ImportsFolder, FolderImportService.Label(ImportRole.BandesAnnonces), WeatherFile);
        List<WeatherRow> shown = rows.Take(6).ToList();
        _ = Task.Run(() => Run(job, ct => RenderAnimated(
            job, Math.Clamp(settings.Seconds, 4, 60), settings.Music, new AtelierStyle(), output, ImportRole.BandesAnnonces,
            (r, canvas, frame) => r.DrawWeather(canvas, frame, settings.Title, shown, settings.ChannelName), ct)));
        return job;
    }

    /// <summary>Interlude en boucle (rangé dans « Habillage vidéo ») : ce qui passe quand la chaîne n'a rien à diffuser.</summary>
    public AtelierJob StartInterlude(string label, string channelName, string music, double seconds, AtelierStyle style)
    {
        var job = new AtelierJob { Name = "Interlude" };
        _jobs[job.Id] = job;
        string output = Path.Combine(FileSystemLayout.ImportsFolder, FolderImportService.Label(ImportRole.Habillage), InterludeFile);
        _ = Task.Run(() => Run(job, ct => RenderAnimated(
            job, Math.Clamp(seconds, 10, 300), music, style, output, ImportRole.Habillage,
            (r, canvas, frame) => r.DrawInterlude(canvas, frame, string.IsNullOrWhiteSpace(label) ? "INTERLUDE" : label, channelName), ct)));
        return job;
    }

    // ------------------------------------------------------------------ l'essentiel du jour

    public const string EssentialFile = FilePrefix + "essentiel-du-jour.mp4";

    /// <summary>
    ///     « L'essentiel du jour » : un titre de l'info par écran, en quadrillage, lu par la voix off si elle est choisie
    ///     (chaque titre reste à l'écran le temps d'être lu). Toujours le même fichier, rangé dans « Bandes-annonces ».
    /// </summary>
    public AtelierJob StartEssential(EssentialSettings settings, IReadOnlyList<string> headlines)
    {
        var job = new AtelierJob { Name = "L'essentiel du jour" };
        _jobs[job.Id] = job;
        _ = Task.Run(() => Run(job, ct => RenderEssential(job, settings, headlines.ToList(), ct)));
        return job;
    }

    private async Task RenderEssential(AtelierJob job, EssentialSettings settings, List<string> headlines, CancellationToken cancellationToken)
    {
        (_, string ffprobe) = await Tools(cancellationToken);
        CultureInfo fr = CultureInfo.GetCultureInfo("fr-FR");
        string dateLine = DateTime.Now.ToString("dddd d MMMM", fr);
        const string Opening = "L'essentiel du jour.";
        var starts = new List<double>();
        double seconds;
        string voiceFile = null;
        VoiceOver voice = null;
        if (settings.Voice is VoiceEngine.Windows or VoiceEngine.Server)
        {
            job.Step = "Voix off…";
            List<string> spoken = headlines.Select(Spoken).ToList();
            voice = new VoiceOver { Engine = settings.Voice, Text = Opening + " " + string.Join(" ", spoken), Start = 0.6, Duck = true };
            voiceFile = await MakeVoice(voice, cancellationToken);
            double length = await MediaSeconds(ffprobe, voiceFile, cancellationToken);

            // chaque titre arrive quand la voix commence à le lire (au prorata du texte)
            double perChar = length / Math.Max(1, voice.Text.Length);
            double at = voice.Start + (Opening.Length + 1) * perChar;
            foreach (string line in spoken)
            {
                starts.Add(Math.Max(2.2, at - 0.2));
                at += (line.Length + 1) * perChar;
            }

            seconds = voice.Start + length + 1.6;
        }
        else
        {
            double each = Math.Clamp(settings.Seconds, 3, 20);
            for (var i = 0; i < headlines.Count; i++)
            {
                starts.Add(3 + i * each);
            }

            seconds = 3 + headlines.Count * each + 0.8;
        }

        string output = Path.Combine(FileSystemLayout.ImportsFolder, FolderImportService.Label(ImportRole.BandesAnnonces), EssentialFile);
        string title = string.IsNullOrWhiteSpace(settings.Title) ? "L'ESSENTIEL" : settings.Title;
        await RenderAnimated(
            job, Math.Min(seconds, 300), settings.Music, new AtelierStyle(), output, ImportRole.BandesAnnonces,
            (r, canvas, frame) => r.DrawEssential(canvas, frame, title, dateLine, headlines, starts, settings.ChannelName),
            cancellationToken, voiceFile, voice);
    }

    /// <summary>Un titre prêt à être lu : « NSN : … » devient « NSN. … », avec un point final.</summary>
    private static string Spoken(string headline)
    {
        string text = headline.Trim().Replace(" : ", ". ", StringComparison.Ordinal);
        return text.Length > 0 && ".!?…".Contains(text[^1], StringComparison.Ordinal) ? text : text + ".";
    }

    private static async Task<double> MediaSeconds(string ffprobe, string path, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(ffprobe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0
            ? value
            : throw new InvalidOperationException("Voix off illisible (durée inconnue).");
    }

    /// <summary>Une vidéo animée au rythme d'une musique (ou d'une pulsation), dessinée image par image.</summary>
    private async Task RenderAnimated(
        AtelierJob job,
        double seconds,
        string musicFile,
        AtelierStyle style,
        string output,
        ImportRole role,
        Action<RetroRenderer, SKCanvas, int> draw,
        CancellationToken cancellationToken,
        string voiceFile = null,
        VoiceOver voice = null)
    {
        (string ffmpeg, _) = await Tools(cancellationToken);
        string music = SourcePath(musicFile);
        bool hasMusic = music is not null && File.Exists(music);
        job.Step = hasMusic ? "Analyse de la musique…" : "Préparation…";
        AudioAnalysis audio = (await AnalysisFor(musicFile, 0, seconds, cancellationToken)).Repeat(seconds, Fps);
        int frames = (int)Math.Ceiling(seconds * Fps);
        string temp = TempPath(".mp4");
        var args = new List<string> { "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{RetroRenderer.Width}x{RetroRenderer.Height}", "-r", Fps.ToString(Inv), "-i", "-" };
        args.AddRange(hasMusic
            ? ["-stream_loop", "-1", "-t", seconds.ToString("0.###", Inv), "-i", music]
            : ["-f", "lavfi", "-t", seconds.ToString("0.###", Inv), "-i", "anullsrc=r=48000:cl=stereo"]);
        args.AddRange(["-map", "0:v", "-map", "1:a", "-af", $"aresample=48000,aformat=channel_layouts=stereo,{(hasMusic ? Loudness + "," : "")}apad,afade=t=out:st={Math.Max(0, seconds - 1).ToString("0.###", Inv)}:d=1"]);
        args.AddRange(EncodeArgs());
        args.AddRange(["-t", seconds.ToString("0.###", Inv), temp]);

        job.Step = "Fabrication des images…";
        using (var renderer = new RetroRenderer(style, audio, Fps, frames))
        {
            await Pipe(ffmpeg, args, frames, (canvas, frame) => draw(renderer, canvas, frame), p => job.Progress = p * 0.9, cancellationToken);
        }

        if (voiceFile is not null && voice is not null)
        {
            job.Step = "Mixage de la voix off…";
            string mixed = TempPath(".mp4");
            await MixVoice(ffmpeg, temp, voiceFile, voice, mixed, cancellationToken);
            File.Delete(temp);
            File.Delete(voiceFile);
            temp = mixed;
        }

        await Finish(job, temp, output, role, cancellationToken);
    }

    // ------------------------------------------------------------------ programmes (pour les extraits)

    public async Task<List<AtelierItem>> SearchItems(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 2)
        {
            return [];
        }

        string pattern = "%" + text.Trim().Replace("%", string.Empty, StringComparison.Ordinal) + "%";
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var movies = await db.Movies.AsNoTracking()
            .Where(m => m.MovieMetadata.Any(x => EF.Functions.Like(x.Title, pattern)))
            .Select(m => new { m.Id, Title = m.MovieMetadata.Select(x => x.Title).FirstOrDefault(), Year = m.MovieMetadata.Select(x => x.Year).FirstOrDefault(),
                Rating = m.MovieMetadata.Select(x => x.ContentRating).FirstOrDefault(),
                Path = m.MediaVersions.SelectMany(v => v.MediaFiles).Select(f => f.Path).FirstOrDefault(),
                Duration = m.MediaVersions.Select(v => v.Duration).FirstOrDefault() })
            .Take(20).ToListAsync(cancellationToken);

        var episodes = await db.Episodes.AsNoTracking()
            .Where(e => e.EpisodeMetadata.Any(x => EF.Functions.Like(x.Title, pattern)) ||
                        e.Season.Show.ShowMetadata.Any(x => EF.Functions.Like(x.Title, pattern)))
            .Select(e => new { e.Id, Show = e.Season.Show.ShowMetadata.Select(x => x.Title).FirstOrDefault(), Season = e.Season.SeasonNumber,
                Rating = e.Season.Show.ShowMetadata.Select(x => x.ContentRating).FirstOrDefault(),
                Number = e.EpisodeMetadata.Select(x => x.EpisodeNumber).FirstOrDefault(), Title = e.EpisodeMetadata.Select(x => x.Title).FirstOrDefault(),
                Path = e.MediaVersions.SelectMany(v => v.MediaFiles).Select(f => f.Path).FirstOrDefault(),
                Duration = e.MediaVersions.Select(v => v.Duration).FirstOrDefault() })
            .Take(30).ToListAsync(cancellationToken);

        var others = await db.OtherVideos.AsNoTracking()
            .Where(o => o.OtherVideoMetadata.Any(x => EF.Functions.Like(x.Title, pattern)))
            .Select(o => new { o.Id, Title = o.OtherVideoMetadata.Select(x => x.Title).FirstOrDefault(),
                Rating = o.OtherVideoMetadata.Select(x => x.ContentRating).FirstOrDefault(),
                Path = o.MediaVersions.SelectMany(v => v.MediaFiles).Select(f => f.Path).FirstOrDefault(),
                Duration = o.MediaVersions.Select(v => v.Duration).FirstOrDefault() })
            .Take(20).ToListAsync(cancellationToken);

        var result = new List<AtelierItem>();
        result.AddRange(movies.Select(m => new AtelierItem(m.Id, m.Title, $"Film{(m.Year.HasValue ? $" · {m.Year}" : "")} · {ContentCatalog.Format(m.Duration)}", m.Path, m.Duration.TotalSeconds, "Film", m.Rating ?? "")));
        result.AddRange(episodes.Select(e => new AtelierItem(e.Id, e.Show ?? e.Title, $"S{e.Season:00}E{e.Number:00} · {e.Title} · {ContentCatalog.Format(e.Duration)}", e.Path, e.Duration.TotalSeconds, "Série", e.Rating ?? "")));
        result.AddRange(others.Select(o => new AtelierItem(o.Id, o.Title, $"Vidéo · {ContentCatalog.Format(o.Duration)}", o.Path, o.Duration.TotalSeconds, "Vidéo", o.Rating ?? "")));
        return result.Where(r => !string.IsNullOrWhiteSpace(r.Path)).ToList();
    }

    /// <summary>Image d'un programme à l'instant t (JPEG, 320 px de large) pour choisir les extraits.</summary>
    public async Task<byte[]> Thumbnail(string path, double seconds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return [];
        }

        (string ffmpeg, _) = await Tools(cancellationToken);
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-v", "error", "-ss", seconds.ToString("0.###", Inv), "-i", path, "-frames:v", "1", "-vf", "scale=320:-2", "-f", "image2", "-c:v", "mjpeg", "-" })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        _ = process.StandardError.ReadToEndAsync(cancellationToken);
        using var memory = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(memory, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return memory.ToArray();
    }

    /// <summary>Le chemin d'un fichier de programme (pour les vignettes), seulement s'il est dans la médiathèque.</summary>
    public async Task<string> PathOf(int mediaItemId, CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Movies.Where(m => m.Id == mediaItemId).SelectMany(m => m.MediaVersions).SelectMany(v => v.MediaFiles).Select(f => f.Path).FirstOrDefaultAsync(cancellationToken)
               ?? await db.Episodes.Where(m => m.Id == mediaItemId).SelectMany(m => m.MediaVersions).SelectMany(v => v.MediaFiles).Select(f => f.Path).FirstOrDefaultAsync(cancellationToken)
               ?? await db.OtherVideos.Where(m => m.Id == mediaItemId).SelectMany(m => m.MediaVersions).SelectMany(v => v.MediaFiles).Select(f => f.Path).FirstOrDefaultAsync(cancellationToken)
               ?? await db.MusicVideos.Where(m => m.Id == mediaItemId).SelectMany(m => m.MediaVersions).SelectMany(v => v.MediaFiles).Select(f => f.Path).FirstOrDefaultAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ voix off

    /// <summary>Fabrique la voix off (WAV) : voix de Windows, serveur de synthèse, ou fichier existant.</summary>
    public static async Task<string> MakeVoice(VoiceOver voice, CancellationToken cancellationToken)
    {
        switch (voice.Engine)
        {
            case VoiceEngine.File:
            {
                string path = SourcePath(voice.File);
                return path is not null && File.Exists(path) ? path : throw new InvalidOperationException("Fichier de voix off introuvable.");
            }

            case VoiceEngine.Windows:
            {
                if (!OperatingSystem.IsWindows())
                {
                    throw new InvalidOperationException("La voix de Windows n'est disponible que sous Windows : choisissez un serveur de synthèse ou un fichier.");
                }

                string text = TempPath(".txt");
                string wav = TempPath(".wav");
                await File.WriteAllTextAsync(text, voice.Text ?? string.Empty, Encoding.UTF8, cancellationToken);
                string script =
                    "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; "
                    + "$v = $s.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture.Name -like 'fr*' } | Select-Object -First 1; "
                    + "if ($v) { $s.SelectVoice($v.VoiceInfo.Name) }; "
                    + $"$s.SetOutputToWaveFile('{wav.Replace("'", "''", StringComparison.Ordinal)}'); "
                    + $"$s.Speak([IO.File]::ReadAllText('{text.Replace("'", "''", StringComparison.Ordinal)}', [Text.Encoding]::UTF8)); $s.Dispose()";
                var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(script);
                using var process = Process.Start(psi)!;
                string errors = await process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                File.Delete(text);
                return File.Exists(wav) && new FileInfo(wav).Length > 100
                    ? wav
                    : throw new InvalidOperationException($"Synthèse vocale Windows impossible : {errors.Trim()}");
            }

            case VoiceEngine.Server:
            {
                NounoursSettings settings = NounoursSettings.Load();
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                if (!string.IsNullOrWhiteSpace(settings.Voice.ApiKey))
                {
                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.Voice.ApiKey);
                }

                var body = new
                {
                    model = settings.Voice.Model,
                    input = voice.Text ?? string.Empty,
                    voice = settings.Voice.VoiceName,
                    response_format = "wav"
                };
                using HttpResponseMessage response = await http.PostAsync(
                    settings.Voice.Url,
                    new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Serveur de synthèse : {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken)}");
                }

                string wav = TempPath(".wav");
                await File.WriteAllBytesAsync(wav, await response.Content.ReadAsByteArrayAsync(cancellationToken), cancellationToken);
                return wav;
            }

            default:
                throw new InvalidOperationException("Pas de voix off demandée.");
        }
    }

    /// <summary>Pose la voix sur la vidéo ; la musique baisse automatiquement quand la voix parle.</summary>
    private static async Task MixVoice(string ffmpeg, string video, string voice, VoiceOver settings, string output, CancellationToken cancellationToken)
    {
        int delay = (int)(Math.Max(0, settings.Start) * 1000);
        string voiceChain = $"[1:a]aresample=48000,aformat=channel_layouts=stereo,adelay={delay}|{delay},volume=1.5";
        string filter = settings.Duck
            ? voiceChain + ",asplit=2[v1][v2];[0:a][v1]sidechaincompress=threshold=0.02:ratio=8:attack=30:release=500[m];[m][v2]amix=inputs=2:duration=first:normalize=0[a]"
            : voiceChain + "[v];[0:a][v]amix=inputs=2:duration=first:normalize=0[a]";
        await RunFfmpeg(
            ffmpeg,
            ["-y", "-v", "error", "-i", video, "-i", voice, "-filter_complex", filter, "-map", "0:v", "-map", "[a]",
                "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2", "-movflags", "+faststart", output],
            cancellationToken);
    }

    // ------------------------------------------------------------------ plomberie FFmpeg

    private async Task Run(AtelierJob job, Func<CancellationToken, Task> work)
    {
        await _render.WaitAsync();
        try
        {
            await work(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Atelier : échec de {Job}", job.Name);
            job.State = AtelierJobState.Failed;
            job.Error = ex.Message;
            job.Step = "Échec";
        }
        finally
        {
            _render.Release();
        }
    }

    private async Task Finish(AtelierJob job, string temp, string output, ImportRole role, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.Move(temp, output, true);
        job.Output = output;

        // rangé dans la bibliothèque d'habillage correspondante (créée au besoin) puis analysé
        job.Step = "Ajout à la médiathèque…";
        using IServiceScope scope = scopeFactory.CreateScope();
        IFolderImportService import = scope.ServiceProvider.GetRequiredService<IFolderImportService>();
        await import.Import(Path.GetDirectoryName(output), role, cancellationToken);

        job.Progress = 1;
        job.State = AtelierJobState.Done;
        job.Step = $"Terminé : rangé dans « {FolderImportService.Label(role)} »";
    }

    private static async Task RenderCard(
        string ffmpeg,
        string output,
        double seconds,
        int frameOffset,
        RetroRenderer renderer,
        Action<SKCanvas, int, float> draw,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        int frames = (int)Math.Ceiling(seconds * Fps);
        var args = new List<string>
        {
            "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{RetroRenderer.Width}x{RetroRenderer.Height}",
            "-r", Fps.ToString(Inv), "-i", "-", "-f", "lavfi", "-t", seconds.ToString("0.###", Inv), "-i", "anullsrc=r=48000:cl=stereo",
            "-map", "0:v", "-map", "1:a"
        };
        args.AddRange(EncodeArgs());
        args.AddRange(["-shortest", output]);
        await Pipe(ffmpeg, args, frames, (canvas, frame) => draw(canvas, frameOffset + frame, frame / (float)Math.Max(1, frames - 1)), progress, cancellationToken);
    }

    /// <summary>Normalisation du volume (EBU R128) : tous les sons de l'atelier au même niveau perçu.</summary>
    private const string Loudness = "loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000";

    private static IEnumerable<string> EncodeArgs() =>
    [
        "-c:v", "libx264", "-preset", "veryfast", "-crf", "19", "-pix_fmt", "yuv420p", "-r", Fps.ToString(Inv),
        "-video_track_timescale", "12800", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2", "-movflags", "+faststart"
    ];

    private static async Task Pipe(
        string ffmpeg,
        List<string> args,
        int frames,
        Action<SKCanvas, int> draw,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg ne démarre pas");
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
        using var bitmap = new SKBitmap(RetroRenderer.Width, RetroRenderer.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        Stream stdin = process.StandardInput.BaseStream;
        try
        {
            for (var i = 0; i < frames; i++)
            {
                canvas.Save();
                draw(canvas, i);
                canvas.Restore();
                canvas.Flush();
                await stdin.WriteAsync(bitmap.GetPixelSpan().ToArray(), cancellationToken);
                progress(i / (double)frames);
            }
        }
        catch (IOException)
        {
            // FFmpeg s'est arrêté : l'erreur est lue ci-dessous
        }
        finally
        {
            stdin.Close();
        }

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg : {(await errors).Trim()}");
        }
    }

    private static async Task RunFfmpeg(string ffmpeg, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg ne démarre pas");
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg : {(await errors).Trim()}");
        }
    }

    private static async Task<bool> HasAudio(string ffprobe, string path, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(ffprobe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", path })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace(output);
    }

    private async Task<(string FFmpeg, string FFprobe)> Tools(CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        string ffmpegKey = ConfigElementKey.FFmpegPath.Key;
        string ffprobeKey = ConfigElementKey.FFprobePath.Key;
        string ffmpeg = await db.ConfigElements.Where(c => c.Key == ffmpegKey).Select(c => c.Value).FirstOrDefaultAsync(cancellationToken);
        string ffprobe = await db.ConfigElements.Where(c => c.Key == ffprobeKey).Select(c => c.Value).FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            throw new InvalidOperationException("FFmpeg n'est pas configuré (Réglages › FFmpeg).");
        }

        return (ffmpeg, string.IsNullOrWhiteSpace(ffprobe) ? ffmpeg.Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase) : ffprobe);
    }

    private static string OutputPath(ImportRole role, string title)
    {
        string folder = Path.Combine(FileSystemLayout.ImportsFolder, FolderImportService.Label(role));
        string slug = HabillageService.Slugify(string.IsNullOrWhiteSpace(title) ? "sans-titre" : title);
        return Path.Combine(folder, $"{FilePrefix}{slug}-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
    }

    private static string TempPath(string extension)
    {
        Directory.CreateDirectory(WorkFolder);
        return Path.Combine(WorkFolder, Guid.NewGuid().ToString("N") + extension);
    }

    public static string KindName(AtelierKind kind) =>
        kind switch
        {
            AtelierKind.Jingle => "Jingle",
            AtelierKind.Generique => "Générique",
            AtelierKind.DebutPub => "Début de pub",
            AtelierKind.FinPub => "Fin de pub",
            AtelierKind.Carton => "Carton",
            _ => "Annonce"
        };
}
