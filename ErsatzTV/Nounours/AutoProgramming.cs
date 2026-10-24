using System.Globalization;
using System.Threading.Channels;
using MediatR;
using ErsatzTV.Application;
using ErsatzTV.Application.MediaCollections;
using ErsatzTV.Application.Nounours;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Application.ProgramSchedules;
using ErsatzTV.Application.Search;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Nounours;

/// <summary>Les grandes familles de programmes qu'on sait reconnaître dans la médiathèque.</summary>
public enum AutoCategory
{
    Jeunesse,
    Series,
    Film,
    FilmFamille,
    Documentaires,
    Clips,
    Info,
    Nuit,

    /// <summary>Films de moins de 10 ans.</summary>
    FilmRecent,

    /// <summary>Films de 10 à 20 ans.</summary>
    FilmAncien,

    /// <summary>Films de plus de 20 ans.</summary>
    FilmClassique
}

/// <summary>Un bloc de la grille : à partir de telle heure, telle famille de programmes (jusqu'au bloc suivant, ou un seul film).</summary>
public sealed class AutoBlock
{
    public TimeSpan Start { get; set; }
    public AutoCategory Category { get; set; }

    /// <summary>Un seul programme (le film du soir), sinon « en continu jusqu'au bloc suivant ».</summary>
    public bool One { get; set; }

    public string Label { get; set; } = string.Empty;
}

public sealed class AutoPlan
{
    public string Style { get; set; } = "generaliste";
    public List<AutoBlock> Week { get; set; } = [];
    public List<AutoBlock> Weekend { get; set; } = [];
    public bool WeekendDifferent { get; set; } = true;

    /// <summary>Un jingle avant chaque programme (collection « Jingles »).</summary>
    public bool Jingles { get; set; } = true;

    /// <summary>Des pubs entre les programmes (collection « Pubs »).</summary>
    public bool Pubs { get; set; } = true;

    /// <summary>Les programmes commencent au quart d'heure (00, 15, 30, 45), les pubs comblent l'écart.</summary>
    public bool QuarterHour { get; set; } = true;
}

/// <summary>Ce que la médiathèque contient pour une famille : la requête retenue (ou son repli) et le nombre de programmes.</summary>
public sealed record CategoryInfo(AutoCategory Category, string Name, string Query, int Count, bool Fallback, double AvgMinutes = 0);

public sealed record AutoStyle(string Key, string Name, string Description);

