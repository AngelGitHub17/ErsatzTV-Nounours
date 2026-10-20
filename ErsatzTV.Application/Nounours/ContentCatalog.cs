using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Nounours;

public enum ContentSourceKind
{
    /// <summary>Préréglage ou recherche libre : crée une collection intelligente.</summary>
    Search,
    Show,
    Collection,
    SmartCollection,
    MultiCollection,
    Playlist,

    /// <summary>Un fichier précis (jingle, générique, bande-annonce…).</summary>
    MediaItem,

    /// <summary>Un programme composé (édition Nounours).</summary>
    Programme
}

public sealed record ContentOption(
    ContentSourceKind Kind,
    int Id,
    string Name,
    string Detail,
    CollectionType ItemType = CollectionType.Collection,
    TimeSpan? Duration = null);

/// <summary>Tout ce qu'on peut programmer sur une chaîne, en une seule liste lisible.</summary>
public interface IContentCatalog
{
    Task<List<ContentOption>> GetAll(CancellationToken cancellationToken);

    /// <summary>Recherche de fichiers précis (autres vidéos, films, clips) par titre.</summary>
    Task<List<ContentOption>> SearchItems(string text, CancellationToken cancellationToken);
}

public sealed class ContentCatalog(IDbContextFactory<TvContext> dbContextFactory) : IContentCatalog
{
    public async Task<List<ContentOption>> GetAll(CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var result = new List<ContentOption>();

        var shows = await dbContext.Shows
            .AsNoTracking()
            .Include(s => s.ShowMetadata)
            .Select(s => new { s.Id, Meta = s.ShowMetadata.Select(m => new { m.Title, m.Year }).FirstOrDefault() })
            .ToListAsync(cancellationToken);
        result.AddRange(
            shows.Where(s => s.Meta != null)
                .Select(s => new ContentOption(
                    ContentSourceKind.Show,
                    s.Id,
                    s.Meta.Title,
                    s.Meta.Year.HasValue ? $"Série · {s.Meta.Year}" : "Série")));

        result.AddRange(
            (await dbContext.Collections.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync(cancellationToken))
            .Select(c => new ContentOption(ContentSourceKind.Collection, c.Id, c.Name, "Collection")));

        result.AddRange(
            (await dbContext.SmartCollections.AsNoTracking().Select(c => new { c.Id, c.Name, c.Query }).ToListAsync(cancellationToken))
            .Select(c => new ContentOption(ContentSourceKind.SmartCollection, c.Id, c.Name, $"Collection intelligente · {c.Query}")));

        result.AddRange(
            (await dbContext.MultiCollections.AsNoTracking().Select(c => new { c.Id, c.Name }).ToListAsync(cancellationToken))
            .Select(c => new ContentOption(ContentSourceKind.MultiCollection, c.Id, c.Name, "Multi-collection")));

        result.AddRange(
            (await dbContext.Playlists.AsNoTracking()
                .Where(p => !p.IsSystem && p.PlaylistGroup.Name != ProgrammeService.PlaylistGroupName && p.PlaylistGroup.Name != ProgrammeService.BreakPlaylistGroupName)
                .Select(p => new { p.Id, p.Name }).ToListAsync(cancellationToken))
            .Select(p => new ContentOption(ContentSourceKind.Playlist, p.Id, p.Name, "Playlist")));

        foreach (ProgrammeSummary programme in await ProgrammeService.ReadSummaries(cancellationToken))
        {
            result.Add(new ContentOption(ContentSourceKind.Programme, programme.PlaylistId ?? 0, programme.Name, $"Programme · {programme.Segments} segment(s)"));
        }

        return result.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<List<ContentOption>> SearchItems(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 2)
        {
            return [];
        }

        string pattern = "%" + text.Trim().Replace("%", string.Empty, StringComparison.Ordinal) + "%";
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var others = await dbContext.OtherVideos.AsNoTracking()
            .Where(v => v.OtherVideoMetadata.Any(m => EF.Functions.Like(m.Title, pattern)))
            .Select(v => new { v.Id, Title = v.OtherVideoMetadata.First().Title, Duration = v.MediaVersions.Select(mv => mv.Duration).FirstOrDefault() })
            .OrderBy(v => v.Title).Take(25).ToListAsync(cancellationToken);
        var movies = await dbContext.Movies.AsNoTracking()
            .Where(v => v.MovieMetadata.Any(m => EF.Functions.Like(m.Title, pattern)))
            .Select(v => new { v.Id, Title = v.MovieMetadata.First().Title, Duration = v.MediaVersions.Select(mv => mv.Duration).FirstOrDefault() })
            .OrderBy(v => v.Title).Take(25).ToListAsync(cancellationToken);
        var clips = await dbContext.MusicVideos.AsNoTracking()
            .Where(v => v.MusicVideoMetadata.Any(m => EF.Functions.Like(m.Title, pattern)))
            .Select(v => new { v.Id, Title = v.MusicVideoMetadata.First().Title, Duration = v.MediaVersions.Select(mv => mv.Duration).FirstOrDefault() })
            .OrderBy(v => v.Title).Take(25).ToListAsync(cancellationToken);

        return others.Select(o => new ContentOption(ContentSourceKind.MediaItem, o.Id, o.Title, $"Vidéo · {Format(o.Duration)}", CollectionType.OtherVideo, o.Duration))
            .Concat(movies.Select(o => new ContentOption(ContentSourceKind.MediaItem, o.Id, o.Title, $"Film · {Format(o.Duration)}", CollectionType.Movie, o.Duration)))
            .Concat(clips.Select(o => new ContentOption(ContentSourceKind.MediaItem, o.Id, o.Title, $"Clip · {Format(o.Duration)}", CollectionType.MusicVideo, o.Duration)))
            .OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static string Format(TimeSpan duration) =>
        duration.TotalHours >= 1 ? $"{(int)duration.TotalHours} h {duration.Minutes:00}" :
        duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes} min {duration.Seconds:00}" : $"{duration.Seconds} s";

    public static CollectionType ToCollectionType(ContentSourceKind kind) =>
        kind switch
        {
            ContentSourceKind.Show => CollectionType.TelevisionShow,
            ContentSourceKind.Collection => CollectionType.Collection,
            ContentSourceKind.MultiCollection => CollectionType.MultiCollection,
            ContentSourceKind.Playlist => CollectionType.Playlist,
            ContentSourceKind.Programme => CollectionType.Playlist,
            _ => CollectionType.SmartCollection
        };
}
