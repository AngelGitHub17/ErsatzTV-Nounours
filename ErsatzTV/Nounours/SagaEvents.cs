using System.Globalization;
using ErsatzTV.Application;
using ErsatzTV.Application.Nounours;
using ErsatzTV.Application.Nounours.Atelier;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Nounours;

/// <summary>Où en est une saga : prochain passage, dernier passage.</summary>
public sealed record SagaStatus(
    DateTimeOffset? NextStart,
    string NextTitle,
    int NextIndex,
    int Count,
    string NextChannel,
    DateTimeOffset? LastStart,
    string LastTitle);

/// <summary>
///     Les sagas à l'antenne : soirée spéciale (carton d'ouverture avec le film du soir, carton « la suite… » avec le
///     suivant), bande-annonce du prochain film de saga, et « il y a 30 ans sortait… » le jour anniversaire d'un film.
///     Les cartons sont refaits chaque jour sous le même nom : là où ils sont programmés, ils se mettent à jour seuls.
/// </summary>
public sealed class SagaEventService(
    IDbContextFactory<TvContext> dbContextFactory,
    MovieCollectionService collections,
    AtelierService atelier,
    IAutomationService automation,
    IJournalService journal,
    System.Threading.Channels.ChannelWriter<IBackgroundServiceRequest> worker,
    ILogger<SagaEventService> logger)
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    public const string PromoFile = AtelierService.FilePrefix + "prochaine-saga.mp4";
    public const string AnniversaryFile = AtelierService.FilePrefix + "anniversaire.mp4";
    private const double CardSeconds = 10;

    private static string Folder(ImportRole role) => Path.Combine(FileSystemLayout.ImportsFolder, FolderImportService.Label(role));

    public static string OpeningFile(SagaEvening e) => $"{AtelierService.FilePrefix}saga-{e.Id}-ouverture.mp4";
    public static string NextFile(SagaEvening e) => $"{AtelierService.FilePrefix}saga-{e.Id}-la-suite.mp4";

    // ------------------------------------------------------------------ où en est la saga

    public async Task<SagaStatus> Status(int collectionId, string query, CancellationToken cancellationToken)
    {
        List<CollectionFilm> films = await collections.Films(query, cancellationToken);
        if (films.Count == 0)
        {
            return new SagaStatus(null, null, 0, 0, null, null, null);
        }

        var ids = films.Select(f => f.MovieId).ToList();
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        DateTime now = DateTime.UtcNow;

        // seulement les rendez-vous de la saga (un film de la saga peut aussi passer ailleurs, ex. la nuit)
        List<string> labels = await db.ProgramScheduleItems.AsNoTracking()
            .Where(i => i.SmartCollectionId == collectionId && i.CustomTitle != null && i.CustomTitle != "")
            .Select(i => i.CustomTitle)
            .Distinct()
            .ToListAsync(cancellationToken);
        var next = await db.PlayoutItems.AsNoTracking()
            .Where(i => ids.Contains(i.MediaItemId) && i.FillerKind == FillerKind.None && i.Finish > now)
            .Where(i => labels.Count == 0 || labels.Contains(i.CustomTitle))
            .OrderBy(i => i.Start)
            .Select(i => new { i.Start, i.MediaItemId, Channel = i.Playout.Channel.Name })
            .FirstOrDefaultAsync(cancellationToken);

        // dernier passage : le journal de diffusion des 14 derniers jours
        var titles = films.Select(f => f.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        JournalLine last = Enumerable.Range(0, 14)
            .SelectMany(d => journal.Read(DateOnly.FromDateTime(DateTime.Today.AddDays(-d))))
            .Where(l => l.Kind == "Film" && titles.Contains(l.Title) && l.Start < DateTimeOffset.Now)
            .MaxBy(l => l.Start);

        int index = next is null ? 0 : ids.IndexOf(next.MediaItemId) + 1;
        return new SagaStatus(
            next is null ? null : new DateTimeOffset(next.Start, TimeSpan.Zero).ToLocalTime(),
            next is null ? null : films[index - 1].Title,
            index,
            films.Count,
            next?.Channel,
            last?.Start,
            last?.Title);
    }

    // ------------------------------------------------------------------ chaque jour

    /// <summary>Appelé chaque minute par les services automatiques.</summary>
    public async Task Tick(CancellationToken cancellationToken)
    {
        NounoursSettings settings = NounoursSettings.Load();
        SagaSettings sagas = settings.Sagas;
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        bool due = TimeSpan.TryParse(sagas.At, CultureInfo.InvariantCulture, out TimeSpan at) && DateTime.Now.TimeOfDay >= at;
        if (due && sagas.LastMade != today && (sagas.Promo || sagas.Evenings.Any(e => e.Enabled)))
        {
            NounoursSettings.Update(s => s.Sagas.LastMade = today);
            await MakeCards(cancellationToken);
        }

        // les cartons fabriqués entrent dans la médiathèque : on les branche sur les rendez-vous
        foreach (SagaEvening evening in sagas.Evenings.Where(e => e.Enabled))
        {
            await Hook(evening, cancellationToken);
        }

        await AnniversaryTick(settings.Sagas.Anniversary, cancellationToken);
    }

    /// <summary>Refait les cartons des soirées spéciales et la bande-annonce du prochain film de saga.</summary>
    public async Task<string> MakeCards(CancellationToken cancellationToken)
    {
        SagaSettings sagas = NounoursSettings.Load().Sagas;
        var done = new List<string>();
        foreach (SagaEvening evening in sagas.Evenings.Where(e => e.Enabled))
        {
            string result = await MakeEvening(evening, cancellationToken);
            done.Add($"{evening.Name} : {result}");
        }

        if (sagas.Promo)
        {
            done.Add(await MakePromo(cancellationToken));
        }

        string summary = done.Count == 0 ? "Rien à fabriquer." : string.Join(" · ", done);
        NounoursSettings.Update(s => s.Sagas.LastResult = summary);
        return summary;
    }

    private async Task<(string Query, List<CollectionFilm> Films)> SagaFilms(int collectionId, CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        string query = await db.SmartCollections.Where(c => c.Id == collectionId).Select(c => c.Query).FirstOrDefaultAsync(cancellationToken);
        return (query, query is null ? [] : await collections.Films(query, cancellationToken));
    }

    private async Task<string> MakeEvening(SagaEvening evening, CancellationToken cancellationToken)
    {
        (string query, List<CollectionFilm> films) = await SagaFilms(evening.SmartCollectionId, cancellationToken);
        if (films.Count == 0)
        {
            return "saga vide";
        }

        SagaStatus status = await Status(evening.SmartCollectionId, query, cancellationToken);
        if (status.NextStart is null)
        {
            return "pas de passage prévu dans la grille";
        }

        // le film d'après, et quand il passera (prochain jour du rendez-vous)
        CollectionFilm following = films[status.NextIndex % films.Count];
        DateTimeOffset after = NextOccurrence(evening, status.NextStart.Value);
        string name = evening.Name.Replace(MovieCollectionService.SagaPrefix, string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        string channel = NounoursSettings.Load().Sagas.ChannelName;
        string music = AtelierSounds.Pick("Cinéma", 1, "Indicatif").FirstOrDefault() ?? string.Empty;

        atelier.StartJingle(new JingleSpec
        {
            Kind = AtelierKind.Jingle, Label = "SOIRÉE SPÉCIALE", Title = name,
            Subtitle = $"{When(status.NextStart.Value)} · {status.NextTitle} · {status.NextIndex}/{films.Count}",
            ChannelName = channel, AudioFile = music, Duration = CardSeconds, OutputName = OpeningFile(evening)[AtelierService.FilePrefix.Length..]
        }, ImportRole.Habillage);
        atelier.StartJingle(new JingleSpec
        {
            Kind = AtelierKind.Jingle, Label = "LA SUITE", Title = following.Title,
            Subtitle = $"{When(after, status.NextStart.Value)} · {name}",
            ChannelName = channel, AudioFile = music, Duration = CardSeconds, OutputName = NextFile(evening)[AtelierService.FilePrefix.Length..]
        }, ImportRole.Habillage);
        string result = $"{When(status.NextStart.Value).ToLowerInvariant()} : {status.NextTitle} ; la suite : {following.Title} ({When(after, status.NextStart.Value).ToLowerInvariant()})";
        NounoursSettings.Update(s =>
        {
            SagaEvening saved = s.Sagas.Evenings.FirstOrDefault(e => e.Id == evening.Id);
            if (saved is not null)
            {
                saved.LastResult = result;
            }
        });
        return result;
    }

    /// <summary>Le prochain jour du rendez-vous après ce passage.</summary>
    public static DateTimeOffset NextOccurrence(SagaEvening evening, DateTimeOffset from)
    {
        TimeSpan time = TimeSpan.TryParse(evening.At, CultureInfo.InvariantCulture, out TimeSpan t) ? t : from.TimeOfDay;
        for (var d = 1; d <= 7; d++)
        {
            DateTime day = from.Date.AddDays(d);
            if (evening.Days.Count == 0 || evening.Days.Contains(day.DayOfWeek))
            {
                return new DateTimeOffset(day + time, TimeZoneInfo.Local.GetUtcOffset(day + time));
            }
        }

        return from.AddDays(7);
    }

    /// <summary>« CE SOIR 20H45 », « DEMAIN 22H45 », « VENDREDI PROCHAIN 20H45 », « LUNDI 2 NOVEMBRE 20H45 ».</summary>
    public static string When(DateTimeOffset start, DateTimeOffset? reference = null)
    {
        DateTime today = (reference ?? DateTimeOffset.Now).LocalDateTime.Date;
        DateTime day = start.LocalDateTime.Date;
        string hour = start.LocalDateTime.ToString("HH'H'mm", Fr);
        int days = (day - today).Days;
        string when = days switch
        {
            0 when reference is null => start.LocalDateTime.Hour >= 18 ? "CE SOIR" : "AUJOURD'HUI",
            0 => "LE MÊME JOUR",
            1 => "DEMAIN",
            7 => $"{day.ToString("dddd", Fr)} PROCHAIN",
            < 7 => day.ToString("dddd", Fr),
            _ => day.ToString("dddd d MMMM", Fr)
        };
        return $"{when} {hour}".ToUpperInvariant();
    }

    /// <summary>Carton d'ouverture en jingle avant le film de la saga, « la suite » après (préréglages de remplissage).</summary>
    private async Task Hook(SagaEvening evening, CancellationToken cancellationToken)
    {
        (int Id, double Seconds)? opening = await automation.FindByPath(Path.Combine(Folder(ImportRole.Habillage), OpeningFile(evening)), cancellationToken);
        (int Id, double Seconds)? following = await automation.FindByPath(Path.Combine(Folder(ImportRole.Habillage), NextFile(evening)), cancellationToken);
        if (opening is null && following is null)
        {
            return;
        }

        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        async Task<int?> Preset(string name, FillerKind kind, int? mediaItemId)
        {
            if (mediaItemId is null)
            {
                return null;
            }

            FillerPreset preset = await db.FillerPresets.FirstOrDefaultAsync(p => p.Name == name, cancellationToken);
            if (preset is null)
            {
                preset = new FillerPreset { Name = name };
                await db.FillerPresets.AddAsync(preset, cancellationToken);
            }

            bool other = await db.OtherVideos.AnyAsync(o => o.Id == mediaItemId, cancellationToken);
            preset.FillerKind = kind;
            preset.FillerMode = FillerMode.Count;
            preset.Count = 1;
            preset.CollectionType = other ? CollectionType.OtherVideo : CollectionType.Movie;
            preset.MediaItemId = mediaItemId;
            preset.CollectionId = null;
            preset.SmartCollectionId = null;
            preset.MultiCollectionId = null;
            preset.PlaylistId = null;
            preset.AllowWatermarks = false;
            await db.SaveChangesAsync(cancellationToken);
            return preset.Id;
        }

        int? pre = await Preset($"Saga · {evening.Name} · ouverture", FillerKind.PreRoll, opening?.Id);
        int? post = await Preset($"Saga · {evening.Name} · la suite", FillerKind.PostRoll, following?.Id);

        // les rendez-vous de la saga sur cette chaîne (tous ses plannings : semaine, copies par jour, périodes)
        Playout playout = await db.Playouts.AsNoTracking().Include(p => p.ProgramScheduleAlternates)
            .FirstOrDefaultAsync(p => p.ChannelId == evening.ChannelId, cancellationToken);
        if (playout is null)
        {
            return;
        }

        var schedules = playout.ProgramScheduleAlternates.Select(a => a.ProgramScheduleId).Append(playout.ProgramScheduleId ?? 0).Distinct().ToList();
        List<ProgramScheduleItem> items = await db.ProgramScheduleItems
            .Where(i => schedules.Contains(i.ProgramScheduleId) && i.SmartCollectionId == evening.SmartCollectionId)
            .ToListAsync(cancellationToken);
        var changed = false;
        foreach (ProgramScheduleItem item in items)
        {
            if (pre is not null && item.PreRollFillerId != pre)
            {
                item.PreRollFillerId = pre;
                changed = true;
            }

            if (post is not null && item.PostRollFillerId != post)
            {
                item.PostRollFillerId = post;
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
            await worker.WriteAsync(new ErsatzTV.Application.Playouts.BuildPlayout(playout.Id, ErsatzTV.Core.Scheduling.PlayoutBuildMode.Refresh), cancellationToken);
            logger.LogInformation("Soirée spéciale {Saga} : cartons branchés sur {Count} rendez-vous", evening.Name, items.Count);
        }
    }

    // ------------------------------------------------------------------ bande-annonce du prochain film de saga

    public async Task<string> MakePromo(CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<(int Id, string Name, string Query)> sagas = (await db.SmartCollections.AsNoTracking()
                .Where(c => c.Name.StartsWith(MovieCollectionService.SagaPrefix))
                .Select(c => new { c.Id, c.Name, c.Query })
                .ToListAsync(cancellationToken))
            .Select(c => (c.Id, c.Name, c.Query))
            .ToList();

        // la saga qui passe le plus tôt dans les 7 jours
        (string Name, SagaStatus Status)? soonest = null;
        foreach ((int id, string name, string query) in sagas)
        {
            SagaStatus status = await Status(id, query, cancellationToken);
            if (status.NextStart is { } start && start < DateTimeOffset.Now.AddDays(7) && (soonest is null || start < soonest.Value.Status.NextStart))
            {
                soonest = (name[MovieCollectionService.SagaPrefix.Length..], status);
            }
        }

        if (soonest is null)
        {
            NounoursSettings.Update(s => s.Sagas.PromoResult = "Aucune saga programmée dans les 7 jours.");
            return "aucune saga programmée";
        }

        (string sagaName, SagaStatus next) = soonest.Value;
        var film = await db.Movies.AsNoTracking()
            .Where(m => m.MovieMetadata.Any(x => x.Title == next.NextTitle))
            .Select(m => new
            {
                m.Id,
                Path = m.MediaVersions.SelectMany(v => v.MediaFiles).Select(f => f.Path).FirstOrDefault(),
                Duration = m.MediaVersions.Select(v => v.Duration).FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (film is null || string.IsNullOrWhiteSpace(film.Path) || !File.Exists(film.Path))
        {
            return "fichier du film introuvable";
        }

        SagaSettings settings = NounoursSettings.Load().Sagas;
        string when = When(next.NextStart!.Value);
        var spec = new TrailerSpec
        {
            Label = $"LA SAGA {sagaName.ToUpperInvariant()}",
            Title = next.NextTitle,
            DateLine = when,
            ChannelName = settings.ChannelName,
            MusicFile = settings.PromoMusic,
            OutputName = PromoFile[AtelierService.FilePrefix.Length..]
        };
        double d = Math.Max(10, film.Duration.TotalSeconds);
        double length = Math.Clamp(d / 14, 2, 3.5);
        foreach (double at in new[] { 0.2, 0.45, 0.7 })
        {
            spec.Extracts.Add(new TrailerExtract
            {
                MediaItemId = film.Id, Title = next.NextTitle, Path = film.Path, ItemDuration = d, Length = length,
                Start = Math.Floor(Math.Min(d - length, d * at)),
                OverlayTitle = next.NextTitle, OverlayLine = $"{when} · {next.NextIndex}/{next.Count}"
            });
        }

        atelier.StartTrailer(spec);
        string result = $"{next.NextTitle} ({sagaName}), {when.ToLowerInvariant()}";
        NounoursSettings.Update(s => s.Sagas.PromoResult = result);
        return "bande-annonce : " + result;
    }

    // ------------------------------------------------------------------ il y a 30 ans sortait…

    /// <summary>Les films sortis un jour comme aujourd'hui, du plus bel anniversaire (30, 20, 25 ans…) au moins rond.</summary>
    public async Task<List<(int MovieId, string Title, DateTime Released, int Years)>> AnniversaryCandidates(int minYears, DateTime day, CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<int> excluded = await db.LocalLibraries.Where(l => l.Name.StartsWith("Habillage ·")).Select(l => l.Id).ToListAsync(cancellationToken);
        var movies = await db.MovieMetadata.AsNoTracking()
            .Where(m => m.ReleaseDate != null && !excluded.Contains(m.Movie.LibraryPath.LibraryId))
            .Select(m => new { m.MovieId, m.Title, m.ReleaseDate })
            .ToListAsync(cancellationToken);
        return movies
            // le 1er janvier est la date « par défaut » quand on ne connaît que l'année : pas d'anniversaire
            .Where(m => m.ReleaseDate!.Value.Month == day.Month && m.ReleaseDate.Value.Day == day.Day && !(day.Month == 1 && day.Day == 1))
            .Select(m => (m.MovieId, m.Title, Released: m.ReleaseDate!.Value, Years: day.Year - m.ReleaseDate.Value.Year))
            .Where(m => m.Years >= Math.Max(1, minYears))
            .OrderByDescending(m => m.Years % 10 == 0 ? 2 : m.Years % 5 == 0 ? 1 : 0)
            .ThenByDescending(m => m.Years)
            .ToList();
    }

    public async Task<string> MakeAnniversary(CancellationToken cancellationToken)
    {
        AnniversarySettings settings = NounoursSettings.Load().Sagas.Anniversary;
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        var candidates = await AnniversaryCandidates(settings.MinYears, DateTime.Today, cancellationToken);
        if (candidates.Count == 0)
        {
            NounoursSettings.Update(s =>
            {
                s.Sagas.Anniversary.LastMade = today;
                s.Sagas.Anniversary.LastResult = $"Aucun film sorti un {DateTime.Today.ToString("d MMMM", Fr)} (il y a {settings.MinYears} ans ou plus).";
                s.Sagas.Anniversary.PendingDay = null;
            });
            return "aucun anniversaire aujourd'hui";
        }

        (int movieId, string title, DateTime released, int years) = candidates[0];
        atelier.StartJingle(new JingleSpec
        {
            Kind = AtelierKind.Jingle, Label = $"IL Y A {years} ANS", Title = title,
            Subtitle = $"SORTAIT LE {released.ToString("d MMMM yyyy", Fr).ToUpperInvariant()}",
            ChannelName = settings.ChannelName, AudioFile = AtelierSounds.Pick("Cinéma", 1, "Indicatif").FirstOrDefault() ?? string.Empty,
            Duration = CardSeconds, OutputName = AnniversaryFile[AtelierService.FilePrefix.Length..]
        }, ImportRole.Habillage);
        string result = $"il y a {years} ans sortait « {title} » : carton puis film à {settings.At}";
        NounoursSettings.Update(s =>
        {
            AnniversarySettings a = s.Sagas.Anniversary;
            (a.LastMade, a.LastResult, a.PendingDay, a.PendingMovieId, a.PendingTitle, a.Scheduled) = (today, result, today, movieId, title, false);
        });
        return result;
    }

    private async Task AnniversaryTick(AnniversarySettings settings, CancellationToken cancellationToken)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        if (!settings.Enabled || settings.ChannelId == 0 || !TimeSpan.TryParse(settings.At, CultureInfo.InvariantCulture, out TimeSpan at))
        {
            return;
        }

        // fabriqué le matin, avant l'heure du passage
        if (settings.LastMade != today && DateTime.Now.TimeOfDay >= TimeSpan.FromHours(5) && DateTime.Now.TimeOfDay < at)
        {
            await MakeAnniversary(cancellationToken);
            return;
        }

        if (settings.PendingDay != today || settings.Scheduled)
        {
            return;
        }

        string path = Path.Combine(Folder(ImportRole.Habillage), AnniversaryFile);
        (int Id, double Seconds)? card = File.Exists(path) && File.GetLastWriteTime(path).Date == DateTime.Today
            ? await automation.FindByPath(path, cancellationToken)
            : null;
        if (card is null)
        {
            return; // pas encore dans la médiathèque
        }

        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        int? playoutId = await db.Playouts.Where(p => p.ChannelId == settings.ChannelId).Select(p => (int?)p.Id).FirstOrDefaultAsync(cancellationToken);
        TimeSpan filmDuration = await db.Movies.Where(m => m.Id == settings.PendingMovieId)
            .Select(m => m.MediaVersions.Select(v => v.Duration).FirstOrDefault())
            .FirstOrDefaultAsync(cancellationToken);
        double filmSeconds = filmDuration.TotalSeconds;
        if (playoutId is null)
        {
            return;
        }

        TimeSpan filmAt = at + TimeSpan.FromSeconds(Math.Ceiling(card.Value.Seconds));
        NounoursSettings.Update(s =>
        {
            s.Appointments.RemoveAll(a => a.Id is "anniv-carton" or "anniv-film");
            s.Appointments.Add(new Appointment
            {
                Id = "anniv-carton", Name = "Il y a … ans (carton)", At = at.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture), Date = today,
                PlayoutId = playoutId.Value, MediaItemId = card.Value.Id, MediaTitle = "Carton anniversaire", DurationSeconds = card.Value.Seconds
            });
            s.Appointments.Add(new Appointment
            {
                Id = "anniv-film", Name = $"Anniversaire : {settings.PendingTitle}", At = filmAt.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture), Date = today,
                PlayoutId = playoutId.Value, MediaItemId = settings.PendingMovieId, MediaTitle = settings.PendingTitle, DurationSeconds = filmSeconds
            });
            s.Sagas.Anniversary.Scheduled = true;
        });
        logger.LogInformation("Anniversaire : « {Title} » à {At} sur la chaîne {Channel}", settings.PendingTitle, settings.At, settings.ChannelId);
    }
}
