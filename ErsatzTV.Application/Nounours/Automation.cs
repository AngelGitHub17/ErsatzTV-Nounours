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
}

public sealed class AutomationService(
    IMediator mediator,
    IDbContextFactory<TvContext> dbContextFactory,
    IGuideService guideService,
    AtelierService atelier,
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
