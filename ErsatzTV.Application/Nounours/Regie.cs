using System.Text.Json;
using ErsatzTV.Application.Channels;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Nounours;

public enum RegieMode
{
    /// <summary>La vidéo une fois, puis retour automatique à la grille.</summary>
    Once,

    /// <summary>La vidéo en boucle pendant X minutes, puis retour automatique.</summary>
    Minutes,

    /// <summary>En boucle jusqu'à « Rendre l'antenne » (6 h maximum).</summary>
    UntilRelease
}

public sealed record RegieSource(int MediaItemId, string Title, string Detail, TimeSpan Duration, bool IsLive);

public sealed record RegieChannel(
    int PlayoutId,
    int ChannelId,
    string Number,
    string Name,
    bool OnAir,
    string Title,
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    string NowPlaying);

/// <summary>
///     Mode régie : prendre l'antenne d'une chaîne tout de suite (flash, écran de secours, direct…)
///     puis la rendre. La grille n'est pas reconstruite : les éléments recouverts sont mis de côté
///     dans <c>regie/&lt;playout&gt;.json</c> et remis en place au retour, à l'endroit exact où ils en seraient.
/// </summary>
public interface IRegieService
{
    Task<List<RegieChannel>> GetChannels(CancellationToken cancellationToken);
    Task<List<RegieSource>> Search(string text, CancellationToken cancellationToken);

    /// <summary>Vidéos importées comme « Habillage vidéo » (écrans de secours, mires…).</summary>
    Task<List<RegieSource>> Suggestions(CancellationToken cancellationToken);

    Task<Either<BaseError, Unit>> TakeAir(
        int playoutId,
        int mediaItemId,
        RegieMode mode,
        int minutes,
        string title,
        CancellationToken cancellationToken);

    Task<Either<BaseError, Unit>> Release(int playoutId, CancellationToken cancellationToken);
}

