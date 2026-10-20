using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using static ErsatzTV.Application.Playouts.Mapper;

namespace ErsatzTV.Application.Nounours;

public enum GuideEntryKind
{
    Movie,
    Episode,
    MusicVideo,
    OtherVideo,
    Song,
    Image,
    RemoteStream,
    Gap,
    Other
}

public sealed record GuideEntry(
    int ChannelId,
    int PlayoutId,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset Finish,
    GuideEntryKind Kind,
    FillerKind FillerKind,
    string CustomTitle)
{
    public bool IsFiller => FillerKind is not FillerKind.None;

    public string DisplayTitle => string.IsNullOrWhiteSpace(CustomTitle) ? Title : CustomTitle;
}

public sealed record ChannelPlayoutInfo(
    int ChannelId,
    int PlayoutId,
    PlayoutScheduleKind ScheduleKind,
    int? ProgramScheduleId,
    string ProgramScheduleName);

public sealed record NowNext(int ChannelId, int PlayoutId, Option<GuideEntry> Now, Option<GuideEntry> Next);

public sealed record LibraryOverview(
    int Channels,
    int Playouts,
    int Movies,
    int Shows,
    int Episodes,
    int MusicVideos,
    int OtherVideos,
    int Songs,
    int Collections,
    int SmartCollections,
    int Libraries,
    DateTime? LastScan);

/// <summary>Lecture du programme (playout items) pour la grille, le tableau de bord et les modules.</summary>
public interface IGuideService
{
    Task<List<GuideEntry>> GetEntries(
        DateTimeOffset start,
        DateTimeOffset finish,
        IReadOnlyCollection<int> channelIds,
        bool includeFiller,
        CancellationToken cancellationToken);

    Task<Dictionary<int, NowNext>> GetNowNext(CancellationToken cancellationToken);

    Task<LibraryOverview> GetOverview(CancellationToken cancellationToken);

    Task<Dictionary<int, ChannelPlayoutInfo>> GetChannelPlayouts(CancellationToken cancellationToken);
}

public sealed class GuideService(IDbContextFactory<TvContext> dbContextFactory) : IGuideService
{
    public async Task<List<GuideEntry>> GetEntries(
        DateTimeOffset start,
        DateTimeOffset finish,
        IReadOnlyCollection<int> channelIds,
        bool includeFiller,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        DateTime fromUtc = start.UtcDateTime;
        DateTime toUtc = finish.UtcDateTime;

        Dictionary<int, int> playoutToChannel = await dbContext.Playouts
            .AsNoTracking()
            .Where(p => channelIds.Count == 0 || channelIds.Contains(p.ChannelId))
            .ToDictionaryAsync(p => p.Id, p => p.ChannelId, cancellationToken);

        var playoutIds = playoutToChannel.Keys.ToList();

        List<PlayoutItem> items = await IncludeForTitle(dbContext.PlayoutItems.AsNoTracking())
            .Where(i => playoutIds.Contains(i.PlayoutId))
            .Where(i => i.Finish > fromUtc && i.Start < toUtc)
            .Where(i => includeFiller || i.FillerKind == FillerKind.None)
            .OrderBy(i => i.Start)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        List<PlayoutGap> gaps = await dbContext.PlayoutGaps
            .AsNoTracking()
            .Where(g => playoutIds.Contains(g.PlayoutId))
            .Where(g => g.Finish > fromUtc && g.Start < toUtc)
            .ToListAsync(cancellationToken);

        var result = items.Select(i => ToEntry(i, playoutToChannel[i.PlayoutId])).ToList();
        result.AddRange(
            gaps.Select(g => new GuideEntry(
                playoutToChannel[g.PlayoutId],
                g.PlayoutId,
                "Hors antenne",
                g.StartOffset,
                g.FinishOffset,
                GuideEntryKind.Gap,
                FillerKind.None,
                null)));

        return result.OrderBy(e => e.ChannelId).ThenBy(e => e.Start).ToList();
    }

