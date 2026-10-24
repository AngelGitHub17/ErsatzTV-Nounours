using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ErsatzTV.Application.Nounours;
using ErsatzTV.Application.Search;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Nounours;

/// <summary>Un film d'une collection (pour l'aperçu).</summary>
public sealed record CollectionFilm(int MovieId, string Title, int? Year);

/// <summary>Une époque de films : « moins de 5 ans », « 10 à 20 ans »…</summary>
public sealed record FilmAge(string Key, string Name, string Query, int Count, int? CollectionId);

/// <summary>Où la saga a été trouvée.</summary>
public enum SagaSource
{
    /// <summary>Collection Jellyfin / Plex / Emby.</summary>
    Server,

    /// <summary>&lt;set&gt; des fichiers .nfo.</summary>
    Nfo,

    /// <summary>Titres qui se ressemblent (« Scream », « Scream 2 »…).</summary>
    Titles,

    /// <summary>Ajoutée à la main.</summary>
    Manual
}

/// <summary>Une saga détectée (ou ajoutée) : son nom, sa requête de recherche et ses films dans l'ordre de sortie.</summary>
public sealed class Saga
{
    public string Name { get; set; } = string.Empty;
    public SagaSource Source { get; set; }
    public string Query { get; set; } = string.Empty;
    public List<CollectionFilm> Films { get; set; } = [];
    public int? CollectionId { get; set; }
    public bool Selected { get; set; } = true;

    public string CollectionName => MovieCollectionService.SagaPrefix + Name.Trim();
}

