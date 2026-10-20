using System.Text.Json;
using System.Text.Json.Serialization;
using ErsatzTV.Application.MediaCollections;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Nounours;

public enum SegmentRole
{
    Pub,
    Jingle,
    Contenu,
    BandeAnnonce,
    Programme,
    Autre
}

/// <summary>Un morceau du déroulé : « 3 pubs », « jingle d'ouverture », « 1 épisode de la série »…</summary>
public sealed class ProgrammeSegment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public SegmentRole Role { get; set; } = SegmentRole.Contenu;
    public string Label { get; set; } = string.Empty;

    public ContentSourceKind SourceKind { get; set; } = ContentSourceKind.Show;
    public int? SourceId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public CollectionType ItemType { get; set; } = CollectionType.OtherVideo;
    public string Query { get; set; } = string.Empty;

    /// <summary>Nombre d'éléments joués à chaque passage.</summary>
    public int Count { get; set; } = 1;

    public PlaybackOrder Order { get; set; } = PlaybackOrder.Shuffle;
    public bool? ShowInGuide { get; set; }

    [JsonIgnore]
    public bool IsInGuide => ShowInGuide ?? Role is SegmentRole.Contenu or SegmentRole.Programme;
}

public enum BreakPlacement
{
    /// <summary>Toutes les X minutes.</summary>
    Every,

    /// <summary>N coupures réparties régulièrement.</summary>
    Count
}

/// <summary>Coupures publicitaires pendant le contenu principal.</summary>
public sealed class ProgrammeBreaks
{
    public bool Enabled { get; set; }
    public BreakPlacement Placement { get; set; } = BreakPlacement.Every;
    public double EveryMinutes { get; set; } = 15;
    public int Count { get; set; } = 2;
    public double StartMinutes { get; set; } = 10;
    public double EndMinutes { get; set; } = 5;
    public List<ProgrammeSegment> Segments { get; set; } = [];

    public string Directive() =>
        Placement == BreakPlacement.Every
            ? FormattableString.Invariant($"@coupures toutes={EveryMinutes} debut={StartMinutes} fin={EndMinutes}")
            : FormattableString.Invariant($"@coupures nombre={Count} debut={StartMinutes} fin={EndMinutes}");
}

public sealed class Programme
{
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = "Nouveau programme";
    public string Description { get; set; } = string.Empty;
    public string Color { get; set; } = "#4F8CFF";
    public List<ProgrammeSegment> Segments { get; set; } = [];
    public ProgrammeBreaks Breaks { get; set; } = new();

    // éléments ErsatzTV générés
    public int? PlaylistId { get; set; }
    public int? BreakPlaylistId { get; set; }
    public int? BreakFillerId { get; set; }

    /// <summary>Coupures reprises d'un programme lié quand ce programme n'a pas les siennes.</summary>
    public int? InheritedBreakFillerId { get; set; }