    public async Task<Dictionary<int, NowNext>> GetNowNext(CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        List<GuideEntry> entries = await GetEntries(now, now.AddHours(12), [], includeFiller: true, cancellationToken);

        var result = new Dictionary<int, NowNext>();
        foreach (IGrouping<int, GuideEntry> group in entries.GroupBy(e => e.ChannelId))
        {
            var ordered = group.OrderBy(e => e.Start).ToList();
            Option<GuideEntry> current = ordered.Find(e => e.Start <= now && e.Finish > now);
            Option<GuideEntry> next = ordered.Find(e =>
                e.Start > now && !e.IsFiller && e.Kind != GuideEntryKind.Gap);

            result[group.Key] = new NowNext(group.Key, group.First().PlayoutId, current, next);
        }

        return result;
    }

    public async Task<LibraryOverview> GetOverview(CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var minimum = new DateTime(2001, 1, 1);
        DateTime? lastScan = await dbContext.LibraryPaths
            .AsNoTracking()
            .Where(lp => lp.LastScan != null && lp.LastScan > minimum)
            .MaxAsync(lp => lp.LastScan, cancellationToken);

        return new LibraryOverview(
            await dbContext.Channels.CountAsync(cancellationToken),
            await dbContext.Playouts.CountAsync(cancellationToken),
            await dbContext.Movies.CountAsync(cancellationToken),
            await dbContext.Shows.CountAsync(cancellationToken),
            await dbContext.Episodes.CountAsync(cancellationToken),
            await dbContext.MusicVideos.CountAsync(cancellationToken),
            await dbContext.OtherVideos.CountAsync(cancellationToken),
            await dbContext.Songs.CountAsync(cancellationToken),
            await dbContext.Collections.CountAsync(cancellationToken),
            await dbContext.SmartCollections.CountAsync(cancellationToken),
            await dbContext.Libraries.CountAsync(cancellationToken),
            lastScan);
    }

    public async Task<Dictionary<int, ChannelPlayoutInfo>> GetChannelPlayouts(CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<Playout> playouts = await dbContext.Playouts
            .AsNoTracking()
            .Include(p => p.ProgramSchedule)
            .ToListAsync(cancellationToken);

        return playouts
            .GroupBy(p => p.ChannelId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    Playout p = g.First();
                    return new ChannelPlayoutInfo(p.ChannelId, p.Id, p.ScheduleKind, p.ProgramScheduleId, p.ProgramSchedule?.Name);
                });
    }

    private static GuideEntry ToEntry(PlayoutItem item, int channelId) =>
        new(
            channelId,
            item.PlayoutId,
            GetDisplayTitle(item.MediaItem, item.ChapterTitle),
            item.StartOffset,
            item.FinishOffset,
            item.MediaItem switch
            {
                Movie => GuideEntryKind.Movie,
                Episode => GuideEntryKind.Episode,
                MusicVideo => GuideEntryKind.MusicVideo,
                OtherVideo => GuideEntryKind.OtherVideo,
                Song => GuideEntryKind.Song,
                Image => GuideEntryKind.Image,
                RemoteStream => GuideEntryKind.RemoteStream,
                _ => GuideEntryKind.Other
            },
            item.FillerKind,
            item.CustomTitle);

    private static IQueryable<PlayoutItem> IncludeForTitle(IQueryable<PlayoutItem> query) =>
        query
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Movie).MovieMetadata)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as MusicVideo).MusicVideoMetadata)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as MusicVideo).Artist)
            .ThenInclude(mm => mm.ArtistMetadata)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Episode).EpisodeMetadata)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Episode).Season)
            .ThenInclude(s => s.Show)
            .ThenInclude(s => s.ShowMetadata)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as OtherVideo).OtherVideoMetadata)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Song).SongMetadata)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Image).ImageMetadata)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as RemoteStream).RemoteStreamMetadata);
}