/// <summary>
///     Programmation automatique : regarde ce que contient la médiathèque (dessins animés, séries, films, documentaires,
///     clips, flux d'info), propose une grille de journée façon télé (matinale jeunesse, séries, film de l'après-midi,
///     film du soir à 20h45, nuit en rediffusion) et un week-end différent, puis l'applique à une chaîne.
///     Les familles sont des collections intelligentes : les nouveaux médias y entrent tout seuls.
/// </summary>
public sealed class AutoProgrammingService(
    IMediator mediator,
    IDbContextFactory<TvContext> dbContextFactory,
    ChannelWizardService wizard,
    IWeekTemplateService weekTemplates,
    ChannelWriter<IBackgroundServiceRequest> worker,
    ErsatzTV.Application.Nounours.IInfoService info,
    ILogger<AutoProgrammingService> logger)
{
    private static readonly string[] KidsGenres = ["animation", "family", "kids", "children", "jeunesse", "enfants", "familial"];
    private static readonly string[] FamilyMovieGenres = ["family", "animation", "comedy", "adventure", "familial", "comédie", "aventure"];
    private static readonly string[] DocGenres = ["documentary", "documentaire", "news", "reality"];

    public static readonly AutoStyle[] Styles =
    [
        new("generaliste", "Généraliste", "Jeunesse le matin, séries, film l'après-midi, film du soir à 20h45, nuit en rediffusion."),
        new("jeunesse", "Jeunesse", "Dessins animés et séries pour enfants toute la journée, un film famille l'après-midi et le soir."),
        new("cinema", "Cinéma", "Des films en continu, un film famille à 14h, le grand film à 20h45."),
        new("series", "Séries", "Les séries à la suite, comme une chaîne de séries, avec une matinale jeunesse."),
        new("info", "Info", "Les flux d'info en continu, entrecoupés de documentaires et magazines."),
        new("musique", "Musique", "Des clips toute la journée, un documentaire le soir.")
    ];

    public static string CategoryName(AutoCategory category) =>
        category switch
        {
            AutoCategory.Jeunesse => "Jeunesse",
            AutoCategory.Series => "Séries",
            AutoCategory.Film => "Films",
            AutoCategory.FilmFamille => "Films famille",
            AutoCategory.Documentaires => "Documentaires et magazines",
            AutoCategory.Clips => "Clips",
            AutoCategory.Info => "Info en direct",
            AutoCategory.FilmRecent => "Films récents (moins de 10 ans)",
            AutoCategory.FilmAncien => "Films de 10 à 20 ans",
            AutoCategory.FilmClassique => "Grands classiques (plus de 20 ans)",
            _ => "Nuit (rediffusions)"
        };

    // ------------------------------------------------------------------ médiathèque

    /// <summary>Pour chaque famille : la requête de recherche, et combien de programmes elle trouve (avec repli si rien).</summary>
    public async Task<Dictionary<AutoCategory, CategoryInfo>> Analyze(CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // l'habillage (jingles, pubs…) et les flux en direct ne sont pas des programmes
        List<int> excluded = await db.LocalLibraries
            .Where(l => l.Name.StartsWith("Habillage ·") || l.Name == FolderImportService.Label(ImportRole.Direct))
            .Select(l => l.Id)
            .ToListAsync(cancellationToken);
        int? direct = await db.LocalLibraries.Where(l => l.Name == FolderImportService.Label(ImportRole.Direct)).Select(l => (int?)l.Id).FirstOrDefaultAsync(cancellationToken);
        string notHabillage = string.Concat(excluded.Select(id => $" AND NOT library_id:{id}"));

        string Any(string field, IEnumerable<string> values) => "(" + string.Join(" OR ", values.Select(v => v.Contains(' ') ? $"{field}:\"{v}\"" : $"{field}:{v}")) + ")";
        string kidsShows = Any("show_genre", KidsGenres);
        string kidsMovies = Any("genre", KidsGenres);
        string docs = $"({Any("genre", DocGenres)} OR {Any("show_genre", DocGenres)})";

        var candidates = new (AutoCategory Category, string[] Queries)[]
        {
            (AutoCategory.Jeunesse, [$"((type:episode AND {kidsShows}) OR (type:movie AND {kidsMovies}))", "type:episode"]),
            (AutoCategory.Series, [$"(type:episode AND NOT {kidsShows})", "type:episode", "type:movie"]),
            (AutoCategory.Film, ["type:movie", "type:episode"]),
            (AutoCategory.FilmFamille, [$"(type:movie AND {Any("genre", FamilyMovieGenres)})", "type:movie", "type:episode"]),
            (AutoCategory.Documentaires, [$"({docs} OR (type:other_video{notHabillage}))", "type:episode"]),
            (AutoCategory.Clips, ["type:music_video", "type:episode"]),
            (AutoCategory.Info, [direct is null ? "type:remote_stream AND NOT title:obs" : $"(type:remote_stream AND library_id:{direct} AND NOT title:obs)", $"({docs} OR (type:other_video{notHabillage}))", "type:episode"]),
            (AutoCategory.Nuit, ["(type:movie OR type:episode)", "type:episode"]),
            (AutoCategory.FilmRecent, [MovieCollectionService.AgeQuery("recents"), "type:movie"]),
            (AutoCategory.FilmAncien, [MovieCollectionService.AgeQuery("anciens"), "type:movie"]),
            (AutoCategory.FilmClassique, [MovieCollectionService.AgeQuery("classiques"), "type:movie"])
        };

        var result = new Dictionary<AutoCategory, CategoryInfo>();
        foreach ((AutoCategory category, string[] queries) in candidates)
        {
            CategoryInfo chosen = null;
            for (var i = 0; i < queries.Length; i++)
            {
                int count = await Count(queries[i]);
                if (count > 0 || i == queries.Length - 1)
                {
                    chosen = new CategoryInfo(category, CategoryName(category), queries[i], count, i > 0, count > 0 ? await AverageMinutes(queries[i]) : 0);
                    break;
                }
            }

            result[category] = chosen;
        }

        return result;
    }

    /// <summary>
    ///     « Remplir les trous » : ce qui passe quand la grille d'une chaîne n'a rien de prévu (remplissage de secours),
    ///     pris dans une collection intelligente.
    /// </summary>
    public async Task<string> FillGaps(int channelId, int smartCollectionId, CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        SmartCollection collection = await db.SmartCollections.FirstOrDefaultAsync(c => c.Id == smartCollectionId, cancellationToken);
        ErsatzTV.Core.Domain.Channel channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (collection is null || channel is null)
        {
            return "Chaîne ou collection introuvable.";
        }

        string name = $"Combler les trous · {collection.Name}";
        FillerPreset preset = await db.FillerPresets.FirstOrDefaultAsync(p => p.Name == name, cancellationToken);
        if (preset is null)
        {
            preset = new FillerPreset { Name = name };
            await db.FillerPresets.AddAsync(preset, cancellationToken);
        }

        preset.FillerKind = FillerKind.Fallback;
        preset.FillerMode = FillerMode.None;
        preset.CollectionType = CollectionType.SmartCollection;
        preset.SmartCollectionId = smartCollectionId;
        preset.CollectionId = null;
        preset.MultiCollectionId = null;
        preset.MediaItemId = null;
        preset.PlaylistId = null;
        await db.SaveChangesAsync(cancellationToken);
        channel.FallbackFillerId = preset.Id;
        await db.SaveChangesAsync(cancellationToken);
        return $"Les trous de « {channel.Name} » sont comblés par « {collection.Name} ».";
    }

    /// <summary>Durée moyenne d'un programme de la famille (pour estimer tous les combien il revient).</summary>
    private async Task<double> AverageMinutes(string query)
    {
        try
        {
            SearchResultAllItemsViewModel items = await mediator.Send(new QuerySearchIndexAllItems(query));
            await using TvContext db = await dbContextFactory.CreateDbContextAsync();
            var durations = new List<TimeSpan>();
            List<int> movies = items.MovieIds.Take(2000).ToList();
            List<int> episodes = items.EpisodeIds.Take(2000).ToList();
            List<int> others = items.OtherVideoIds.Take(2000).ToList();
            List<int> clips = items.MusicVideoIds.Take(2000).ToList();
            durations.AddRange(await db.Movies.Where(m => movies.Contains(m.Id)).Select(m => m.MediaVersions.Select(v => v.Duration).FirstOrDefault()).ToListAsync());
            durations.AddRange(await db.Episodes.Where(m => episodes.Contains(m.Id)).Select(m => m.MediaVersions.Select(v => v.Duration).FirstOrDefault()).ToListAsync());
            durations.AddRange(await db.OtherVideos.Where(m => others.Contains(m.Id)).Select(m => m.MediaVersions.Select(v => v.Duration).FirstOrDefault()).ToListAsync());
            durations.AddRange(await db.MusicVideos.Where(m => clips.Contains(m.Id)).Select(m => m.MediaVersions.Select(v => v.Duration).FirstOrDefault()).ToListAsync());
            var valid = durations.Where(d => d > TimeSpan.Zero).ToList();
            // les directs n'ont pas de durée : 30 min (taille des blocs)
            return valid.Count == 0 ? 30 : valid.Average(d => d.TotalMinutes);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Programmation auto : durée moyenne de {Query} impossible", query);
            return 0;
        }
    }

    /// <summary>
    ///     Pour chaque famille : combien de programmes la grille consomme par semaine, et donc tous les combien de jours
    ///     un même programme revient (plus c'est court, plus ça se voit).
    /// </summary>
    public static Dictionary<AutoCategory, double> RepeatDays(AutoPlan plan, Dictionary<AutoCategory, CategoryInfo> library)
    {
        var minutes = new Dictionary<AutoCategory, double>();
        void Add(List<AutoBlock> blocks, int daysPerWeek)
        {
            var ordered = blocks.OrderBy(b => (b.Start - TimeSpan.FromHours(5)).Ticks < 0 ? b.Start.Add(TimeSpan.FromHours(19)) : b.Start - TimeSpan.FromHours(5)).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                AutoBlock block = ordered[i];
                TimeSpan start = block.Start;
                TimeSpan end = i + 1 < ordered.Count ? ordered[i + 1].Start : ordered[0].Start;
                double span = (end - start).TotalMinutes;
                if (span <= 0)
                {
                    span += 24 * 60;
                }

                double avg = Math.Max(1, library[block.Category].AvgMinutes);
                double perDay = block.One ? avg : span;
                minutes[block.Category] = minutes.GetValueOrDefault(block.Category) + perDay * daysPerWeek;
            }
        }

        if (plan.WeekendDifferent && plan.Weekend.Count > 0)
        {
            Add(plan.Week, 5);
            Add(plan.Weekend, 2);
        }
        else
        {
            Add(plan.Week, 7);
        }

        var result = new Dictionary<AutoCategory, double>();
        foreach ((AutoCategory category, double perWeek) in minutes)
        {
            CategoryInfo info = library[category];
            if (info.Count <= 0 || info.AvgMinutes <= 0 || category is AutoCategory.Info)
            {
                continue;
            }

            double programmesPerWeek = perWeek / info.AvgMinutes;
            result[category] = programmesPerWeek <= 0 ? double.PositiveInfinity : 7 * info.Count / programmesPerWeek;
        }

        return result;
    }

    private async Task<int> Count(string query)
    {
        try
        {
            SearchResultAllItemsViewModel items = await mediator.Send(new QuerySearchIndexAllItems(query));
            return items.MovieIds.Count + items.EpisodeIds.Count + items.MusicVideoIds.Count + items.OtherVideoIds.Count +
                   (items.RemoteStreamIds?.Count ?? 0);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Programmation auto : requête {Query} impossible", query);
            return 0;
        }
    }

    // ------------------------------------------------------------------ grilles prêtes

    private static AutoBlock B(int hour, int minute, AutoCategory category, bool one = false, string label = "") =>
        new() { Start = new TimeSpan(hour, minute, 0), Category = category, One = one, Label = label };

    /// <summary>Une grille de semaine et une de week-end pour le style choisi, adaptées à ce que contient la médiathèque.</summary>
    public static AutoPlan Propose(string style, Dictionary<AutoCategory, CategoryInfo> library)
    {
        bool hasInfo = library[AutoCategory.Info] is { Fallback: false, Count: > 0 };
        bool hasDocs = library[AutoCategory.Documentaires] is { Fallback: false, Count: > 0 };
        AutoCategory magazine = hasInfo ? AutoCategory.Info : hasDocs ? AutoCategory.Documentaires : AutoCategory.Series;

        var plan = new AutoPlan { Style = style };
        switch (style)
        {
            case "jeunesse":
                plan.Week =
                [
                    B(6, 0, AutoCategory.Jeunesse, label: "Le réveil des petits"), B(12, 0, AutoCategory.FilmFamille, true, "Film du midi"),
                    B(14, 0, AutoCategory.Jeunesse, label: "Dessins animés"), B(19, 0, AutoCategory.FilmFamille, true, "Film du soir"),
                    B(21, 0, AutoCategory.Jeunesse, label: "Avant de dormir"), B(23, 0, AutoCategory.Jeunesse, label: "Nuit")
                ];
                plan.Weekend =
                [
                    B(7, 0, AutoCategory.Jeunesse, label: "Matinée dessins animés"), B(11, 0, AutoCategory.FilmFamille, true, "Film du matin"),
                    B(13, 0, AutoCategory.Jeunesse), B(15, 0, AutoCategory.FilmFamille, true, "Film de l'après-midi"),
                    B(17, 0, AutoCategory.Jeunesse), B(19, 0, AutoCategory.FilmFamille, true, "Film du soir"), B(21, 0, AutoCategory.Jeunesse, label: "Nuit")
                ];
                break;
            case "cinema":
                plan.Week =
                [
                    B(6, 0, AutoCategory.FilmClassique, label: "Les grands classiques"), B(14, 0, AutoCategory.FilmFamille, true, "Séance de 14h"),
                    B(16, 0, AutoCategory.FilmAncien, label: "Cinéma"), B(20, 45, AutoCategory.FilmRecent, true, "Le film du soir"),
                    B(22, 45, AutoCategory.Film, label: "Seconde séance"), B(1, 0, AutoCategory.Nuit, label: "Nuit")
                ];
                plan.Weekend =
                [
                    B(7, 0, AutoCategory.FilmFamille, label: "Films en famille"), B(14, 0, AutoCategory.FilmFamille, true, "Séance de 14h"),
                    B(16, 0, AutoCategory.FilmClassique, true, "Le classique du week-end"), B(20, 45, AutoCategory.FilmRecent, true, "Le grand film"), B(22, 45, AutoCategory.Film, label: "Seconde séance"),
                    B(1, 0, AutoCategory.Nuit, label: "Nuit")
                ];
                break;
            case "series":
                plan.Week =
                [
                    B(6, 0, AutoCategory.Jeunesse, label: "Matinale jeunesse"), B(9, 0, AutoCategory.Series, label: "Séries"),
                    B(20, 45, AutoCategory.Series, label: "Soirée séries"), B(1, 0, AutoCategory.Nuit, label: "Nuit")
                ];
                plan.Weekend =
                [
                    B(7, 0, AutoCategory.Jeunesse, label: "Matinale jeunesse"), B(11, 0, AutoCategory.Series, label: "Marathon séries"),
                    B(20, 45, AutoCategory.Film, true, "Le film du week-end"), B(22, 45, AutoCategory.Series), B(1, 0, AutoCategory.Nuit, label: "Nuit")
                ];
                break;
            case "info":
                plan.Week =
                [
                    B(6, 0, AutoCategory.Info, label: "La matinale info"), B(12, 0, AutoCategory.Documentaires, label: "Magazines"),
                    B(13, 0, AutoCategory.Info, label: "Info"), B(19, 0, AutoCategory.Documentaires, label: "Documentaires"),
                    B(20, 0, AutoCategory.Info, label: "Info du soir"), B(23, 0, AutoCategory.Documentaires, label: "Nuit")
                ];
                plan.Weekend =
                [
                    B(7, 0, AutoCategory.Info, label: "Info"), B(10, 0, AutoCategory.Documentaires, label: "Magazines du week-end"),
                    B(13, 0, AutoCategory.Info), B(18, 0, AutoCategory.Documentaires), B(20, 0, AutoCategory.Info), B(23, 0, AutoCategory.Documentaires, label: "Nuit")
                ];
                break;
            case "musique":
                plan.Week =
                [
                    B(6, 0, AutoCategory.Clips, label: "Clips du matin"), B(20, 45, AutoCategory.Documentaires, true, "Le doc musique"),
                    B(22, 0, AutoCategory.Clips, label: "Clips de la nuit")
                ];
                plan.Weekend = [B(7, 0, AutoCategory.Clips, label: "Clips"), B(20, 45, AutoCategory.Documentaires, true), B(22, 0, AutoCategory.Clips)];
                break;
            default: // généraliste
                plan.Week =
                [
                    B(6, 0, AutoCategory.Jeunesse, label: "Matinale jeunesse"), B(9, 0, AutoCategory.Series, label: "Séries du matin"),
                    B(12, 0, magazine, label: hasInfo ? "Le journal" : "Magazine"), B(13, 0, AutoCategory.FilmFamille, true, "Le film de l'après-midi"),
                    B(15, 0, AutoCategory.Series, label: "Séries"), B(18, 0, AutoCategory.Jeunesse, label: "Jeunesse"),
                    B(19, 30, magazine, label: hasInfo ? "Le journal du soir" : "Avant-soirée"), B(20, 0, AutoCategory.Series, label: "Access"),
                    B(20, 45, AutoCategory.Film, true, "Le film du soir"), B(22, 45, AutoCategory.Series, label: "Deuxième partie de soirée"),
                    B(0, 30, AutoCategory.Nuit, label: "Nuit")
                ];
                plan.Weekend =
                [
                    B(7, 0, AutoCategory.Jeunesse, label: "Matinée jeunesse"), B(11, 0, magazine, label: "Magazine du week-end"),
                    B(12, 30, AutoCategory.Series), B(14, 0, AutoCategory.FilmFamille, true, "Film famille"), B(16, 0, AutoCategory.Film, true, "Film"),
                    B(18, 0, AutoCategory.Series), B(20, 0, magazine, label: hasInfo ? "Le journal" : "Magazine"),
                    B(20, 45, AutoCategory.Film, true, "Le grand film du week-end"), B(22, 45, AutoCategory.Film, true, "Seconde séance"),
                    B(0, 45, AutoCategory.Nuit, label: "Nuit")
                ];
                break;
        }

        return plan;
    }

    // ------------------------------------------------------------------ application

    /// <summary>Crée la chaîne (ou remplace la programmation d'une chaîne existante) et construit la grille.</summary>
    public async Task<Either<BaseError, List<string>>> Apply(
        AutoPlan plan,
        int? channelId,
        string newChannelName,
        string newChannelNumber,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var steps = new List<string>();
        void Report(string message)
        {
            steps.Add(message);
            progress?.Report(message);
        }

        Dictionary<AutoCategory, CategoryInfo> library = await Analyze(cancellationToken);

        // 1. une collection intelligente par famille utilisée (réutilisée et mise à jour d'une fois sur l'autre)
        var collections = new Dictionary<AutoCategory, int>();
        foreach (AutoCategory category in plan.Week.Concat(plan.Weekend).Select(b => b.Category).Distinct())
        {
            Either<BaseError, int> id = await EnsureCollection($"Auto · {CategoryName(category)}", library[category].Query, cancellationToken);
            foreach (BaseError error in id.LeftToSeq())
            {
                return error;
            }

            collections[category] = id.RightToSeq().Head();
        }

        if (plan.Week.Concat(plan.Weekend).Any(b => b.Category is AutoCategory.Info) && !library[AutoCategory.Info].Fallback)
        {
            // remet les fiches des flux à jour (durée des blocs de direct)
            await info.SyncSources(NounoursSettings.Load(), cancellationToken);
        }

        // le direct (blocs de 30 min) se cale pile sur les créneaux : pas de jingle ni de pubs autour, sinon il déborde
        _liveCollectionId = library[AutoCategory.Info].Fallback ? null : collections.GetValueOrDefault(AutoCategory.Info);
        Report($"{collections.Count} famille(s) de programmes prêtes (elles se mettent à jour avec la médiathèque)");

        // 2. jingles et pubs
        (int? pre, int? post, int? tail) = await EnsureFillers(plan, cancellationToken);
        if (post.HasValue)
        {
            Report(plan.QuarterHour ? "Pubs pour caler les débuts sur le quart d'heure" : "Pubs entre les programmes");
        }

        if (pre.HasValue)
        {
            Report("Un jingle avant chaque programme");
        }

        if (plan.Pubs && !_hasPubs)
        {
            Report("⚠ Aucune pub dans « Pubs » : les programmes s'enchaînent sans coupure (ajoutez des pubs dans « Ajouter des médias », puis réappliquez)");
        }

        if (plan.Jingles && !_hasJingles)
        {
            Report("⚠ Aucun jingle dans « Jingles » : pas de jingle avant les programmes (fabriquez-en dans l'Atelier, puis réappliquez)");
        }

        List<WizardSlot> Slots(List<AutoBlock> blocks) =>
            blocks.OrderBy(b => b.Start < TimeSpan.FromHours(5) ? b.Start + TimeSpan.FromDays(1) : b.Start)
                .Select(b => new WizardSlot
                {
                    Kind = ContentSourceKind.SmartCollection, ContentId = collections[b.Category], ContentName = CategoryName(b.Category),
                    FixedStart = true, StartTime = b.Start,
                    Mode = b.One ? WizardSlotMode.One : WizardSlotMode.Flood,
                    Order = b.Category is AutoCategory.Series or AutoCategory.Jeunesse ? PlaybackOrder.ShuffleInOrder : PlaybackOrder.Shuffle,
                    CustomTitle = b.Label
                })
                .ToList();

        // 3. la chaîne et son planning de semaine
        int playoutId;
        int scheduleId;
        string name;
        if (channelId is null)
        {
            ChannelWizardModel model = await wizard.CreateDefaults(cancellationToken);
            model.Name = string.IsNullOrWhiteSpace(newChannelName) ? "Ma chaîne" : newChannelName.Trim();
            if (!string.IsNullOrWhiteSpace(newChannelNumber))
            {
                model.Number = newChannelNumber.Trim();
            }

            model.Slots = Slots(plan.Week);
            Either<BaseError, ChannelWizardResult> created = await wizard.Create(model, null, cancellationToken);
            foreach (BaseError error in created.LeftToSeq())
            {
                return error;
            }

            ChannelWizardResult r = created.RightToSeq().Head();
            playoutId = r.PlayoutId;
            scheduleId = r.ScheduleId;
            name = model.Name;
            Report($"Chaîne « {name} » créée");
            await SetFillers(scheduleId, pre, post, tail, cancellationToken);
        }
        else
        {
            await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            Playout playout = await db.Playouts.Include(p => p.Channel).FirstOrDefaultAsync(p => p.ChannelId == channelId, cancellationToken);
            if (playout?.ProgramScheduleId is null)
            {
                return BaseError.New("Cette chaîne n'a pas de planning classique (mode expert) : la programmation automatique ne peut pas la remplacer.");
            }

            playoutId = playout.Id;
            scheduleId = playout.ProgramScheduleId.Value;
            name = playout.Channel.Name;
            Either<BaseError, Unit> replaced = await ReplaceItems(scheduleId, Slots(plan.Week), pre, post, tail, cancellationToken);
            foreach (BaseError error in replaced.LeftToSeq())
            {
                return error;
            }

            Report($"Programmation de « {name} » remplacée");
        }

        Report($"Semaine : {plan.Week.Count} rendez-vous, de {Hour(plan.Week.Select(b => b.Start).Where(t => t >= TimeSpan.FromHours(5)).DefaultIfEmpty(TimeSpan.Zero).Min())} jusqu'à la nuit");

        // 4. le week-end, avec son propre planning (semaine type)
        if (plan.WeekendDifferent && plan.Weekend.Count > 0)
        {
            Either<BaseError, int> weekend = await EnsureWeekendSchedule(name, Slots(plan.Weekend), pre, post, tail, cancellationToken);
            foreach (BaseError error in weekend.LeftToSeq())
            {
                return error;
            }

            int weekendId = weekend.RightToSeq().Head();
            var days = WeekTemplateService.Week.ToDictionary(d => d, d => d is DayOfWeek.Saturday or DayOfWeek.Sunday ? weekendId : scheduleId);
            Either<BaseError, Unit> saved = await weekTemplates.Save(playoutId, days, cancellationToken);
            foreach (BaseError error in saved.LeftToSeq())
            {
                return error;
            }

            Report($"Week-end : {plan.Weekend.Count} rendez-vous (planning « {name} · week-end »)");
        }
        else
        {
            await worker.WriteAsync(new BuildPlayout(playoutId, PlayoutBuildMode.Reset), cancellationToken);
        }

        // sans pubs pour combler, un film plus court que son créneau laisse un écran noir jusqu'au rendez-vous suivant :
        // la chaîne reçoit un remplissage de secours (l'interlude s'il existe, sinon les rediffusions)
        if (!_hasPubs)
        {
            await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            ErsatzTV.Core.Domain.Channel channel = await db.Channels.FirstOrDefaultAsync(c => c.Playouts.Any(p => p.Id == playoutId), cancellationToken);
            if (channel is { FallbackFillerId: null })
            {
                int? interlude = await db.FillerPresets.Where(p => p.Name == AutomationService.InterludePresetName).Select(p => (int?)p.Id).FirstOrDefaultAsync(cancellationToken);
                if (interlude is not null)
                {
                    channel.FallbackFillerId = interlude;
                    await db.SaveChangesAsync(cancellationToken);
                    Report("Entre deux programmes sans pub : l'interlude passe (remplissage de secours)");
                }
                else if (collections.TryGetValue(AutoCategory.Nuit, out int night) || collections.TryGetValue(AutoCategory.Series, out night))
                {
                    Report(await FillGaps(channel.Id, night, cancellationToken) + " (en attendant des pubs, pour éviter l'écran noir)");
                }
            }
        }

        Report("Grille en construction : elle apparaît dans le Planning d'ici quelques secondes");
        return steps;
    }

    public async Task<Either<BaseError, int>> EnsureCollection(string name, string query, CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        SmartCollection existing = await db.SmartCollections.AsNoTracking().FirstOrDefaultAsync(c => c.Name == name, cancellationToken);
        if (existing is not null)
        {
            if (existing.Query != query)
            {
                Either<BaseError, UpdateSmartCollectionResult> updated = await mediator.Send(new UpdateSmartCollection(existing.Id, name, query), cancellationToken);
                foreach (BaseError error in updated.LeftToSeq())
                {
                    return error;
                }
            }

            return existing.Id;
        }

        Either<BaseError, SmartCollectionViewModel> created = await mediator.Send(new CreateSmartCollection(query, name), cancellationToken);
        return created.Map(c => c.Id);
    }

    /// <summary>Jingle avant chaque programme ; pubs après (comptées, ou pour caler sur le quart d'heure) ; pubs pour combler avant un rendez-vous.</summary>
    private bool _hasJingles;
    private bool _hasPubs;

    /// <summary>La collection intelligente trouve-t-elle au moins une vidéo ?</summary>
    public async Task<bool> HasItems(int? smartCollectionId, CancellationToken cancellationToken)
    {
        if (smartCollectionId is null)
        {
            return false;
        }

        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        string query = await db.SmartCollections.Where(c => c.Id == smartCollectionId).Select(c => c.Query).FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        try
        {
            SearchResultAllItemsViewModel items = await mediator.Send(new QuerySearchIndexAllItems(query), cancellationToken);
            return items.MovieIds.Count + items.EpisodeIds.Count + items.MusicVideoIds.Count + items.OtherVideoIds.Count +
                (items.RemoteStreamIds?.Count ?? 0) > 0;
        }
        catch (Exception ex)
        {
            // recherche indisponible (index en cours de reconstruction…) : dans le doute, on ne touche à rien
            logger.LogDebug(ex, "Programmation auto : collection {Id} non vérifiée", smartCollectionId);
            return true;
        }
    }

    /// <summary>
    ///     Réparation : un remplissage (jingles, pubs, comblement…) branché sur une collection vide empêche la grille de se
    ///     construire, et la chaîne finit par ne plus rien diffuser. On le débranche des plannings (et des chaînes) puis
    ///     on reconstruit. Renvoie ce qui a été débranché.
    /// </summary>
    public async Task<List<string>> RepairEmptyFillers(CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<FillerPreset> presets = await db.FillerPresets.Where(p => p.CollectionType == CollectionType.SmartCollection && p.SmartCollectionId != null).ToListAsync(cancellationToken);
        var empty = new List<FillerPreset>();
        foreach (FillerPreset preset in presets)
        {
            if (!await HasItems(preset.SmartCollectionId, cancellationToken))
            {
                empty.Add(preset);
            }
        }

        if (empty.Count == 0)
        {
            return [];
        }

        var ids = empty.Select(p => p.Id).ToList();
        List<ProgramScheduleItem> items = await db.ProgramScheduleItems
            .Where(i => ids.Contains(i.PreRollFillerId ?? 0) || ids.Contains(i.PostRollFillerId ?? 0) || ids.Contains(i.MidRollFillerId ?? 0) ||
                        ids.Contains(i.TailFillerId ?? 0) || ids.Contains(i.FallbackFillerId ?? 0))
            .ToListAsync(cancellationToken);
        var schedules = items.Select(i => i.ProgramScheduleId).ToHashSet();
        foreach (ProgramScheduleItem item in items)
        {
            if (ids.Contains(item.PreRollFillerId ?? 0)) item.PreRollFillerId = null;
            if (ids.Contains(item.PostRollFillerId ?? 0)) item.PostRollFillerId = null;
            if (ids.Contains(item.MidRollFillerId ?? 0)) item.MidRollFillerId = null;
            if (ids.Contains(item.TailFillerId ?? 0)) item.TailFillerId = null;
            if (ids.Contains(item.FallbackFillerId ?? 0)) item.FallbackFillerId = null;
        }

        List<ErsatzTV.Core.Domain.Channel> channels = await db.Channels.Where(c => ids.Contains(c.FallbackFillerId ?? 0)).ToListAsync(cancellationToken);
        foreach (ErsatzTV.Core.Domain.Channel channel in channels)
        {
            channel.FallbackFillerId = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (items.Count == 0 && channels.Count == 0)
        {
            return [];
        }

        List<int> playouts = await db.Playouts
            .Where(p => schedules.Contains(p.ProgramScheduleId ?? 0) || p.ProgramScheduleAlternates.Any(a => schedules.Contains(a.ProgramScheduleId)))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        foreach (int playoutId in playouts)
        {
            await worker.WriteAsync(new BuildPlayout(playoutId, PlayoutBuildMode.Refresh), cancellationToken);
        }

        var result = empty.Select(p => p.Name).ToList();
        logger.LogWarning("Remplissages débranchés (collection vide, la grille ne pouvait plus se construire) : {Presets}", string.Join(", ", result));
        return result;
    }

    private async Task<(int? Pre, int? Post, int? Tail)> EnsureFillers(AutoPlan plan, CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        int? jingles = await db.SmartCollections.Where(c => c.Name == FolderImportService.Label(ImportRole.Jingles)).Select(c => (int?)c.Id).FirstOrDefaultAsync(cancellationToken);
        int? pubs = await db.SmartCollections.Where(c => c.Name == FolderImportService.Label(ImportRole.Pubs)).Select(c => (int?)c.Id).FirstOrDefaultAsync(cancellationToken);

        // une collection vide fait échouer TOUTE la construction de la grille (ErsatzTV : « has no valid items ») : on ne s'en sert pas
        jingles = await HasItems(jingles, cancellationToken) ? jingles : null;
        pubs = await HasItems(pubs, cancellationToken) ? pubs : null;
        (_hasJingles, _hasPubs) = (jingles is not null, pubs is not null);

        async Task<int?> Preset(string name, FillerKind kind, FillerMode mode, int? count, int? pad, int? collection)
        {
            if (collection is null)
            {
                return null;
            }

            FillerPreset preset = await db.FillerPresets.FirstOrDefaultAsync(p => p.Name == name, cancellationToken);
            if (preset is null)
            {
                preset = new FillerPreset { Name = name };
                await db.FillerPresets.AddAsync(preset, cancellationToken);
            }

            preset.FillerKind = kind;
            preset.FillerMode = mode;
            preset.Count = count;
            preset.PadToNearestMinute = pad;
            preset.CollectionType = CollectionType.SmartCollection;
            preset.SmartCollectionId = collection;
            preset.CollectionId = null;
            preset.MultiCollectionId = null;
            preset.MediaItemId = null;
            preset.PlaylistId = null;
            preset.AllowWatermarks = false;
            await db.SaveChangesAsync(cancellationToken);
            return preset.Id;
        }

        int? pre = plan.Jingles ? await Preset("Auto · Jingle avant", FillerKind.PreRoll, FillerMode.Count, 1, null, jingles) : null;
        int? post = plan.Pubs
            ? plan.QuarterHour
                ? await Preset("Auto · Pubs au quart d'heure", FillerKind.PostRoll, FillerMode.Pad, null, 15, pubs)
                : await Preset("Auto · Pubs", FillerKind.PostRoll, FillerMode.Count, 2, null, pubs)
            : null;
        int? tail = plan.Pubs ? await Preset("Auto · Combler", FillerKind.Tail, FillerMode.None, null, null, pubs ?? jingles) : null;
        return (pre, post, tail);
    }

    private int? _liveCollectionId;

    private async Task SetFillers(int scheduleId, int? pre, int? post, int? tail, CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<ProgramScheduleItem> items = await db.ProgramScheduleItems.Where(i => i.ProgramScheduleId == scheduleId).ToListAsync(cancellationToken);
        foreach (ProgramScheduleItem item in items)
        {
            bool live = _liveCollectionId is > 0 && item.SmartCollectionId == _liveCollectionId;
            item.PreRollFillerId = live ? null : pre;
            item.PostRollFillerId = live ? null : post;
            item.TailFillerId = tail;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Either<BaseError, Unit>> ReplaceItems(int scheduleId, List<WizardSlot> slots, int? pre, int? post, int? tail, CancellationToken cancellationToken)
    {
        await using (TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            // TPT : ExecuteDelete n'est pas possible sur cette hiérarchie, on passe par le suivi d'EF
            List<ProgramScheduleItem> old = await db.ProgramScheduleItems.Where(i => i.ProgramScheduleId == scheduleId).ToListAsync(cancellationToken);
            db.ProgramScheduleItems.RemoveRange(old);
            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (WizardSlot slot in slots)
        {
            Either<BaseError, ProgramScheduleItemViewModel> added = await mediator.Send(ChannelWizardService.ToScheduleItem(scheduleId, slot, Option<ProgrammeScheduling>.None), cancellationToken);
            foreach (BaseError error in added.LeftToSeq())
            {
                return error;
            }
        }

        await wizard.SortFixedItems(scheduleId, cancellationToken);
        await SetFillers(scheduleId, pre, post, tail, cancellationToken);
        return Unit.Default;
    }

    private async Task<Either<BaseError, int>> EnsureWeekendSchedule(string channelName, List<WizardSlot> slots, int? pre, int? post, int? tail, CancellationToken cancellationToken)
    {
        string name = $"{channelName} · week-end";
        int? id;
        await using (TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            id = await db.ProgramSchedules.Where(s => s.Name == name).Select(s => (int?)s.Id).FirstOrDefaultAsync(cancellationToken);
        }

        if (id is null)
        {
            Either<BaseError, CreateProgramScheduleResult> created = await mediator.Send(
                new CreateProgramSchedule(name, true, false, false, false, FixedStartTimeBehavior.Flexible),
                cancellationToken);
            foreach (BaseError error in created.LeftToSeq())
            {
                return error;
            }

            id = created.RightToSeq().Head().ProgramScheduleId;
        }

        Either<BaseError, Unit> replaced = await ReplaceItems(id.Value, slots, pre, post, tail, cancellationToken);
        return replaced.Map(_ => id.Value);
    }

    public static string Hour(TimeSpan t) => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
}
