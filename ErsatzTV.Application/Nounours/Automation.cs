using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ErsatzTV.Application.Channels;
using ErsatzTV.Application.Libraries;
using ErsatzTV.Application.Nounours.Atelier;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Iptv;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Nounours;

/// <summary>
///     Les services automatiques de l'édition Nounours : publication du guide sur un site, carton « Ce soir »
///     quotidien, chaînes éphémères, sous-titres automatiques.
/// </summary>
public interface IAutomationService
{
    Task<string> PublishGuide(CancellationToken cancellationToken);
    Task<AtelierJob> MakeTonight(CancellationToken cancellationToken);
    Task<List<(string Time, string Title, string Channel)>> TonightRows(TonightSettings settings, CancellationToken cancellationToken);
    Task<int> DeleteExpiredChannels(CancellationToken cancellationToken);
    Task<string> MakeSubtitles(int mediaItemId, CancellationToken cancellationToken);

    /// <summary>Bande-annonce « Cette semaine » avec les films des 7 prochains jours.</summary>
    Task<AtelierJob> MakeWeekly(CancellationToken cancellationToken);

    Task<List<WeatherRow>> WeatherRows(WeatherSettings settings, CancellationToken cancellationToken);
    Task<AtelierJob> MakeWeather(CancellationToken cancellationToken);

    /// <summary>L'interlude devient le « remplissage de secours » des chaînes choisies (ce qui passe quand rien n'est prévu).</summary>
    Task<string> UseInterlude(List<int> channelIds, CancellationToken cancellationToken);

    /// <summary>Rendez-vous d'antenne arrivés à l'heure : la régie passe la vidéo une fois.</summary>
    Task RunAppointments(CancellationToken cancellationToken);

    Task<(int Id, double Seconds)?> FindByPath(string path, CancellationToken cancellationToken);

    /// <summary>Même volume sur toutes les chaînes : normalisation EBU R128 dans tous les profils FFmpeg.</summary>
    Task<int> SetSameVolume(bool enabled, CancellationToken cancellationToken);

    Task<bool> IsSameVolume(CancellationToken cancellationToken);
}