public sealed class RegieService(
    IDbContextFactory<TvContext> dbContextFactory,
    IFFmpegSegmenterService segmenterService,
    System.Threading.Channels.ChannelWriter<IBackgroundServiceRequest> workerChannel,
    ILogger<RegieService> logger) : IRegieService
{
    public const string Context = "nounours:regie";
    private static readonly TimeSpan MaxWindow = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public async Task<List<RegieChannel>> GetChannels(CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var playouts = await dbContext.Playouts
            .AsNoTracking()
            .Select(p => new { p.Id, p.ChannelId, p.Channel.Number, p.Channel.Name })
            .ToListAsync(cancellationToken);

        DateTime nowUtc = DateTime.UtcNow;
        var playoutIds = playouts.Select(p => p.Id).ToList();
        var current = await dbContext.PlayoutItems
            .AsNoTracking()
            .Where(i => playoutIds.Contains(i.PlayoutId) && i.Start <= nowUtc && i.Finish > nowUtc)
            .Select(i => new { i.PlayoutId, i.CustomTitle, i.MediaItemId })
            .ToListAsync(cancellationToken);

        var result = new List<RegieChannel>();
        foreach (var p in playouts)
        {
            RegieBackup backup = ReadBackup(p.Id).IfNoneUnsafe((RegieBackup)null);
            bool onAir = backup is not null && backup.Until > DateTimeOffset.Now;
            if (!onAir && backup is not null)
            {
                // prise d'antenne terminée d'elle-même
                DeleteBackup(p.Id);
            }

            var playing = current.Find(c => c.PlayoutId == p.Id);
            string nowPlaying = playing is null ? "—" : playing.CustomTitle ?? $"élément n° {playing.MediaItemId}";

            result.Add(
                new RegieChannel(
                    p.Id,
                    p.ChannelId,
                    p.Number,
                    p.Name,
                    onAir,
                    onAir ? backup.Title : null,
                    onAir ? backup.Since : null,
                    onAir ? backup.Until : null,
                    nowPlaying));
        }

        return result
            .OrderBy(c => decimal.TryParse(c.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal n) ? n : decimal.MaxValue)
            .ToList();
    }

    public async Task<List<RegieSource>> Search(string text, CancellationToken cancellationToken)
    {
        var catalog = new ContentCatalog(dbContextFactory);
        List<RegieSource> result = (await catalog.SearchItems(text, cancellationToken))
            .Select(o => new RegieSource(o.Id, o.Name, o.Detail, o.Duration ?? TimeSpan.Zero, false))
            .ToList();

        if (!string.IsNullOrWhiteSpace(text) && text.Trim().Length >= 2)
        {
            string pattern = "%" + text.Trim().Replace("%", string.Empty, StringComparison.Ordinal) + "%";
            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var streams = await dbContext.RemoteStreams.AsNoTracking()
                .Where(s => s.RemoteStreamMetadata.Any(m => EF.Functions.Like(m.Title, pattern)))
                .Select(s => new { s.Id, Title = s.RemoteStreamMetadata.First().Title, s.IsLive, s.Duration })
                .Take(25)
                .ToListAsync(cancellationToken);

            result.AddRange(
                streams.Select(s => new RegieSource(
                    s.Id,
                    s.Title,
                    s.IsLive ? "Flux en direct" : "Flux distant",
                    s.Duration ?? TimeSpan.Zero,
                    s.IsLive || s.Duration is null)));
        }

        return result;
    }

    public async Task<List<RegieSource>> Suggestions(CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        string name = $"Habillage · {FolderImportService.Label(ImportRole.Habillage)}";
        var items = await dbContext.OtherVideos.AsNoTracking()
            .Where(v => v.LibraryPath.Library.Name == name)
            .Select(v => new
            {
                v.Id,
                Title = v.OtherVideoMetadata.Select(m => m.Title).FirstOrDefault(),
                Duration = v.MediaVersions.Select(mv => mv.Duration).FirstOrDefault()
            })
            .Take(30)
            .ToListAsync(cancellationToken);

        // les flux en direct (OBS, chaînes d'info) d'abord
        string directLibrary = FolderImportService.Label(ImportRole.Direct);
        var streams = await dbContext.RemoteStreams.AsNoTracking()
            .Where(s => s.LibraryPath.Library.Name == directLibrary)
            .Select(s => new { s.Id, Title = s.RemoteStreamMetadata.Select(m => m.Title).FirstOrDefault() })
            .ToListAsync(cancellationToken);

        return streams
            .Select(s => new RegieSource(s.Id, s.Title ?? $"Flux {s.Id}", "Flux en direct", TimeSpan.Zero, true))
            .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .Concat(items
            .Select(i => new RegieSource(i.Id, i.Title ?? $"Vidéo {i.Id}", $"Habillage vidéo · {ContentCatalog.Format(i.Duration)}", i.Duration, false))
            .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase))
            .ToList();
    }

    public async Task<Either<BaseError, Unit>> TakeAir(
        int playoutId,
        int mediaItemId,
        RegieMode mode,
        int minutes,
        string title,
        CancellationToken cancellationToken)
    {
        if (ReadBackup(playoutId).Any(b => b.Until > DateTimeOffset.Now))
        {
            // nouvelle prise d'antenne par-dessus une autre : on rend d'abord l'antenne proprement
            Either<BaseError, Unit> released = await Release(playoutId, cancellationToken);
            if (released.IsLeft)
            {
                return released;
            }
        }

        await Lock.WaitAsync(cancellationToken);
        try
        {
            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            Playout playout = await dbContext.Playouts
                .Include(p => p.Channel)
                .FirstOrDefaultAsync(p => p.Id == playoutId, cancellationToken);
            if (playout is null)
            {
                return BaseError.New("Chaîne introuvable");
            }

            MediaItem mediaItem = await dbContext.MediaItems
                .AsNoTracking()
                .Include(mi => (mi as Movie).MediaVersions)
                .Include(mi => (mi as Episode).MediaVersions)
                .Include(mi => (mi as MusicVideo).MediaVersions)
                .Include(mi => (mi as OtherVideo).MediaVersions)
                .Include(mi => (mi as Song).MediaVersions)
                .Include(mi => (mi as RemoteStream).MediaVersions)
                .Include(mi => (mi as Movie).MovieMetadata)
                .Include(mi => (mi as OtherVideo).OtherVideoMetadata)
                .Include(mi => (mi as MusicVideo).MusicVideoMetadata)
                .Include(mi => (mi as RemoteStream).RemoteStreamMetadata)
                .FirstOrDefaultAsync(mi => mi.Id == mediaItemId, cancellationToken);
            if (mediaItem is null)
            {
                return BaseError.New("Vidéo introuvable");
            }

            TimeSpan itemDuration;
            try
            {
                itemDuration = mediaItem.GetDurationForPlayout();
            }
            catch (Exception)
            {
                itemDuration = TimeSpan.Zero;
            }

            bool live = mediaItem is RemoteStream { IsLive: true } || itemDuration <= TimeSpan.FromSeconds(1);
            if (live && mode == RegieMode.Once)
            {
                mode = RegieMode.UntilRelease;
            }

            TimeSpan window = mode switch
            {
                RegieMode.Once => itemDuration,
                RegieMode.Minutes => TimeSpan.FromMinutes(Math.Clamp(minutes, 1, (int)MaxWindow.TotalMinutes)),
                _ => MaxWindow
            };

            DateTime nowUtc = DateTime.UtcNow;
            nowUtc = new DateTime(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            DateTime endUtc = nowUtc + window;

            string displayTitle = string.IsNullOrWhiteSpace(title) ? DefaultTitle(mediaItem) : title.Trim();
            var backup = new RegieBackup
            {
                PlayoutId = playoutId,
                ChannelNumber = playout.Channel.Number,
                Title = displayTitle,
                Since = new DateTimeOffset(nowUtc, TimeSpan.Zero).ToLocalTime(),
                Until = new DateTimeOffset(endUtc, TimeSpan.Zero).ToLocalTime()
            };

            // ------------------------------------------------ libérer le créneau
            List<PlayoutItem> overlapping = await dbContext.PlayoutItems
                .Include(i => i.PlayoutItemWatermarks)
                .Include(i => i.PlayoutItemGraphicsElements)
                .Where(i => i.PlayoutId == playoutId && i.Finish > nowUtc && i.Start < endUtc)
                .OrderBy(i => i.Start)
                .ToListAsync(cancellationToken);

            foreach (PlayoutItem item in overlapping)
            {
                if (item.Start < nowUtc)
                {
                    // élément en cours : coupé maintenant ; s'il dépasse la fin, la suite est recréée après
                    backup.Trimmed.Add(SavedItem.From(item));
                    if (item.Finish > endUtc)
                    {
                        PlayoutItem rest = item.Clone();
                        rest.InPoint = item.InPoint + (endUtc - item.Start);
                        rest.Start = endUtc;
                        rest.GuideStart = null;
                        dbContext.PlayoutItems.Add(rest);
                        backup.CreatedKeys.Add(Key(rest.MediaItemId, endUtc));
                    }

                    item.OutPoint = item.InPoint + (nowUtc - item.Start);
                    item.Finish = nowUtc;
                    item.GuideFinish = null;
                }
                else if (item.Finish > endUtc)
                {
                    // élément à cheval sur la fin : il démarre plus loin dans le fichier
                    backup.StartTrimmed.Add(SavedItem.From(item));
                    item.InPoint += endUtc - item.Start;
                    item.Start = endUtc;
                    item.GuideStart = null;
                }
                else
                {
                    backup.Removed.Add(SavedItem.From(item));
                    dbContext.PlayoutItems.Remove(item);
                }
            }

            // les « trous » de la grille (hors antenne) recouverts sont aussi mis de côté
            List<PlayoutGap> gaps = await dbContext.PlayoutGaps
                .Where(g => g.PlayoutId == playoutId && g.Finish > nowUtc && g.Start < endUtc)
                .ToListAsync(cancellationToken);
            foreach (PlayoutGap gap in gaps)
            {
                backup.Gaps.Add(new SavedGap(DateTime.SpecifyKind(gap.Start, DateTimeKind.Utc), DateTime.SpecifyKind(gap.Finish, DateTimeKind.Utc)));
                if (gap.Finish > endUtc)
                {
                    // la fin du trou reste visible dans le guide après la prise d'antenne
                    dbContext.PlayoutGaps.Add(new PlayoutGap { PlayoutId = playoutId, Start = endUtc, Finish = gap.Finish });
                }
            }

            dbContext.PlayoutGaps.RemoveRange(gaps);

            // ------------------------------------------------ la prise d'antenne
            int guideGroup = (await dbContext.PlayoutItems
                .Where(i => i.PlayoutId == playoutId)
                .MaxAsync(i => (int?)i.GuideGroup, cancellationToken) ?? 0) + 1;

            DateTime cursor = nowUtc;
            while (cursor < endUtc)
            {
                TimeSpan length = live ? endUtc - cursor : TimeSpan.FromTicks(Math.Min(itemDuration.Ticks, (endUtc - cursor).Ticks));
                if (length < TimeSpan.FromSeconds(1))
                {
                    break;
                }

                dbContext.PlayoutItems.Add(
                    new PlayoutItem
                    {
                        PlayoutId = playoutId,
                        MediaItemId = mediaItemId,
                        Start = cursor,
                        Finish = cursor + length,
                        InPoint = TimeSpan.Zero,
                        OutPoint = length,
                        CustomTitle = displayTitle,
                        GuideGroup = guideGroup,
                        FillerKind = FillerKind.None,
                        SchedulingContext = Context,
                        PlayoutItemWatermarks = [],
                        PlayoutItemGraphicsElements = []
                    });
                cursor += length;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            WriteBackup(backup);

            logger.LogInformation(
                "Régie : prise d'antenne sur la chaîne {Channel} ({Title}) jusqu'à {Until}",
                playout.Channel.Number,
                displayTitle,
                backup.Until);

            await Apply(playout.Channel.Number, cancellationToken);
            return Unit.Default;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Régie : prise d'antenne impossible");
            return BaseError.New(ex.Message);
        }
        finally
        {
            Lock.Release();
        }
    }

    public async Task<Either<BaseError, Unit>> Release(int playoutId, CancellationToken cancellationToken)
    {
        Option<RegieBackup> maybeBackup = ReadBackup(playoutId);
        if (maybeBackup.IsNone)
        {
            return Unit.Default;
        }

        RegieBackup backup = maybeBackup.Head();
        await Lock.WaitAsync(cancellationToken);
        try
        {
            DateTime nowUtc = DateTime.UtcNow;
            nowUtc = new DateTime(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            DateTime endUtc = backup.Until.UtcDateTime;

            if (nowUtc >= endUtc)
            {
                DeleteBackup(playoutId);
                return Unit.Default;
            }

            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            // 1. retirer la prise d'antenne (et couper celle en cours maintenant)
            List<PlayoutItem> regie = await dbContext.PlayoutItems
                .Where(i => i.PlayoutId == playoutId && i.SchedulingContext == Context && i.Finish > nowUtc)
                .ToListAsync(cancellationToken);
            foreach (PlayoutItem item in regie)
            {
                if (item.Start < nowUtc)
                {
                    item.OutPoint = item.InPoint + (nowUtc - item.Start);
                    item.Finish = nowUtc;
                }
                else
                {
                    dbContext.PlayoutItems.Remove(item);
                }
            }

            // 2. retirer la suite recréée de l'élément coupé (il va être remis en entier ci-dessous)
            List<PlayoutItem> created = await dbContext.PlayoutItems
                .Where(i => i.PlayoutId == playoutId && i.Start == endUtc)
                .ToListAsync(cancellationToken);
            dbContext.PlayoutItems.RemoveRange(created.Where(c => backup.CreatedKeys.Contains(Key(c.MediaItemId, c.Start))));

            // 3. remettre la grille là où elle en serait
            foreach (SavedItem saved in backup.Trimmed.Concat(backup.Removed))
            {
                if (saved.Finish <= nowUtc)
                {
                    continue;
                }

                PlayoutItem restored = saved.ToItem(playoutId);
                if (restored.Start < nowUtc)
                {
                    restored.InPoint += nowUtc - restored.Start;
                    restored.Start = nowUtc;
                    restored.GuideStart = null;
                }

                dbContext.PlayoutItems.Add(restored);
            }

            dbContext.PlayoutGaps.RemoveRange(
                await dbContext.PlayoutGaps
                    .Where(g => g.PlayoutId == playoutId && g.Start == endUtc)
                    .ToListAsync(cancellationToken));

            foreach (SavedGap gap in backup.Gaps.Where(g => g.Finish > nowUtc))
            {
                dbContext.PlayoutGaps.Add(
                    new PlayoutGap
                    {
                        PlayoutId = playoutId,
                        Start = gap.Start < nowUtc ? nowUtc : gap.Start,
                        Finish = gap.Finish
                    });
            }

            foreach (SavedItem saved in backup.StartTrimmed)
            {
                PlayoutItem existing = await dbContext.PlayoutItems
                    .FirstOrDefaultAsync(i => i.PlayoutId == playoutId && i.MediaItemId == saved.MediaItemId && i.Start == endUtc, cancellationToken);
                if (existing is null)
                {
                    continue;
                }

                DateTime start = saved.Start < nowUtc ? nowUtc : saved.Start;
                existing.Start = start;
                existing.InPoint = saved.InPoint + (start - saved.Start);
                existing.GuideStart = saved.GuideStart;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            DeleteBackup(playoutId);

            logger.LogInformation("Régie : antenne rendue sur la chaîne {Channel}", backup.ChannelNumber);
            await Apply(backup.ChannelNumber, cancellationToken);
            return Unit.Default;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Régie : impossible de rendre l'antenne");
            return BaseError.New(ex.Message);
        }
        finally
        {
            Lock.Release();
        }
    }

    // guide à jour + les spectateurs basculent tout de suite (le lecteur se reconnecte sur la nouvelle session)
    private async Task Apply(string channelNumber, CancellationToken cancellationToken)
    {
        await workerChannel.WriteAsync(new RefreshChannelData(channelNumber), cancellationToken);
        await segmenterService.StopChannel(channelNumber, cancellationToken);
    }

    private static string DefaultTitle(MediaItem mediaItem) =>
        mediaItem switch
        {
            Movie m => m.MovieMetadata?.FirstOrDefault()?.Title,
            OtherVideo o => o.OtherVideoMetadata?.FirstOrDefault()?.Title,
            MusicVideo mv => mv.MusicVideoMetadata?.FirstOrDefault()?.Title,
            RemoteStream r => r.RemoteStreamMetadata?.FirstOrDefault()?.Title,
            _ => null
        } ?? "Édition spéciale";

    private static string Key(int mediaItemId, DateTime start) => $"{mediaItemId}@{start.Ticks}";

    private static string BackupPath(int playoutId) => Path.Combine(FileSystemLayout.RegieFolder, $"{playoutId}.json");

    private static Option<RegieBackup> ReadBackup(int playoutId)
    {
        try
        {
            string path = BackupPath(playoutId);
            return File.Exists(path)
                ? Optional(JsonSerializer.Deserialize<RegieBackup>(File.ReadAllText(path), JsonOptions))
                : Option<RegieBackup>.None;
        }
        catch (Exception)
        {
            return Option<RegieBackup>.None;
        }
    }

    private static void WriteBackup(RegieBackup backup)
    {
        Directory.CreateDirectory(FileSystemLayout.RegieFolder);
        File.WriteAllText(BackupPath(backup.PlayoutId), JsonSerializer.Serialize(backup, JsonOptions));
    }

    private static void DeleteBackup(int playoutId)
    {
        try
        {
            File.Delete(BackupPath(playoutId));
        }
        catch (IOException)
        {
            // déjà supprimé
        }
    }

    public sealed class RegieBackup
    {
        public int PlayoutId { get; set; }
        public string ChannelNumber { get; set; }
        public string Title { get; set; }
        public DateTimeOffset Since { get; set; }
        public DateTimeOffset Until { get; set; }
        public List<SavedItem> Trimmed { get; set; } = [];
        public List<SavedItem> StartTrimmed { get; set; } = [];
        public List<SavedItem> Removed { get; set; } = [];
        public List<string> CreatedKeys { get; set; } = [];
        public List<SavedGap> Gaps { get; set; } = [];
    }

    public sealed record SavedGap(DateTime Start, DateTime Finish);

    /// <summary>Copie d'un élément de la grille, pour le remettre en place au retour.</summary>
    public sealed class SavedItem
    {
        public int MediaItemId { get; set; }
        public DateTime Start { get; set; }
        public DateTime Finish { get; set; }
        public DateTime? GuideStart { get; set; }
        public DateTime? GuideFinish { get; set; }
        public string CustomTitle { get; set; }
        public int GuideGroup { get; set; }
        public FillerKind FillerKind { get; set; }
        public TimeSpan InPoint { get; set; }
        public TimeSpan OutPoint { get; set; }
        public string ChapterTitle { get; set; }
        public bool DisableWatermarks { get; set; }
        public string PreferredAudioLanguageCode { get; set; }
        public string PreferredAudioTitle { get; set; }
        public string PreferredSubtitleLanguageCode { get; set; }
        public ChannelSubtitleMode? SubtitleMode { get; set; }
        public string BlockKey { get; set; }
        public string CollectionKey { get; set; }
        public string CollectionEtag { get; set; }
        public string SchedulingContext { get; set; }
        public List<int> WatermarkIds { get; set; } = [];
        public List<SavedGraphics> Graphics { get; set; } = [];

        public static SavedItem From(PlayoutItem item) =>
            new()
            {
                MediaItemId = item.MediaItemId,
                Start = DateTime.SpecifyKind(item.Start, DateTimeKind.Utc),
                Finish = DateTime.SpecifyKind(item.Finish, DateTimeKind.Utc),
                GuideStart = item.GuideStart,
                GuideFinish = item.GuideFinish,
                CustomTitle = item.CustomTitle,
                GuideGroup = item.GuideGroup,
                FillerKind = item.FillerKind,
                InPoint = item.InPoint,
                OutPoint = item.OutPoint,
                ChapterTitle = item.ChapterTitle,
                DisableWatermarks = item.DisableWatermarks,
                PreferredAudioLanguageCode = item.PreferredAudioLanguageCode,
                PreferredAudioTitle = item.PreferredAudioTitle,
                PreferredSubtitleLanguageCode = item.PreferredSubtitleLanguageCode,
                SubtitleMode = item.SubtitleMode,
                BlockKey = item.BlockKey,
                CollectionKey = item.CollectionKey,
                CollectionEtag = item.CollectionEtag,
                SchedulingContext = item.SchedulingContext,
                WatermarkIds = item.PlayoutItemWatermarks?.Select(w => w.WatermarkId).ToList() ?? [],
                Graphics = item.PlayoutItemGraphicsElements?
                    .Where(g => g.GraphicsElementId.HasValue)
                    .Select(g => new SavedGraphics(g.GraphicsElementId.Value, g.Variables))
                    .ToList() ?? []
            };

        public PlayoutItem ToItem(int playoutId) =>
            new()
            {
                PlayoutId = playoutId,
                MediaItemId = MediaItemId,
                Start = DateTime.SpecifyKind(Start, DateTimeKind.Utc),
                Finish = DateTime.SpecifyKind(Finish, DateTimeKind.Utc),
                GuideStart = GuideStart,
                GuideFinish = GuideFinish,
                CustomTitle = CustomTitle,
                GuideGroup = GuideGroup,
                FillerKind = FillerKind,
                InPoint = InPoint,
                OutPoint = OutPoint,
                ChapterTitle = ChapterTitle,
                DisableWatermarks = DisableWatermarks,
                PreferredAudioLanguageCode = PreferredAudioLanguageCode,
                PreferredAudioTitle = PreferredAudioTitle,
                PreferredSubtitleLanguageCode = PreferredSubtitleLanguageCode,
                SubtitleMode = SubtitleMode,
                BlockKey = BlockKey,
                CollectionKey = CollectionKey,
                CollectionEtag = CollectionEtag,
                SchedulingContext = SchedulingContext,
                PlayoutItemWatermarks = WatermarkIds.Select(id => new PlayoutItemWatermark { WatermarkId = id }).ToList(),
                PlayoutItemGraphicsElements = Graphics
                    .Select(g => new PlayoutItemGraphicsElement { GraphicsElementId = g.Id, Variables = g.Variables })
                    .ToList()
            };
    }

    public sealed record SavedGraphics(int Id, string Variables);
}