    public int ItemCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed record ProgrammeSummary(
    string Slug,
    string Name,
    string Description,
    string Color,
    int Segments,
    bool HasBreaks,
    int? PlaylistId,
    int? BreakFillerId,
    int ItemCount,
    DateTimeOffset UpdatedAt);

/// <summary>Ce qu'il faut pour programmer un programme dans un planning.</summary>
public sealed record ProgrammeScheduling(int PlaylistId, int ItemCount, int? BreakFillerId);

public sealed record SegmentEstimate(string SegmentId, TimeSpan? Each, TimeSpan? Total);

public sealed record ProgrammeEstimate(List<SegmentEstimate> Segments, TimeSpan? BreakDuration, int BreakCount, TimeSpan? Total, bool Partial);

public interface IProgrammeService
{
    Task<List<ProgrammeSummary>> GetAll(CancellationToken cancellationToken);
    Task<Option<Programme>> GetProgramme(string slug, CancellationToken cancellationToken);
    Task<Either<BaseError, Programme>> Save(Programme programme, CancellationToken cancellationToken);
    Task Delete(string slug, CancellationToken cancellationToken);
    Task<Option<ProgrammeScheduling>> GetSchedulingByPlaylist(int playlistId, CancellationToken cancellationToken);
    Task<ProgrammeEstimate> Estimate(Programme programme, CancellationToken cancellationToken);
}

/// <summary>
///     Un programme est enregistré en JSON (templates/programmes) et traduit en éléments ErsatzTV :
///     - une playlist « Programme : nom » (groupe « Programmes ») qui enchaîne les segments ;
///     - une playlist « Coupure : nom » + un remplissage « milieu de programme » qui la joue
///       toutes les X minutes grâce à la directive @coupures du moteur.
///     Un segment peut être un autre programme : ses segments sont alors insérés (programmes liés).
/// </summary>
public sealed class ProgrammeService(
    IDbContextFactory<TvContext> dbContextFactory,
    IMediator mediator,
    System.Threading.Channels.ChannelWriter<IBackgroundServiceRequest> workerChannel) : IProgrammeService
{
    public const string PlaylistGroupName = "Programmes";
    public const string BreakPlaylistGroupName = "Programmes · coupures";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<List<ProgrammeSummary>> ReadSummaries(CancellationToken cancellationToken)
    {
        var result = new List<ProgrammeSummary>();
        if (!Directory.Exists(FileSystemLayout.ProgrammesFolder))
        {
            return result;
        }

        foreach (string file in Directory.GetFiles(FileSystemLayout.ProgrammesFolder, "*.json"))
        {
            foreach (Programme p in await Load(file, cancellationToken))
            {
                result.Add(
                    new ProgrammeSummary(
                        p.Slug,
                        p.Name,
                        p.Description,
                        p.Color,
                        p.Segments.Count,
                        p.Breaks.Enabled,
                        p.PlaylistId,
                        p.Breaks.Enabled ? p.BreakFillerId : p.InheritedBreakFillerId,
                        p.ItemCount,
                        p.UpdatedAt));
            }
        }

        return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task<List<ProgrammeSummary>> GetAll(CancellationToken cancellationToken) => ReadSummaries(cancellationToken);

    public Task<Option<Programme>> GetProgramme(string slug, CancellationToken cancellationToken) =>
        Load(PathFor(slug), cancellationToken);

    public async Task<Option<ProgrammeScheduling>> GetSchedulingByPlaylist(int playlistId, CancellationToken cancellationToken)
    {
        foreach (ProgrammeSummary p in await ReadSummaries(cancellationToken))
        {
            if (p.PlaylistId == playlistId)
            {
                return new ProgrammeScheduling(playlistId, Math.Max(1, p.ItemCount), p.BreakFillerId);
            }
        }

        return Option<ProgrammeScheduling>.None;
    }

    public async Task<Either<BaseError, Programme>> Save(Programme programme, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(programme.Name))
        {
            return BaseError.New("Donnez un nom au programme.");
        }

        if (programme.Segments.Count == 0)
        {
            return BaseError.New("Ajoutez au moins un segment.");
        }

        if (string.IsNullOrWhiteSpace(programme.Slug))
        {
            programme.Slug = UniqueSlug(HabillageService.Slugify(programme.Name));
        }

        // 1. sources « recherche » → collections intelligentes
        foreach (ProgrammeSegment segment in programme.Segments.Concat(programme.Breaks.Segments))
        {
            Option<BaseError> error = await EnsureSmartCollection(programme, segment, cancellationToken);
            foreach (BaseError e in error)
            {
                return e;
            }
        }

        // 2. déroulé à plat (programmes liés insérés)
        var flat = new List<ProgrammeSegment>();
        Option<BaseError> flattenError = await Flatten(programme, flat, [programme.Slug], cancellationToken);
        foreach (BaseError e in flattenError)
        {
            return e;
        }

        if (flat.Count == 0)
        {
            return BaseError.New("Le déroulé est vide : vérifiez que chaque segment a une source.");
        }

        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        PlaylistGroup group = await EnsureGroup(dbContext, PlaylistGroupName, cancellationToken);
        Playlist playlist = await EnsurePlaylist(dbContext, group, programme.PlaylistId, $"Programme : {programme.Name}", cancellationToken);
        ReplaceItems(dbContext, playlist, flat);
        programme.PlaylistId = playlist.Id;
        programme.ItemCount = flat.Sum(s => Math.Max(1, s.Count));

        // 3. coupures
        var breakSegments = programme.Breaks.Segments.Where(s => HasSource(s)).ToList();
        if (programme.Breaks.Enabled && breakSegments.Count > 0)
        {
            PlaylistGroup breakGroup = await EnsureGroup(dbContext, BreakPlaylistGroupName, cancellationToken);
            Playlist breakPlaylist = await EnsurePlaylist(
                dbContext,
                breakGroup,
                programme.BreakPlaylistId,
                $"Coupure : {programme.Name}",
                cancellationToken);
            ReplaceItems(dbContext, breakPlaylist, breakSegments);
            await dbContext.SaveChangesAsync(cancellationToken);
            programme.BreakPlaylistId = breakPlaylist.Id;

            FillerPreset filler = programme.BreakFillerId is { } fid
                ? await dbContext.FillerPresets.FirstOrDefaultAsync(f => f.Id == fid, cancellationToken)
                : null;
            if (filler is null)
            {
                filler = new FillerPreset();
                await dbContext.FillerPresets.AddAsync(filler, cancellationToken);
            }

            filler.Name = $"Coupure : {programme.Name}";
            filler.FillerKind = FillerKind.MidRoll;
            filler.FillerMode = FillerMode.Count;
            filler.Count = 1; // une coupure = un passage complet de la playlist de coupure
            filler.CollectionType = CollectionType.Playlist;
            filler.PlaylistId = breakPlaylist.Id;
            filler.CollectionId = null;
            filler.MediaItemId = null;
            filler.MultiCollectionId = null;
            filler.SmartCollectionId = null;
            filler.AllowWatermarks = true;
            filler.Expression = programme.Breaks.Directive();
            await dbContext.SaveChangesAsync(cancellationToken);
            programme.BreakFillerId = filler.Id;
        }
        else
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        programme.InheritedBreakFillerId = null;
        if (!programme.Breaks.Enabled)
        {
            foreach (ProgrammeSegment linkedSegment in programme.Segments.Where(x => x.SourceKind == ContentSourceKind.Programme))
            {
                foreach (Programme child in await FindLinked(linkedSegment, cancellationToken))
                {
                    programme.InheritedBreakFillerId ??= child.Breaks.Enabled ? child.BreakFillerId : child.InheritedBreakFillerId;
                }
            }
        }

        // 4. fichier du programme
        programme.UpdatedAt = DateTimeOffset.Now;
        Directory.CreateDirectory(FileSystemLayout.ProgrammesFolder);
        await File.WriteAllTextAsync(PathFor(programme.Slug), JsonSerializer.Serialize(programme, JsonOptions), cancellationToken);

        // 5. les chaînes qui utilisent ce programme (ou un programme qui l'inclut) se mettent à jour
        await RefreshPlayoutsUsing(dbContext, cancellationToken);

        return programme;
    }

    public async Task Delete(string slug, CancellationToken cancellationToken)
    {
        foreach (Programme programme in await GetProgramme(slug, cancellationToken))
        {
            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            foreach (int id in new[] { programme.PlaylistId, programme.BreakPlaylistId }.Where(i => i.HasValue).Select(i => i.Value))
            {
                bool used = await dbContext.ProgramScheduleItems.AnyAsync(i => i.PlaylistId == id, cancellationToken);
                Playlist playlist = await dbContext.Playlists.FindAsync([id], cancellationToken);
                if (playlist is not null && !used)
                {
                    dbContext.Playlists.Remove(playlist);
                }
            }

            if (programme.BreakFillerId is { } fid)
            {
                FillerPreset filler = await dbContext.FillerPresets.FindAsync([fid], cancellationToken);
                bool used = await dbContext.ProgramScheduleItems.AnyAsync(i => i.MidRollFillerId == fid, cancellationToken);
                if (filler is not null && !used)
                {
                    dbContext.FillerPresets.Remove(filler);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            File.Delete(PathFor(slug));
        }
    }

    public async Task<ProgrammeEstimate> Estimate(Programme programme, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var partial = false;
        var segments = new List<SegmentEstimate>();
        TimeSpan total = TimeSpan.Zero;

        foreach (ProgrammeSegment segment in programme.Segments)
        {
            TimeSpan? each = await EstimateEach(dbContext, segment, [programme.Slug], cancellationToken);
            TimeSpan? segTotal = each * Math.Max(1, segment.Count);
            segments.Add(new SegmentEstimate(segment.Id, each, segTotal));
            if (segTotal is { } t)
            {
                total += t;
            }
            else
            {
                partial = true;
            }
        }

        TimeSpan? breakDuration = null;
        var breakCount = 0;
        if (programme.Breaks.Enabled && programme.Breaks.Segments.Count > 0)
        {
            TimeSpan b = TimeSpan.Zero;
            foreach (ProgrammeSegment segment in programme.Breaks.Segments)
            {
                TimeSpan? each = await EstimateEach(dbContext, segment, [programme.Slug], cancellationToken);
                if (each is { } e)
                {
                    b += e * Math.Max(1, segment.Count);
                }
                else
                {
                    partial = true;
                }
            }

            breakDuration = b;

            // nombre de coupures estimé sur le plus long segment « contenu »
            TimeSpan main = segments
                .Where(s => programme.Segments.First(x => x.Id == s.SegmentId).Role == SegmentRole.Contenu)
                .Select(s => s.Each ?? TimeSpan.Zero)
                .DefaultIfEmpty(TimeSpan.Zero)
                .Max();
            if (main > TimeSpan.Zero)
            {
                if (MidRollBreaks.TryParse(programme.Breaks.Directive(), out MidRollBreaks.Settings settings, out _))
                {
                    breakCount = Math.Max(0, MidRollBreaks.BuildChapters(settings, TimeSpan.Zero, main).Count - 1);
                    total += b * breakCount;
                }
            }
        }

        return new ProgrammeEstimate(segments, breakDuration, breakCount, total > TimeSpan.Zero ? total : null, partial);
    }

    // ------------------------------------------------------------------ interne

    private static async Task<TimeSpan?> EstimateEach(TvContext dbContext, ProgrammeSegment segment, System.Collections.Generic.HashSet<string> visited, CancellationToken cancellationToken)
    {
        switch (segment.SourceKind)
        {
            case ContentSourceKind.MediaItem when segment.SourceId is { } id:
                return segment.ItemType switch
                {
                    CollectionType.Movie => await dbContext.Movies.Where(m => m.Id == id).SelectMany(m => m.MediaVersions).Select(v => (TimeSpan?)v.Duration).FirstOrDefaultAsync(cancellationToken),
                    CollectionType.MusicVideo => await dbContext.MusicVideos.Where(m => m.Id == id).SelectMany(m => m.MediaVersions).Select(v => (TimeSpan?)v.Duration).FirstOrDefaultAsync(cancellationToken),
                    CollectionType.Episode => await dbContext.Episodes.Where(m => m.Id == id).SelectMany(m => m.MediaVersions).Select(v => (TimeSpan?)v.Duration).FirstOrDefaultAsync(cancellationToken),
                    _ => await dbContext.OtherVideos.Where(m => m.Id == id).SelectMany(m => m.MediaVersions).Select(v => (TimeSpan?)v.Duration).FirstOrDefaultAsync(cancellationToken)
                };
            case ContentSourceKind.Show when segment.SourceId is { } showId:
            {
                List<long> ticks = await dbContext.Episodes
                    .Where(e => e.Season.ShowId == showId)
                    .SelectMany(e => e.MediaVersions)
                    .Select(v => v.Duration.Ticks)
                    .ToListAsync(cancellationToken);
                return ticks.Count == 0 ? null : TimeSpan.FromTicks((long)ticks.Average());
            }
            case ContentSourceKind.Search when segment.Query is "type:movie":
                return await AverageAsync(dbContext.Movies.SelectMany(m => m.MediaVersions).Select(v => v.Duration.Ticks), cancellationToken);
            case ContentSourceKind.Search when segment.Query is "type:episode":
                return await AverageAsync(dbContext.Episodes.SelectMany(m => m.MediaVersions).Select(v => v.Duration.Ticks), cancellationToken);
            case ContentSourceKind.Search when segment.Query is "type:music_video":
                return await AverageAsync(dbContext.MusicVideos.SelectMany(m => m.MediaVersions).Select(v => v.Duration.Ticks), cancellationToken);
            case ContentSourceKind.Search when segment.Query is "type:other_video":
                return await AverageAsync(dbContext.OtherVideos.SelectMany(m => m.MediaVersions).Select(v => v.Duration.Ticks), cancellationToken);
            case ContentSourceKind.Programme:
            {
                Option<Programme> linked = await FindLinked(segment, cancellationToken);
                foreach (Programme p in linked.Where(p => !visited.Contains(p.Slug)))
                {
                    visited.Add(p.Slug);
                    TimeSpan sum = TimeSpan.Zero;
                    foreach (ProgrammeSegment s in p.Segments)
                    {
                        TimeSpan? e = await EstimateEach(dbContext, s, visited, cancellationToken);
                        if (e is null)
                        {
                            return null;
                        }

                        sum += e.Value * Math.Max(1, s.Count);
                    }

                    return sum;
                }

                return null;
            }
            default:
                return null;
        }
    }

    private static async Task<TimeSpan?> AverageAsync(IQueryable<long> ticks, CancellationToken cancellationToken)
    {
        List<long> values = await ticks.ToListAsync(cancellationToken);
        return values.Count == 0 ? null : TimeSpan.FromTicks((long)values.Average());
    }

    private async Task<Option<BaseError>> EnsureSmartCollection(Programme programme, ProgrammeSegment segment, CancellationToken cancellationToken)
    {
        if (segment.SourceKind != ContentSourceKind.Search || string.IsNullOrWhiteSpace(segment.Query))
        {
            return Option<BaseError>.None;
        }

        // ErsatzTV limite les noms à 50 caractères
        string name = $"{Cut(programme.Name, 22)} · {Cut(string.IsNullOrWhiteSpace(segment.Label) ? segment.Role.ToString() : segment.Label, 14)} #{segment.Id}";
        if (segment.SourceId is { } existing)
        {
            Either<BaseError, UpdateSmartCollectionResult> updated =
                await mediator.Send(new UpdateSmartCollection(existing, name, segment.Query), cancellationToken);
            if (updated.IsRight)
            {
                return Option<BaseError>.None;
            }
        }

        Either<BaseError, SmartCollectionViewModel> created =
            await mediator.Send(new CreateSmartCollection(segment.Query, name), cancellationToken);
        foreach (BaseError error in created.LeftToSeq())
        {
            return BaseError.New($"Recherche « {segment.Query} » : {error.Value}");
        }

        segment.SourceId = created.RightToSeq().Head().Id;
        return Option<BaseError>.None;
    }

    private static async Task<Option<BaseError>> Flatten(
        Programme programme,
        List<ProgrammeSegment> output,
        System.Collections.Generic.HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        foreach (ProgrammeSegment segment in programme.Segments)
        {
            if (segment.SourceKind == ContentSourceKind.Programme)
            {
                Option<Programme> linked = await FindLinked(segment, cancellationToken);
                if (linked.IsNone)
                {
                    return BaseError.New($"Le programme lié « {segment.SourceName} » est introuvable.");
                }

                foreach (Programme child in linked)
                {
                    if (!visited.Add(child.Slug))
                    {
                        return BaseError.New($"« {child.Name} » s'inclut lui-même (boucle entre programmes liés).");
                    }

                    for (var i = 0; i < Math.Max(1, segment.Count); i++)
                    {
                        Option<BaseError> error = await Flatten(child, output, visited, cancellationToken);
                        foreach (BaseError e in error)
                        {
                            return e;
                        }
                    }

                    visited.Remove(child.Slug);
                }
            }
            else if (HasSource(segment))
            {
                output.Add(segment);
            }
        }

        return Option<BaseError>.None;
    }

    private static async Task<Option<Programme>> FindLinked(ProgrammeSegment segment, CancellationToken cancellationToken)
    {
        foreach (ProgrammeSummary s in await ReadSummaries(cancellationToken))
        {
            if ((segment.SourceId.HasValue && s.PlaylistId == segment.SourceId) || s.Slug == segment.Query)
            {
                return await Load(PathFor(s.Slug), cancellationToken);
            }
        }

        return Option<Programme>.None;
    }

    private static string Cut(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..(max - 1)].TrimEnd() + "…";

    private static bool HasSource(ProgrammeSegment s) =>
        s.SourceKind == ContentSourceKind.Search ? s.SourceId.HasValue : s.SourceId.HasValue;

    private static async Task<PlaylistGroup> EnsureGroup(TvContext dbContext, string name, CancellationToken cancellationToken)
    {
        PlaylistGroup group = await dbContext.PlaylistGroups.FirstOrDefaultAsync(g => g.Name == name, cancellationToken);
        if (group is null)
        {
            group = new PlaylistGroup { Name = name, Playlists = [] };
            await dbContext.PlaylistGroups.AddAsync(group, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return group;
    }

    private static async Task<Playlist> EnsurePlaylist(TvContext dbContext, PlaylistGroup group, int? id, string name, CancellationToken cancellationToken)
    {
        Playlist playlist = id is { } existing
            ? await dbContext.Playlists.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == existing, cancellationToken)
            : null;
        if (playlist is null)
        {
            playlist = new Playlist { PlaylistGroupId = group.Id, Items = [] };
            await dbContext.Playlists.AddAsync(playlist, cancellationToken);
        }

        playlist.Name = name;
        playlist.PlaylistGroupId = group.Id;
        await dbContext.SaveChangesAsync(cancellationToken);
        return playlist;
    }

    private static void ReplaceItems(TvContext dbContext, Playlist playlist, List<ProgrammeSegment> segments)
    {
        dbContext.PlaylistItems.RemoveRange(playlist.Items);
        playlist.Items.Clear();

        var index = 0;
        foreach (ProgrammeSegment s in segments)
        {
            CollectionType type = s.SourceKind == ContentSourceKind.MediaItem
                ? s.ItemType
                : ContentCatalog.ToCollectionType(s.SourceKind);
            playlist.Items.Add(
                new PlaylistItem
                {
                    Index = index++,
                    CollectionType = type,
                    CollectionId = type == CollectionType.Collection ? s.SourceId : null,
                    MultiCollectionId = type == CollectionType.MultiCollection ? s.SourceId : null,
                    SmartCollectionId = type == CollectionType.SmartCollection ? s.SourceId : null,
                    MediaItemId = s.SourceKind is ContentSourceKind.MediaItem or ContentSourceKind.Show ? s.SourceId : null,
                    PlaybackOrder = s.SourceKind == ContentSourceKind.MediaItem ? PlaybackOrder.Chronological : s.Order,
                    Count = Math.Max(1, s.Count),
                    PlayAll = false,
                    IncludeInProgramGuide = s.IsInGuide
                });
        }
    }

    private async Task RefreshPlayoutsUsing(TvContext dbContext, CancellationToken cancellationToken)
    {
        var playlistIds = (await ReadSummaries(cancellationToken)).Where(p => p.PlaylistId.HasValue).Select(p => p.PlaylistId!.Value).ToList();
        List<int> playoutIds = await dbContext.Playouts
            .Where(p => p.ProgramSchedule.Items.Any(i => i.PlaylistId != null && playlistIds.Contains(i.PlaylistId.Value)))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        foreach (int playoutId in playoutIds)
        {
            // reconstruction complète : la modification du programme doit s'appliquer aux créneaux déjà construits
            await workerChannel.WriteAsync(new BuildPlayout(playoutId, PlayoutBuildMode.Reset), cancellationToken);
        }
    }

    private static string PathFor(string slug) =>
        Path.Combine(FileSystemLayout.ProgrammesFolder, $"{HabillageService.Slugify(slug)}.json");

    private static string UniqueSlug(string slug)
    {
        string candidate = slug;
        var i = 2;
        while (File.Exists(PathFor(candidate)))
        {
            candidate = $"{slug}-{i++}";
        }

        return candidate;
    }

    private static async Task<Option<Programme>> Load(string file, CancellationToken cancellationToken)
    {
        if (!File.Exists(file))
        {
            return Option<Programme>.None;
        }

        try
        {
            Programme p = JsonSerializer.Deserialize<Programme>(await File.ReadAllTextAsync(file, cancellationToken), JsonOptions);
            if (p is null)
            {
                return Option<Programme>.None;
            }

            p.Slug = Path.GetFileNameWithoutExtension(file);
            return p;
        }
        catch (JsonException)
        {
            return Option<Programme>.None;
        }
    }
}
