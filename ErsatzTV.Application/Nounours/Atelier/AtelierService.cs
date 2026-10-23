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
        int frames = Math.Max(1, (int)Math.Ceiling(audio.DurationSeconds * Fps));
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
        var job = new AtelierJob { Name = $"{KindName(spec.Kind)} · {spec.Title}" };
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
        int frames = Math.Max(Fps, (int)Math.Ceiling(duration * Fps));

        string output = OutputPath(role, spec.Title);
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

        double fadeStart = Math.Max(0, duration - 0.4);
        args.AddRange(["-map", "0:v", "-map", "1:a", "-af", $"afade=t=out:st={fadeStart.ToString("0.###", Inv)}:d=0.4"]);
        args.AddRange(EncodeArgs());
        args.AddRange(["-shortest", temp]);

        job.Step = "Fabrication des images…";
        using var renderer = new RetroRenderer(spec.Style, audio, Fps, frames);
        await Pipe(ffmpeg, args, frames, (canvas, frame) => renderer.DrawJingle(canvas, spec, frame), p => job.Progress = p * 0.95, cancellationToken);

        await Finish(job, temp, output, role, cancellationToken);
    }

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
            // calque cadre + bandeaux posé sur les extraits
            string overlay = Path.Combine(work, "calque.png");
            using (var bitmap = new SKBitmap(RetroRenderer.Width, RetroRenderer.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
            {
                using (var canvas = new SKCanvas(bitmap))
                {
                    renderer.DrawTrailerOverlay(canvas, spec.Title, spec.DateLine);
                }

                using SKImage image = SKImage.FromBitmap(bitmap);
                using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
                await File.WriteAllBytesAsync(overlay, data.ToArray(), cancellationToken);
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
            string frameColor = "0x" + (spec.Style.FrameColor ?? "#F2EFE4").TrimStart('#')[..6];
            for (var i = 0; i < extracts.Count; i++)
            {
                TrailerExtract extract = extracts[i];
                job.Step = $"Extrait {i + 1} / {extracts.Count} : {extract.Title}";
                string part = Path.Combine(work, $"{i + 1:00}-extrait.mp4");
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
                        ? $"[0:a]aresample=48000,aformat=channel_layouts=stereo,afade=t=in:d=0.15,afade=t=out:st={fadeOut}:d=0.25[ao]"
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
                string music = $"[1:a]aresample=48000,aformat=channel_layouts=stereo,apad,atrim=0:{t},afade=t=out:st={fade}:d=1.5[m]";
                string filter = spec.KeepOriginalAudio
                    ? music + ";[0:a]volume=0.35[o];[m][o]amix=inputs=2:duration=first:normalize=0[a]"
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

            await Finish(job, temp, OutputPath(ImportRole.BandesAnnonces, spec.Title), ImportRole.BandesAnnonces, cancellationToken);
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
                Path = m.MediaVersions.SelectMany(v => v.MediaFiles).Select(f => f.Path).FirstOrDefault(),
                Duration = m.MediaVersions.Select(v => v.Duration).FirstOrDefault() })
            .Take(20).ToListAsync(cancellationToken);

        var episodes = await db.Episodes.AsNoTracking()
            .Where(e => e.EpisodeMetadata.Any(x => EF.Functions.Like(x.Title, pattern)) ||
                        e.Season.Show.ShowMetadata.Any(x => EF.Functions.Like(x.Title, pattern)))
            .Select(e => new { e.Id, Show = e.Season.Show.ShowMetadata.Select(x => x.Title).FirstOrDefault(), Season = e.Season.SeasonNumber,
                Number = e.EpisodeMetadata.Select(x => x.EpisodeNumber).FirstOrDefault(), Title = e.EpisodeMetadata.Select(x => x.Title).FirstOrDefault(),
                Path = e.MediaVersions.SelectMany(v => v.MediaFiles).Select(f => f.Path).FirstOrDefault(),
                Duration = e.MediaVersions.Select(v => v.Duration).FirstOrDefault() })
            .Take(30).ToListAsync(cancellationToken);

        var others = await db.OtherVideos.AsNoTracking()
            .Where(o => o.OtherVideoMetadata.Any(x => EF.Functions.Like(x.Title, pattern)))
            .Select(o => new { o.Id, Title = o.OtherVideoMetadata.Select(x => x.Title).FirstOrDefault(),
                Path = o.MediaVersions.SelectMany(v => v.MediaFiles).Select(f => f.Path).FirstOrDefault(),
                Duration = o.MediaVersions.Select(v => v.Duration).FirstOrDefault() })
            .Take(20).ToListAsync(cancellationToken);

        var result = new List<AtelierItem>();
        result.AddRange(movies.Select(m => new AtelierItem(m.Id, m.Title, $"Film{(m.Year.HasValue ? $" · {m.Year}" : "")} · {ContentCatalog.Format(m.Duration)}", m.Path, m.Duration.TotalSeconds)));
        result.AddRange(episodes.Select(e => new AtelierItem(e.Id, e.Show ?? e.Title, $"S{e.Season:00}E{e.Number:00} · {e.Title} · {ContentCatalog.Format(e.Duration)}", e.Path, e.Duration.TotalSeconds)));
        result.AddRange(others.Select(o => new AtelierItem(o.Id, o.Title, $"Vidéo · {ContentCatalog.Format(o.Duration)}", o.Path, o.Duration.TotalSeconds)));
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
            _ => "Annonce"
        };
}
