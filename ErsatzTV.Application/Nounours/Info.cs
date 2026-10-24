using System.Globalization;
using System.Text;
using System.Xml.Linq;
using ErsatzTV.Application.Nounours.Atelier;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Nounours;

/// <summary>Les fichiers lus en direct par les bandeaux de la chaîne d'info.</summary>
public static class InfoFiles
{
    public static string Folder => Path.Combine(FileSystemLayout.AppDataFolder, "info");

    /// <summary>« titres » (bandeau défilant), « flash » (bandeau rouge, masqué s'il est vide), « sujet » (EN DIRECT · …).</summary>
    public static string For(string kind) => Path.Combine(Folder, $"{kind}.txt").Replace('\\', '/');
}

public interface IInfoService
{
    /// <summary>
    ///     Met les bandeaux à l'antenne (visible en quelques secondes sur les chaînes qui ont l'habillage info) ;
    ///     un nouveau flash peut déclencher le jingle « Flash spécial ».
    /// </summary>
    Task<string> PublishBanners(InfoSettings info, CancellationToken cancellationToken);

    /// <summary>Relit les flux de titres (RSS, Atom, texte) et met à jour le bandeau.</summary>
    Task<string> RefreshFeeds(CancellationToken cancellationToken);

    /// <summary>Fabrique le jingle « Flash spécial » (atelier, rangé dans « Habillage vidéo »).</summary>
    AtelierJob MakeFlashJingle(InfoSettings info);

    /// <summary>Crée ou met à jour l'habillage « Info en continu » et l'applique aux chaînes.</summary>
    Task<string> ApplyScene(InfoSettings info, IReadOnlyCollection<int> channelIds, CancellationToken cancellationToken);

    /// <summary>Écrit les flux (sources d'info, OBS) en .yml et les fait analyser : ils deviennent des flux en direct de la régie.</summary>
    Task<string> SyncSources(NounoursSettings settings, CancellationToken cancellationToken);

    string ObsUrl(ObsSettings obs);
}