/// <summary>
///     Collections intelligentes de films : par époque (récents, un peu anciens, très anciens) et par saga
///     (Harry Potter, Scream…). Les sagas viennent des collections de Jellyfin / Plex, du &lt;set&gt; des .nfo,
///     ou des titres qui se ressemblent. Les collections suivent la médiathèque toutes seules.
/// </summary>
public sealed class MovieCollectionService(
    IMediator mediator,
    IDbContextFactory<TvContext> dbContextFactory,
    AutoProgrammingService auto,
    ILogger<MovieCollectionService> logger)
{
    public const string SagaPrefix = "Saga · ";
    public const string AgePrefix = "Films · ";

    private static readonly (string Key, string Name, string Query)[] AgeDefinitions =
    [
        ("nouveautes", "Nouveautés (moins de 5 ans)", "type:movie AND released_inthelast:\"5 years\""),
        ("recents", "Films récents (moins de 10 ans)", "type:movie AND released_inthelast:\"10 years\""),
        ("anciens", "Un peu anciens (10 à 20 ans)", "type:movie AND released_notinthelast:\"10 years\" AND released_inthelast:\"20 years\""),
        ("classiques", "Très anciens (plus de 20 ans)", "type:movie AND released_notinthelast:\"20 years\"")
    ];

    // mots qui ne suffisent pas à faire une saga (« Le », « La »…)
    private static readonly System.Collections.Generic.HashSet<string> StopWords =
    [
        "le", "la", "les", "l", "un", "une", "des", "de", "du", "d", "et", "a", "au", "aux", "en", "the", "an", "of", "and", "in", "on", "mon", "ma",
        "mes", "son", "sa", "ses", "ce", "cette", "il", "elle", "qui", "que"
    ];

    private static readonly Regex Sequel = new(
        @"(\s+(\d{1,2}|i{1,3}|iv|v|vi{0,3}|ix|x|xi{0,3}|partie\s+\d+|part\s+\d+|chapitre\s+\d+|chapter\s+\d+|episode\s+\d+))+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string AgeQuery(string key) => AgeDefinitions.First(a => a.Key == key).Query;

    // ------------------------------------------------------------------ époques

    public async Task<List<FilmAge>> Ages(CancellationToken cancellationToken)
    {
        Dictionary<string, int> existing = await ExistingCollections(cancellationToken);
        var result = new List<FilmAge>();
        foreach ((string key, string name, string query) in AgeDefinitions)
        {
            result.Add(new FilmAge(key, name, query, (await Films(query, cancellationToken)).Count, existing.TryGetValue(AgePrefix + name, out int id) ? id : null));
        }

        return result;
    }

    public async Task<Either<BaseError, int>> CreateAges(CancellationToken cancellationToken)
    {
        var count = 0;
        foreach ((_, string name, string query) in AgeDefinitions)
        {
            Either<BaseError, int> created = await auto.EnsureCollection(AgePrefix + name, query, cancellationToken);
            foreach (BaseError error in created.LeftToSeq())
            {
                return error;
            }

            count++;
        }

        return count;
    }

    // ------------------------------------------------------------------ sagas

    /// <summary>Les sagas de la médiathèque, de la source la plus sûre (Jellyfin / Plex, .nfo) aux titres qui se ressemblent.</summary>
    public async Task<List<Saga>> DetectSagas(CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<int> excluded = await db.LocalLibraries
            .Where(l => l.Name.StartsWith("Habillage ·"))
            .Select(l => l.Id)
            .ToListAsync(cancellationToken);
        List<MovieMetadata> movies = await db.MovieMetadata
            .AsNoTracking()
            .Include(m => m.Tags)
            .Include(m => m.Movie).ThenInclude(m => m.LibraryPath)
            .Where(m => !excluded.Contains(m.Movie.LibraryPath.LibraryId))
            .ToListAsync(cancellationToken);

        var sagas = new List<Saga>();
        var taken = new System.Collections.Generic.HashSet<int>();

        // 1. collections du serveur (Jellyfin, Plex, Emby) et <set> des .nfo
        foreach (IGrouping<(string Name, bool Nfo), MovieMetadata> group in movies
                     .SelectMany(m => m.Tags
                         .Where(t => !string.IsNullOrWhiteSpace(t.ExternalCollectionId) || t.ExternalTypeId == Tag.NfoSetTypeId)
                         .Select(t => (Tag: t, Movie: m)))
                     .GroupBy(x => (x.Tag.Name.Trim(), x.Tag.ExternalTypeId == Tag.NfoSetTypeId), x => x.Movie))
        {
            var films = group.DistinctBy(m => m.MovieId).Where(m => !taken.Contains(m.MovieId)).ToList();
            if (films.Count < 2)
            {
                continue;
            }

            string tag = group.Key.Name.Replace("\"", string.Empty, StringComparison.Ordinal);
            sagas.Add(new Saga
            {
                Name = CleanSagaName(group.Key.Name),
                Source = group.Key.Nfo ? SagaSource.Nfo : SagaSource.Server,
                Query = $"type:movie AND tag:\"{tag}\"",
                Films = Ordered(films)
            });
            taken.UnionWith(films.Select(f => f.MovieId));
        }

        // 2. titres qui se ressemblent : même titre sans le numéro ni le sous-titre (« Scream 2 », « Star Wars : … »)
        //    et les suites dont le titre commence par celui du premier (« Les Bronzés », « Les Bronzés font du ski »)
        var rest = movies.Where(m => !taken.Contains(m.MovieId) && !string.IsNullOrWhiteSpace(m.Title)).ToList();
        foreach (MovieMetadata candidate in rest.OrderBy(m => Normalize(BaseTitle(m.Title)).Length).ToList())
        {
            string key = Normalize(BaseTitle(candidate.Title));
            if (taken.Contains(candidate.MovieId) || !Words(key).Any(w => w.Length >= 4 && !StopWords.Contains(w)))
            {
                continue;
            }

            var films = rest.Where(m => !taken.Contains(m.MovieId))
                .Where(m => Normalize(BaseTitle(m.Title)) == key || Normalize(m.Title).StartsWith(key + " ", StringComparison.Ordinal))
                .ToList();
            if (films.Count < 2)
            {
                continue;
            }

            string name = BaseTitle(candidate.Title);
            sagas.Add(new Saga { Name = name, Source = SagaSource.Titles, Query = TitleQuery(name), Films = Ordered(films) });
            taken.UnionWith(films.Select(f => f.MovieId));
        }

        // 3. mêmes deux premiers mots significatifs (« Harry Potter à l'école… », « Harry Potter et la Chambre… »)
        rest = rest.Where(m => !taken.Contains(m.MovieId)).ToList();
        foreach (IGrouping<string, MovieMetadata> group in rest.GroupBy(m => Prefix(m.Title)).Where(g => g.Key.Length > 0 && g.Count() >= 2))
        {
            List<MovieMetadata> films = group.ToList();
            string name = PrefixOriginal(Ordered(films).Select(f => films.First(m => m.MovieId == f.MovieId)).First().Title);
            sagas.Add(new Saga { Name = name, Source = SagaSource.Titles, Query = TitleQuery(name), Films = Ordered(films) });
            taken.UnionWith(films.Select(f => f.MovieId));
        }

        Dictionary<string, int> existing = await ExistingCollections(cancellationToken);
        foreach (Saga saga in sagas)
        {
            saga.CollectionId = existing.TryGetValue(saga.CollectionName, out int id) ? id : null;

            // ce que la collection contiendra vraiment (la recherche fait foi ; vide si l'index n'est pas encore à jour)
            List<CollectionFilm> found = await Films(saga.Query, cancellationToken);
            if (found.Count > 0)
            {
                saga.Films = found;
            }
        }

        // sagas déjà créées à la main (ou renommées) : on les montre aussi
        foreach ((string name, int id) in existing.Where(e => e.Key.StartsWith(SagaPrefix, StringComparison.Ordinal)))
        {
            if (sagas.Any(s => s.CollectionName == name))
            {
                continue;
            }

            string query = await db.SmartCollections.Where(c => c.Id == id).Select(c => c.Query).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
            sagas.Add(new Saga { Name = name[SagaPrefix.Length..], Source = SagaSource.Manual, Query = query, CollectionId = id, Films = await Films(query, cancellationToken) });
        }

        return sagas.OrderBy(s => s.Name, StringComparer.Create(CultureInfo.GetCultureInfo("fr-FR"), true)).ToList();
    }

    /// <summary>Les films que trouve une requête, dans l'ordre de sortie (pour l'aperçu).</summary>
    public async Task<List<CollectionFilm>> Films(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        try
        {
            SearchResultAllItemsViewModel items = await mediator.Send(new QuerySearchIndexAllItems(query), cancellationToken);
            await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            List<MovieMetadata> movies = await db.MovieMetadata.AsNoTracking().Where(m => items.MovieIds.Contains(m.MovieId)).ToListAsync(cancellationToken);
            return Ordered(movies);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Collections de films : requête {Query} impossible", query);
            return [];
        }
    }

    public async Task<Either<BaseError, int>> CreateSaga(Saga saga, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(saga.Name) || string.IsNullOrWhiteSpace(saga.Query))
        {
            return BaseError.New("Une saga a besoin d'un nom et d'une recherche.");
        }

        Either<BaseError, int> result = await auto.EnsureCollection(saga.CollectionName, saga.Query.Trim(), cancellationToken);
        foreach (int id in result.RightToSeq())
        {
            saga.CollectionId = id;
        }

        return result;
    }

    /// <summary>Une saga ajoutée à la main : les films dont le titre contient ces mots.</summary>
    public static string TitleQuery(string words) =>
        $"type:movie AND title:\"{(words ?? string.Empty).Replace("\"", string.Empty, StringComparison.Ordinal).Trim()}\"";

    private async Task<Dictionary<string, int>> ExistingCollections(CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.SmartCollections.AsNoTracking()
            .Where(c => c.Name.StartsWith(SagaPrefix) || c.Name.StartsWith(AgePrefix))
            .ToDictionaryAsync(c => c.Name, c => c.Id, cancellationToken);
    }

    private static List<CollectionFilm> Ordered(IEnumerable<MovieMetadata> movies) =>
        movies
            .DistinctBy(m => m.MovieId)
            .OrderBy(m => m.ReleaseDate ?? new DateTime(m.Year ?? 9999, 1, 1))
            .ThenBy(m => m.Title)
            .Select(m => new CollectionFilm(m.MovieId, m.Title, m.Year ?? m.ReleaseDate?.Year))
            .ToList();

    /// <summary>« Harry Potter Collection » → « Harry Potter », « Saga Scream » → « Scream ».</summary>
    public static string CleanSagaName(string name)
    {
        string cleaned = Regex.Replace(name.Trim(), @"(\s*[-–:]?\s*(collection|saga|la saga|the collection|trilogie|trilogy|- coffret|coffret))+$", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"^(la saga|saga|collection)\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
        return cleaned.Length == 0 ? name.Trim() : cleaned;
    }

    /// <summary>Le titre sans sous-titre ni numéro : « Scream 2 » → « Scream », « Star Wars : Episode IV » → « Star Wars ».</summary>
    public static string BaseTitle(string title)
    {
        string t = title.Trim();
        int cut = t.IndexOfAny([':', '–']);
        int dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && (cut < 0 || dash < cut))
        {
            cut = dash;
        }

        if (cut > 0)
        {
            t = t[..cut];
        }

        return Sequel.Replace(t.Trim(), string.Empty).Trim();
    }

    public static string Normalize(string text)
    {
        string decomposed = (text ?? string.Empty).ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (char c in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    private static List<string> Words(string title) => Normalize(title).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>Les deux premiers mots, s'ils sont significatifs tous les deux (« harry potter »), sinon rien.</summary>
    public static string Prefix(string title)
    {
        List<string> words = Words(title);
        if (words.Count < 3 || StopWords.Contains(words[0]) || StopWords.Contains(words[1]) || words[0].Length < 3 || words[1].Length < 3)
        {
            return string.Empty;
        }

        return $"{words[0]} {words[1]}";
    }

    private static string PrefixOriginal(string title)
    {
        string[] words = title.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(2));
    }
}