public sealed class AutomationService(
    IMediator mediator,
    IDbContextFactory<TvContext> dbContextFactory,
    IGuideService guideService,
    AtelierService atelier,
    IRegieService regie,
    Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopeFactory,
    ILogger<AutomationService> logger) : IAutomationService
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ------------------------------------------------------------------ publication du guide

    public async Task<string> PublishGuide(CancellationToken cancellationToken)
    {
        NounoursSettings settings = NounoursSettings.Load();
        PublishSettings publish = settings.Publish;
        if (string.IsNullOrWhiteSpace(publish.Url))
        {
            return "Aucune adresse de publication.";
        }

        string result;
        try
        {
            var address = new Uri(string.IsNullOrWhiteSpace(settings.PublicAddress) ? "http://localhost:8409" : settings.PublicAddress.TrimEnd('/'));
            string pathBase = address.AbsolutePath.TrimEnd('/');
            Either<BaseError, ChannelGuide> guide = await mediator.Send(
                new GetChannelGuide(address.Scheme, address.Authority, pathBase, null),
                cancellationToken);
            if (guide.IsLeft)
            {
                result = $"Guide indisponible : {guide.LeftToSeq().Head()}";
            }
            else
            {
                EpgJson.EpgDocument document = EpgJson.Build(
                    guide.RightToSeq().Head().ToXml(),
                    $"{address.Scheme}://{address.Authority}{pathBase}",
                    null,
                    Math.Clamp(publish.Hours, 1, 336),
                    null,
                    false);

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                using var content = new StringContent(JsonSerializer.Serialize(document, Json), Encoding.UTF8, "application/json");
                content.Headers.Add("X-Nounours-Token", publish.Token ?? string.Empty);
                using HttpResponseMessage response = await http.PostAsync(publish.Url, content, cancellationToken);
                string body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
                result = response.IsSuccessStatusCode
                    ? $"Envoyé ({document.Chaines.Count} chaîne(s)) : {Shorten(body)}"
                    : $"Refusé par le site : {(int)response.StatusCode} {Shorten(body)}";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            result = $"Échec : {ex.Message}";
        }

        logger.LogInformation("Publication du guide : {Result}", result);
        NounoursSettings.Update(s =>
        {
            s.Publish.LastSent = DateTimeOffset.Now;
            s.Publish.LastResult = result;
        });
        return result;
    }

    private static string Shorten(string text) => text.Length > 160 ? text[..160] + "…" : text;

    // ------------------------------------------------------------------ carton « Ce soir »

    public async Task<List<(string Time, string Title, string Channel)>> TonightRows(TonightSettings settings, CancellationToken cancellationToken)
    {
        DateTime today = DateTime.Today;
        TimeSpan from = TimeSpan.TryParse(settings.From, CultureInfo.InvariantCulture, out TimeSpan f) ? f : new TimeSpan(20, 0, 0);
        TimeSpan until = TimeSpan.TryParse(settings.Until, CultureInfo.InvariantCulture, out TimeSpan u) ? u : new TimeSpan(23, 59, 0);
        var start = new DateTimeOffset(today + from);
        DateTimeOffset end = until > from ? new DateTimeOffset(today + until) : new DateTimeOffset(today.AddDays(1) + until);

        List<GuideEntry> entries = await guideService.GetEntries(start, end, settings.ChannelIds, includeFiller: false, cancellationToken);
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Dictionary<int, string> names = await db.Channels.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        bool several = entries.Select(e => e.ChannelId).Distinct().Count() > 1;

        var rows = new List<(string, string, string)>();
        string previous = null;
        foreach (GuideEntry entry in entries.Where(e => e.Kind != GuideEntryKind.Gap && e.Start >= start).OrderBy(e => e.Start).ThenBy(e => e.ChannelId))
        {
            string title = entry.DisplayTitle ?? string.Empty;
            int dash = title.IndexOf(" - ", StringComparison.Ordinal);
            if (entry.Kind == GuideEntryKind.Episode && dash > 0)
            {
                title = title[..dash];
            }

            string key = $"{entry.ChannelId}|{title}";
            if (key == previous)
            {
                continue; // épisodes à la suite : une seule ligne
            }

            previous = key;
            DateTimeOffset local = entry.Start.ToLocalTime();
            rows.Add(($"{local:HH}H{local:mm}", title, several ? names.GetValueOrDefault(entry.ChannelId) : null));
        }

        return rows;
    }

    public async Task<AtelierJob> MakeTonight(CancellationToken cancellationToken)
    {
        TonightSettings settings = NounoursSettings.Load().Tonight;
        List<(string Time, string Title, string Channel)> rows = await TonightRows(settings, cancellationToken);
        if (rows.Count == 0)
        {
            rows.Add(("—", "PROGRAMME À VENIR", null));
        }

        NounoursSettings.Update(s => s.Tonight.LastMade = DateOnly.FromDateTime(DateTime.Now));
        return atelier.StartTonight(settings, rows);
    }

    // ------------------------------------------------------------------ chaînes éphémères

    public async Task<int> DeleteExpiredChannels(CancellationToken cancellationToken)
    {
        Dictionary<int, DateTimeOffset> list = NounoursSettings.Load().EphemeralChannels;
        var expired = list.Where(e => e.Value <= DateTimeOffset.Now).Select(e => e.Key).ToList();
        foreach (int channelId in expired)
        {
            Either<BaseError, Unit> result = await mediator.Send(new DeleteChannel(channelId), cancellationToken);
            logger.LogInformation("Chaîne éphémère {Channel} supprimée : {Result}", channelId, result.IsRight ? "ok" : result.LeftToSeq().Head().ToString());
        }

        if (expired.Count > 0)
        {
            NounoursSettings.Update(s =>
            {
                foreach (int id in expired)
                {
                    s.EphemeralChannels.Remove(id);
                }
            });
        }

        return expired.Count;
    }

    // ------------------------------------------------------------------ bande-annonce de la semaine

    public async Task<AtelierJob> MakeWeekly(CancellationToken cancellationToken)
    {
        WeeklySettings settings = NounoursSettings.Load().Weekly;
        DateTimeOffset now = DateTimeOffset.Now;
        List<GuideEntry> entries = await guideService.GetEntries(now, now.AddDays(7), settings.ChannelIds, includeFiller: false, cancellationToken);

        // les films de la semaine, première diffusion de chacun
        var films = entries
            .Where(e => e.Kind == GuideEntryKind.Movie && e.Start > now)
            .OrderBy(e => e.Start)
            .GroupBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(Math.Clamp(settings.MaxFilms, 1, 8))
            .ToList();

        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spec = new TrailerSpec
        {
            Label = string.IsNullOrWhiteSpace(settings.Label) ? "CETTE SEMAINE" : settings.Label,
            Title = films.Count == 1 ? films[0].Title : "LES FILMS",
            DateLine = $"DU {now.ToString("dddd d", Fr).ToUpperInvariant()} AU {now.AddDays(6).ToString("dddd d MMMM", Fr).ToUpperInvariant()}",
            ChannelName = settings.ChannelName,
            MusicFile = settings.Music,
            OutputName = "cette-semaine.mp4"
        };

        foreach (GuideEntry film in films)
        {
            var found = await db.Movies.AsNoTracking()
                .Where(m => m.MovieMetadata.Any(x => x.Title == film.Title))
                .Select(m => new
                {
                    m.Id,
                    Path = m.MediaVersions.SelectMany(v => v.MediaFiles).Select(f => f.Path).FirstOrDefault(),
                    Duration = m.MediaVersions.Select(v => v.Duration).FirstOrDefault()
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (found is null || string.IsNullOrWhiteSpace(found.Path) || !File.Exists(found.Path))
            {
                continue;
            }

            DateTimeOffset local = film.Start.ToLocalTime();
            string when = local.Date == now.Date ? "CE SOIR" : local.ToString("dddd", Fr).ToUpperInvariant();
            double d = Math.Max(10, found.Duration.TotalSeconds);
            double length = Math.Clamp(d / 14, 2, 3.5);
            foreach (double at in new[] { 0.3, 0.62 })
            {
                spec.Extracts.Add(new TrailerExtract
                {
                    MediaItemId = found.Id, Title = film.Title, Path = found.Path, ItemDuration = d, Length = length,
                    Start = Math.Floor(Math.Min(d - length, d * at)),
                    OverlayTitle = film.Title, OverlayLine = $"{when} {local:HH}H{local:mm}"
                });
            }
        }

        string result = spec.Extracts.Count == 0
            ? "Aucun film dans le guide des 7 prochains jours (ou fichiers introuvables)."
            : $"{spec.Extracts.Count / 2} film(s) : {string.Join(", ", spec.Extracts.Select(e => e.Title).Distinct())}";
        NounoursSettings.Update(s =>
        {
            s.Weekly.LastMade = DateOnly.FromDateTime(DateTime.Now);
            s.Weekly.LastResult = result;
        });
        return spec.Extracts.Count == 0 ? null : atelier.StartTrailer(spec);
    }

    // ------------------------------------------------------------------ météo

    private static readonly HttpClient Weather = CreateWeatherClient();

    private static HttpClient CreateWeatherClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ErsatzTV-Nounours/1.0");
        return client;
    }

    public async Task<List<WeatherRow>> WeatherRows(WeatherSettings settings, CancellationToken cancellationToken)
    {
        var rows = new List<WeatherRow>();
        IEnumerable<string> cities = (settings.Cities ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(6);
        foreach (string city in cities)
        {
            (string Name, double Latitude, double Longitude)? found = await HabillageService.FindCity(city, cancellationToken);
            if (found is null)
            {
                rows.Add(new WeatherRow("?", city.ToUpperInvariant(), "VILLE INTROUVABLE"));
                continue;
            }

            string url = string.Create(
                CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/forecast?latitude={found.Value.Latitude}&longitude={found.Value.Longitude}&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto");
            using JsonDocument json = JsonDocument.Parse(await Weather.GetStringAsync(url, cancellationToken));
            JsonElement root = json.RootElement;
            double now = root.GetProperty("current").GetProperty("temperature_2m").GetDouble();
            int code = root.GetProperty("current").GetProperty("weather_code").GetInt32();
            double max = root.GetProperty("daily").GetProperty("temperature_2m_max")[0].GetDouble();
            double min = root.GetProperty("daily").GetProperty("temperature_2m_min")[0].GetDouble();
            rows.Add(new WeatherRow(
                $"{Math.Round(now).ToString(CultureInfo.InvariantCulture)}°",
                found.Value.Name.ToUpperInvariant(),
                $"{WeatherWord(code)} · {Math.Round(min).ToString(CultureInfo.InvariantCulture)}° / {Math.Round(max).ToString(CultureInfo.InvariantCulture)}°"));
        }

        return rows;
    }

    public static string WeatherWord(int code) =>
        code switch
        {
            0 => "SOLEIL",
            1 or 2 => "ÉCLAIRCIES",
            3 => "COUVERT",
            45 or 48 => "BROUILLARD",
            >= 51 and <= 57 => "BRUINE",
            >= 61 and <= 67 => "PLUIE",
            >= 71 and <= 77 => "NEIGE",
            >= 80 and <= 82 => "AVERSES",
            85 or 86 => "AVERSES DE NEIGE",
            >= 95 => "ORAGES",
            _ => "VARIABLE"
        };

    public async Task<AtelierJob> MakeWeather(CancellationToken cancellationToken)
    {
        WeatherSettings settings = NounoursSettings.Load().Weather;
        try
        {
            List<WeatherRow> rows = await WeatherRows(settings, cancellationToken);
            NounoursSettings.Update(s =>
            {
                s.Weather.LastMade = DateOnly.FromDateTime(DateTime.Now);
                s.Weather.LastResult = string.Join(" · ", rows.Select(r => $"{r.City} {r.Temperature}"));
            });
            return rows.Count == 0 ? null : atelier.StartWeather(settings, rows);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            NounoursSettings.Update(s =>
            {
                s.Weather.LastMade = DateOnly.FromDateTime(DateTime.Now);
                s.Weather.LastResult = $"Météo indisponible : {ex.Message}";
            });
            throw new InvalidOperationException($"Météo indisponible : {ex.Message}", ex);
        }
    }

    // ------------------------------------------------------------------ interlude

    public const string InterludePresetName = "Interlude (Nounours)";

    public async Task<string> UseInterlude(List<int> channelIds, CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        string path = Path.Combine(FileSystemLayout.ImportsFolder, FolderImportService.Label(ImportRole.Habillage), AtelierService.InterludeFile);
        int? otherVideoId = await db.OtherVideos.AsNoTracking()
            .Where(o => o.MediaVersions.Any(v => v.MediaFiles.Any(f => f.Path == path)))
            .Select(o => (int?)o.Id)
            .FirstOrDefaultAsync(cancellationToken);
        int? movieId = otherVideoId is null
            ? await db.Movies.AsNoTracking()
                .Where(o => o.MediaVersions.Any(v => v.MediaFiles.Any(f => f.Path == path)))
                .Select(o => (int?)o.Id)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        int? mediaItemId = otherVideoId ?? movieId;
        if (mediaItemId is null)
        {
            throw new InvalidOperationException("L'interlude n'est pas encore dans la médiathèque : fabriquez-le, puis réessayez une minute plus tard (le temps de l'analyse).");
        }

        Core.Domain.Filler.FillerPreset preset = await db.FillerPresets.FirstOrDefaultAsync(p => p.Name == InterludePresetName, cancellationToken);
        if (preset is null)
        {
            preset = new Core.Domain.Filler.FillerPreset { Name = InterludePresetName };
            await db.FillerPresets.AddAsync(preset, cancellationToken);
        }

        preset.FillerKind = Core.Domain.Filler.FillerKind.Fallback;
        preset.FillerMode = Core.Domain.Filler.FillerMode.None;
        preset.CollectionType = otherVideoId.HasValue ? CollectionType.OtherVideo : CollectionType.Movie;
        preset.MediaItemId = mediaItemId;
        preset.CollectionId = null;
        preset.SmartCollectionId = null;
        preset.MultiCollectionId = null;
        preset.PlaylistId = null;
        await db.SaveChangesAsync(cancellationToken);

        List<Channel> channels = await db.Channels.Where(c => channelIds.Contains(c.Id)).ToListAsync(cancellationToken);
        foreach (Channel channel in channels)
        {
            channel.FallbackFillerId = preset.Id;
        }

        await db.SaveChangesAsync(cancellationToken);
        return channels.Count == 0
            ? "Interlude prêt (aucune chaîne choisie)."
            : $"Interlude utilisé sur {string.Join(", ", channels.Select(c => c.Name))}.";
    }

    // ------------------------------------------------------------------ rendez-vous d'antenne

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> InFlight = new();

    /// <summary>
    ///     Rendez-vous d'antenne (récurrents ou insertions du jour) et diffusions automatiques des services.
    ///     Appelé chaque minute : ce qui doit partir dans la minute qui vient est lancé à la seconde près.
    /// </summary>
    public async Task RunAppointments(CancellationToken cancellationToken)
    {
        NounoursSettings settings = NounoursSettings.Load();
        DateTimeOffset now = DateTimeOffset.Now;

        // 1. rendez-vous et insertions
        foreach (Appointment appointment in settings.Appointments.Where(a => a.Enabled && a.PlayoutId > 0 && a.MediaItemId > 0))
        {
            if (!TimeSpan.TryParse(appointment.At, CultureInfo.InvariantCulture, out TimeSpan at) ||
                appointment.Date is null && appointment.Days.Count > 0 && !appointment.Days.Contains(now.DayOfWeek))
            {
                continue;
            }

            DateTime day = appointment.Date?.ToDateTime(TimeOnly.MinValue) ?? now.Date;
            var due = new DateTimeOffset(day + at, now.Offset);
            if (appointment.LastRun is { } last && last >= due)
            {
                continue;
            }

            var plan = new PlannedAir($"rdv:{appointment.Id}:{due:yyyyMMddHHmm}", appointment.Name, appointment.PlayoutId, appointment.MediaItemId,
                appointment.DurationSeconds, appointment.BeforeNext, appointment.Minutes, due);
            await Launch(plan, now, result => NounoursSettings.Update(s =>
            {
                Appointment saved = s.Appointments.FirstOrDefault(a => a.Id == appointment.Id);
                if (saved is not null)
                {
                    saved.LastRun = due;
                    saved.LastResult = result;
                    if (saved.Date is not null)
                    {
                        saved.Enabled = false; // insertion faite : on la garde pour l'historique du jour
                    }
                }
            }), cancellationToken);
        }

        // 2. diffusions automatiques des vidéos des services
        string folder(ImportRole role) => Path.Combine(FileSystemLayout.ImportsFolder, FolderImportService.Label(role));
        var services = new (string Key, string Name, BroadcastSettings Broadcast, string File)[]
        {
            ("meteo", "Météo", settings.Weather.Broadcast, Path.Combine(folder(ImportRole.BandesAnnonces), AtelierService.WeatherFile)),
            ("semaine", "Cette semaine", settings.Weekly.Broadcast, Path.Combine(folder(ImportRole.BandesAnnonces), "atelier-cette-semaine.mp4")),
            ("cesoir", "Ce soir", settings.Tonight.Broadcast, Path.Combine(folder(ImportRole.BandesAnnonces), AtelierService.TonightFile)),
            ("interlude", "Interlude", settings.InterludeBroadcast, Path.Combine(folder(ImportRole.Habillage), AtelierService.InterludeFile))
        };

        List<RegieChannel> channels = null;
        foreach ((string key, string name, BroadcastSettings broadcast, string file) in services)
        {
            if (broadcast is not { Enabled: true } || broadcast.ChannelIds.Count == 0 || !File.Exists(file))
            {
                continue;
            }

            (int Id, double Seconds)? media = await FindByPath(file, cancellationToken);
            if (media is null)
            {
                continue; // pas encore analysée
            }

            channels ??= await regie.GetChannels(cancellationToken);
            foreach (TimeSpan at in ParseTimes(broadcast.Times))
            {
                var due = new DateTimeOffset(now.Date + at, now.Offset);
                foreach (RegieChannel channel in channels.Where(c => broadcast.ChannelIds.Contains(c.ChannelId)))
                {
                    string runKey = $"{key}:{at:hhmm}:{channel.ChannelId}";
                    if (settings.BroadcastRuns.TryGetValue(runKey, out DateTimeOffset last) && last >= due)
                    {
                        continue;
                    }

                    var plan = new PlannedAir($"diff:{runKey}:{due:yyyyMMdd}", name, channel.PlayoutId, media.Value.Id, media.Value.Seconds, broadcast.BeforeNext, 0, due);
                    await Launch(plan, now, _ => NounoursSettings.Update(s => s.BroadcastRuns[runKey] = due), cancellationToken);
                }
            }
        }
    }

    public static List<TimeSpan> ParseTimes(string times) =>
        (times ?? string.Empty)
        .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(t => TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out TimeSpan v) ? v : (TimeSpan?)null)
        .Where(t => t is not null)
        .Select(t => t!.Value)
        .Distinct()
        .Order()
        .ToList();

    private sealed record PlannedAir(string Key, string Name, int PlayoutId, int MediaItemId, double Seconds, bool BeforeNext, int Minutes, DateTimeOffset Due);

    /// <summary>Calcule l'instant de départ ; s'il tombe dans la minute, attend puis prend l'antenne. Trop tard (10 min) : abandonné.</summary>
    private async Task Launch(PlannedAir plan, DateTimeOffset now, Action<string> done, CancellationToken cancellationToken)
    {
        if (now < plan.Due.AddMinutes(-2))
        {
            return; // pas encore l'heure (et « juste avant » ne regarde que 2 minutes en avance)
        }

        DateTimeOffset start = plan.Due;
        if (plan.BeforeNext)
        {
            DateTimeOffset? next = await NextBoundary(plan.PlayoutId, plan.Due, cancellationToken);
            if (next is not null)
            {
                start = next.Value - TimeSpan.FromSeconds(Math.Max(1, plan.Seconds));
                if (start < plan.Due.AddMinutes(-2))
                {
                    start = plan.Due.AddMinutes(-2);
                }
            }
        }

        if (now - start > TimeSpan.FromMinutes(10))
        {
            done($"Manqué ({start:HH:mm})");
            return;
        }

        if (start - now > TimeSpan.FromSeconds(65) || !InFlight.TryAdd(plan.Key, true))
        {
            return; // une prochaine minute s'en chargera (ou c'est déjà en route)
        }

        _ = Task.Run(async () =>
        {
            try
            {
                TimeSpan wait = start - DateTimeOffset.Now;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, CancellationToken.None);
                }

                RegieMode mode = plan.Minutes > 0 ? RegieMode.Minutes : RegieMode.Once;
                // la minute du planificateur est finie : on prend notre propre portée de services
                using Microsoft.Extensions.DependencyInjection.IServiceScope scope = scopeFactory.CreateScope();
                IRegieService air = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IRegieService>(scope.ServiceProvider);
                Either<BaseError, Unit> result = await air.TakeAir(plan.PlayoutId, plan.MediaItemId, mode, plan.Minutes, plan.Name, CancellationToken.None);
                string text = result.Match(_ => $"Passé à {DateTimeOffset.Now:HH:mm:ss}", error => $"Échec : {error}");
                logger.LogInformation("Antenne automatique « {Name} » : {Result}", plan.Name, text);
                done(text);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Antenne automatique « {Name} » : erreur", plan.Name);
                done($"Erreur : {ex.Message}");
            }
            finally
            {
                InFlight.TryRemove(plan.Key, out _);
            }
        }, CancellationToken.None);
    }

    /// <summary>Début du premier programme qui commence après <paramref name="after" /> (les intercalaires comptent).</summary>
    private async Task<DateTimeOffset?> NextBoundary(int playoutId, DateTimeOffset after, CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        DateTime utc = after.UtcDateTime;
        DateTime? start = await db.PlayoutItems.AsNoTracking()
            .Where(i => i.PlayoutId == playoutId && i.Start > utc)
            .OrderBy(i => i.Start)
            .Select(i => (DateTime?)i.Start)
            .FirstOrDefaultAsync(cancellationToken);
        return start is null ? null : new DateTimeOffset(DateTime.SpecifyKind(start.Value, DateTimeKind.Utc)).ToLocalTime();
    }

    public async Task<int> SetSameVolume(bool enabled, CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<FFmpegProfile> profiles = await db.FFmpegProfiles.ToListAsync(cancellationToken);
        foreach (FFmpegProfile profile in profiles)
        {
            profile.NormalizeLoudnessMode = enabled ? NormalizeLoudnessMode.LoudNorm : NormalizeLoudnessMode.Off;
            if (enabled)
            {
                profile.NormalizeAudio = true; // la normalisation du son passe par le réencodage audio
                profile.TargetLoudness ??= -16;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return profiles.Count;
    }

    public async Task<bool> IsSameVolume(CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<NormalizeLoudnessMode> modes = await db.FFmpegProfiles.Select(p => p.NormalizeLoudnessMode).ToListAsync(cancellationToken);
        return modes.Count > 0 && modes.All(m => m == NormalizeLoudnessMode.LoudNorm);
    }

    /// <summary>Le programme (film ou vidéo) correspondant à un fichier, et sa durée.</summary>
    public async Task<(int Id, double Seconds)?> FindByPath(string path, CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var other = await db.OtherVideos.AsNoTracking()
            .Where(o => o.MediaVersions.Any(v => v.MediaFiles.Any(f => f.Path == path)))
            .Select(o => new { o.Id, Duration = o.MediaVersions.Select(v => v.Duration).FirstOrDefault() })
            .FirstOrDefaultAsync(cancellationToken);
        if (other is not null)
        {
            return (other.Id, other.Duration.TotalSeconds);
        }

        var movie = await db.Movies.AsNoTracking()
            .Where(o => o.MediaVersions.Any(v => v.MediaFiles.Any(f => f.Path == path)))
            .Select(o => new { o.Id, Duration = o.MediaVersions.Select(v => v.Duration).FirstOrDefault() })
            .FirstOrDefaultAsync(cancellationToken);
        return movie is null ? null : (movie.Id, movie.Duration.TotalSeconds);
    }

    // ------------------------------------------------------------------ sous-titres automatiques

    public async Task<string> MakeSubtitles(int mediaItemId, CancellationToken cancellationToken)
    {
        SubtitleSettings settings = NounoursSettings.Load().Subtitles;
        string video = await atelier.PathOf(mediaItemId, cancellationToken);
        if (string.IsNullOrWhiteSpace(video) || !File.Exists(video))
        {
            throw new InvalidOperationException("Fichier introuvable (seuls les fichiers locaux sont pris en charge).");
        }

        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        string ffmpegKey = ConfigElementKey.FFmpegPath.Key;
        string ffmpeg = await db.ConfigElements.Where(c => c.Key == ffmpegKey).Select(c => c.Value).FirstOrDefaultAsync(cancellationToken);

        // 1. le son seul, compact (mono 16 kHz)
        string audio = Path.Combine(Path.GetTempPath(), $"nounours-stt-{Guid.NewGuid():N}.mp3");
        var psi = new ProcessStartInfo(ffmpeg ?? "ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-y", "-v", "error", "-i", video, "-vn", "-ac", "1", "-ar", "16000", "-b:a", "48k", audio })
        {
            psi.ArgumentList.Add(arg);
        }

        using (Process process = Process.Start(psi)!)
        {
            string errors = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Extraction du son impossible : {errors.Trim()}");
            }
        }

        try
        {
            // 2. transcription par le serveur (format SRT)
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            }

            using var form = new MultipartFormDataContent();
            await using FileStream stream = File.OpenRead(audio);
            var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            form.Add(file, "file", "audio.mp3");
            form.Add(new StringContent(settings.Model ?? "whisper-1"), "model");
            form.Add(new StringContent(settings.Language ?? "fr"), "language");
            form.Add(new StringContent("srt"), "response_format");
            using HttpResponseMessage response = await http.PostAsync(settings.Url, form, cancellationToken);
            string srt = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode || !srt.Contains("-->", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Serveur de transcription : {(int)response.StatusCode} {Shorten(srt)}");
            }

            // 3. fichier .srt à côté de la vidéo : ErsatzTV le trouve à la prochaine analyse
            string target = Path.Combine(Path.GetDirectoryName(video)!, $"{Path.GetFileNameWithoutExtension(video)}.{settings.Language ?? "fr"}.srt");
            await File.WriteAllTextAsync(target, srt, new UTF8Encoding(false), cancellationToken);

            int? libraryId = await db.MediaItems.Where(m => m.Id == mediaItemId).Select(m => (int?)m.LibraryPath.LibraryId).FirstOrDefaultAsync(cancellationToken);
            if (libraryId.HasValue)
            {
                await mediator.Send(new QueueLibraryScanByLibraryId(libraryId.Value), cancellationToken);
            }

            return target;
        }
        finally
        {
            File.Delete(audio);
        }
    }

    public static string FormatTime(DateTimeOffset value) => value.ToString("HH'H'mm", Fr);
}