public sealed class InfoService(
    IHabillageService habillage,
    IFolderImportService import,
    IRegieService regie,
    IAutomationService automation,
    AtelierService atelier,
    IDbContextFactory<TvContext> dbContextFactory) : IInfoService
{
    public const string SceneSlug = "info-en-continu";
    private const string ObsFile = "obs-direct.yml";
    public const string FlashJingleFile = AtelierService.FilePrefix + "flash-special.mp4";

    /// <summary>
    ///     Source prête à l'emploi : EBS, la chaîne d'information des institutions européennes (Commission, Parlement…),
    ///     diffusée en clair et réutilisable sous licence CC BY 4.0 en citant la source. Image 720p + piste française.
    /// </summary>
    public static InfoSource EbsPreset => new()
    {
        Name = "EBS · Union européenne (FR)",
        Url = "https://streams.prd.commavservices.eu/live/ebs/720p/720p.m3u8",
        AudioUrl = "https://streams.prd.commavservices.eu/live/ebs/fre/fre.m3u8",
        Credit = $"Images : © Union européenne, {DateTime.Now.Year} — CC BY 4.0"
    };

    public static string StreamsFolder => Path.Combine(FileSystemLayout.ImportsFolder, FolderImportService.Label(ImportRole.Direct));

    private static string FeedCache => Path.Combine(InfoFiles.Folder, "flux-cache.txt");

    public async Task<string> PublishBanners(InfoSettings info, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(InfoFiles.Folder);
        WriteHeadlines(info);
        Write("flash", info.Flash);
        Write("sujet", info.Topic);

        // nouveau flash : jingle « Flash spécial » sur les chaînes choisies
        string flash = (info.Flash ?? string.Empty).Trim();
        string previous = NounoursSettings.Load().Info.LastFlash ?? string.Empty;
        NounoursSettings.Update(s => s.Info.LastFlash = flash);
        if (!info.FlashJingle || flash.Length == 0 || flash == previous || info.FlashChannelIds.Count == 0)
        {
            return "Bandeaux à l'antenne.";
        }

        string path = Path.Combine(FileSystemLayout.ImportsFolder, FolderImportService.Label(ImportRole.Habillage), FlashJingleFile);
        (int Id, double Seconds)? jingle = File.Exists(path) ? await automation.FindByPath(path, cancellationToken) : null;
        if (jingle is null)
        {
            return "Bandeaux à l'antenne. Jingle « Flash spécial » introuvable : fabriquez-le d'abord.";
        }

        var aired = new List<string>();
        foreach (RegieChannel channel in (await regie.GetChannels(cancellationToken)).Where(c => info.FlashChannelIds.Contains(c.ChannelId)))
        {
            Either<BaseError, Unit> result = await regie.TakeAir(channel.PlayoutId, jingle.Value.Id, RegieMode.Once, 0, "Flash spécial", cancellationToken);
            if (result.IsRight)
            {
                aired.Add(channel.Name);
            }
        }

        return aired.Count == 0 ? "Bandeaux à l'antenne (jingle non passé)." : $"Flash à l'antenne, jingle passé sur {string.Join(", ", aired)}.";
    }

    /// <summary>Titres du bandeau = les vôtres + ceux des flux (dernière lecture).</summary>
    private static void WriteHeadlines(InfoSettings info)
    {
        string feeds = File.Exists(FeedCache) ? File.ReadAllText(FeedCache) : string.Empty;
        Write("titres", (info.Headlines ?? string.Empty) + "\n" + feeds);
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ErsatzTV-Nounours/1.0");
        return client;
    }

    public async Task<string> RefreshFeeds(CancellationToken cancellationToken)
    {
        InfoSettings info = NounoursSettings.Load().Info;
        var titles = new List<string>();
        var problems = new List<string>();
        foreach (string url in (info.Feeds ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string body = await Http.GetStringAsync(url, cancellationToken);
                titles.AddRange(ParseFeed(body).Take(Math.Clamp(info.FeedItems, 1, 30)));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
            {
                problems.Add($"{url} : {ex.Message}");
            }
        }

        Directory.CreateDirectory(InfoFiles.Folder);
        await File.WriteAllTextAsync(FeedCache, string.Join('\n', titles.Distinct()), new UTF8Encoding(false), cancellationToken);
        WriteHeadlines(info);
        string result = problems.Count == 0 ? $"{titles.Count} titre(s) lus" : $"{titles.Count} titre(s), problème : {string.Join(" ; ", problems)}";
        NounoursSettings.Update(s =>
        {
            s.Info.FeedsAt = DateTimeOffset.Now;
            s.Info.FeedsResult = result;
        });
        return result;
    }

    /// <summary>RSS (item/title), Atom (entry/title) ou texte brut (une ligne = un titre).</summary>
    public static List<string> ParseFeed(string body)
    {
        string trimmed = body.TrimStart();
        if (trimmed.StartsWith('<'))
        {
            XDocument doc = XDocument.Parse(trimmed);
            return doc.Descendants()
                .Where(e => e.Name.LocalName is "item" or "entry")
                .Select(e => e.Elements().FirstOrDefault(c => c.Name.LocalName == "title")?.Value?.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => System.Net.WebUtility.HtmlDecode(t).Replace('\n', ' '))
                .ToList();
        }

        if (trimmed.StartsWith('['))
        {
            // tableau JSON de textes (ou d'objets { "titre": … } / { "title": … })
            return System.Text.Json.Nodes.JsonNode.Parse(trimmed)!.AsArray()
                .Select(n => n is System.Text.Json.Nodes.JsonValue v ? v.ToString() : (n?["titre"] ?? n?["title"])?.ToString())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!.Trim())
                .ToList();
        }

        return trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.Length > 0).ToList();
    }

    public AtelierJob MakeFlashJingle(InfoSettings info)
    {
        string music = AtelierSounds.Pick("Suspense", 1, "Indicatif").FirstOrDefault() ?? string.Empty;
        var spec = new JingleSpec
        {
            Kind = AtelierKind.Jingle, Label = "FLASH", Title = "Information spéciale", ChannelName = info.ChannelName,
            AudioFile = music, Duration = 0, OutputName = "flash-special.mp4",
            Style = new AtelierStyle { Palette = ["#8C0A11", "#FF3200", "#3C3632", "#FFA800"], Energy = 1.2 }
        };
        return atelier.StartJingle(spec, ImportRole.Habillage);
    }

    private static void Write(string kind, string text)
    {
        string path = InfoFiles.For(kind);
        string content = string.Join('\n', (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        // écriture atomique : le bandeau ne lit jamais un fichier à moitié écrit
        string temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    public async Task<string> ApplyScene(InfoSettings info, IReadOnlyCollection<int> channelIds, CancellationToken cancellationToken)
    {
        await PublishBanners(info, cancellationToken);
        Option<HabillageScene> existing = await habillage.GetScene(SceneSlug, cancellationToken);
        HabillageScene scene = existing.IfNone(() => new HabillageScene { Slug = SceneSlug });
        scene.Name = "Info en continu";
        scene.Description = "Chaîne d'info : sujet « EN DIRECT », flash, titres qui défilent, heure. Les textes se changent en direct depuis la page Info.";
        scene.UseDuringFiller = true;
        scene.MergeWithSchedule = true;
        scene.Items = Items(info);
        await habillage.Save(scene, cancellationToken);
        await habillage.Apply(SceneSlug, channelIds, cancellationToken);
        return channelIds.Count == 0 ? "Habillage info enregistré (aucune chaîne)." : $"Habillage info appliqué à {channelIds.Count} chaîne(s).";
    }

    /// <summary>Les calques de l'habillage info, aux couleurs d'époque.</summary>
    private static List<HabillageItem> Items(InfoSettings info) =>
    [
        new HabillageItem
        {
            Kind = HabillageItemKind.Text, Label = "Crédit des images", Text = info.Credit ?? string.Empty, X = 72, Y = 12.4,
            FontSize = 20, FontWeight = 500, Color = "#FFFFFF", HaloWidth = 0, BoxColor = string.IsNullOrWhiteSpace(info.Credit) ? string.Empty : "#000000B3",
            BoxSnap = false, ZIndex = 22, Width = 24.5, Align = "Right", Visible = !string.IsNullOrWhiteSpace(info.Credit)
        },
        new HabillageItem
        {
            Kind = HabillageItemKind.Text, Label = "Nom de la chaîne", Text = (info.ChannelName ?? "INFO").ToUpperInvariant(), X = 3.5, Y = 5,
            FontSize = 40, FontWeight = 900, Color = "#FFFFFF", HaloWidth = 0, BoxColor = "#000000FF", BoxSnap = true, ZIndex = 20, Width = 0
        },
        new HabillageItem { Kind = HabillageItemKind.Banner, Label = "Pastille rouge", X = 3.5, Y = 11.6, Width = 3.2, Height = 1.2, BackgroundColor = "#FF3200FF", CornerRadius = 0, ZIndex = 21 },
        new HabillageItem { Kind = HabillageItemKind.Banner, Label = "Heure · fond", X = 87.5, Y = 5, Width = 9, Height = 6.6, BackgroundColor = "#000000FF", CornerRadius = 0, ZIndex = 20 },
        new HabillageItem
        {
            Kind = HabillageItemKind.Clock, Label = "Heure", Format = "HH:mm", X = 88.7, Y = 5.6, Width = 0, FontSize = 46, FontWeight = 900, Color = "#FFFFFF", HaloWidth = 0, ZIndex = 21
        },
        new HabillageItem
        {
            Kind = HabillageItemKind.Ticker, Label = "Sujet en direct", TickerLive = "sujet", TickerLabel = "EN DIRECT", LabelColor = "#FF3200",
            X = 3.5, Y = 76.5, Width = 70, Height = 6.2, FontSize = 36, FontWeight = 900, Color = "#000000", BackgroundColor = "#FFFFFFF2", HaloWidth = 0, ZIndex = 30
        },
        new HabillageItem
        {
            Kind = HabillageItemKind.Ticker, Label = "Flash info", TickerLive = "flash", TickerLabel = "FLASH", LabelColor = "#FFA800", TickerSpeed = 170,
            X = 0, Y = 85.6, Width = 100, Height = 6.4, FontSize = 36, FontWeight = 900, Color = "#FFFFFF", BackgroundColor = "#8C0A11F2", HaloWidth = 0, ZIndex = 31
        },
        new HabillageItem
        {
            Kind = HabillageItemKind.Ticker, Label = "Titres", TickerLive = "titres", TickerLabel = "INFO", LabelColor = "#FF3200", TickerSpeed = 130,
            RssUrl = info.RssUrl ?? string.Empty, X = 0, Y = 92.6, Width = 100, Height = 6.4, FontSize = 34, FontWeight = 700, Color = "#FFFFFF",
            BackgroundColor = "#000000F2", HaloWidth = 0, ZIndex = 32
        }
    ];

    public string ObsUrl(ObsSettings obs) =>
        string.Equals(obs.Protocol, "udp", StringComparison.OrdinalIgnoreCase)
            // ErsatzTV écoute : OBS envoie vers udp://<adresse de ce PC>:<port>
            ? string.Create(CultureInfo.InvariantCulture, $"udp://0.0.0.0:{obs.Port}?overrun_nonfatal=1&fifo_size=1000000")
            // OBS écoute (mode listener), ErsatzTV va chercher l'image
            : string.Create(CultureInfo.InvariantCulture, $"srt://{obs.Host}:{obs.Port}?mode=caller&latency={Math.Clamp(obs.LatencyMs, 20, 8000) * 1000}");

    public async Task<string> SyncSources(NounoursSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(StreamsFolder);
        var wanted = new Dictionary<string, (string Title, string Url)>
        {
            [ObsFile] = ("OBS (direct)", ObsUrl(settings.Obs))
        };
        string ffmpeg = await FFmpegPath(cancellationToken);
        var scripts = new Dictionary<string, string>();
        foreach (InfoSource source in settings.Info.Sources.Where(s => !string.IsNullOrWhiteSpace(s.Url)))
        {
            string name = string.IsNullOrWhiteSpace(source.Name) ? "Flux" : source.Name.Trim();
            string file = $"flux-{HabillageService.Slugify(name)}.yml";
            wanted[file] = (name, source.Url.Trim());
            if (!string.IsNullOrWhiteSpace(source.AudioUrl))
            {
                // image d'un flux + son d'un autre (ex. version française) : FFmpeg les assemble à la volée
                scripts[file] = $"\"{ffmpeg}\" -hide_banner -loglevel error -i \"{source.Url.Trim()}\" -i \"{source.AudioUrl.Trim()}\" "
                                + "-map 0:v:0 -map 1:a:0 -c copy -f mpegts pipe:1";
            }
        }

        foreach (string file in Directory.GetFiles(StreamsFolder, "*.yml"))
        {
            if (!wanted.ContainsKey(Path.GetFileName(file)))
            {
                File.Delete(file);
            }
        }

        foreach ((string file, (string title, string url)) in wanted)
        {
            // les flux d'info durent « 1 h » pour la grille (répétés à la suite) ; OBS n'a pas de durée
            string yaml = scripts.TryGetValue(file, out string script)
                ? $"title: {Quote(title)}\nscript: {Quote(script)}\nis_live: true\nduration: \"01:00:00\"\n"
                : file == ObsFile
                    ? $"title: {Quote(title)}\nurl: {Quote(url)}\nis_live: true\n"
                    : $"title: {Quote(title)}\nurl: {Quote(url)}\nis_live: true\nduration: \"01:00:00\"\n";
            string path = Path.Combine(StreamsFolder, file);
            if (!File.Exists(path) || await File.ReadAllTextAsync(path, cancellationToken) != yaml)
            {
                await File.WriteAllTextAsync(path, yaml, new UTF8Encoding(false), cancellationToken);
            }
        }

        Either<BaseError, ImportResult> result = await import.Import(StreamsFolder, ImportRole.Direct, cancellationToken);
        return result.Match(
            _ => $"{wanted.Count} flux enregistré(s) : ils apparaissent dans la régie d'ici quelques secondes.",
            error => $"Échec : {error}");
    }

    private async Task<string> FFmpegPath(CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        string key = ConfigElementKey.FFmpegPath.Key;
        string path = await db.ConfigElements.Where(c => c.Key == key).Select(c => c.Value).FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(path) ? "ffmpeg" : path;
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
