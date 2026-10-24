using System.Globalization;
using System.Text;
using ErsatzTV.Core;

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
    /// <summary>Met les bandeaux à l'antenne (visible en ~5 s sur les chaînes qui ont l'habillage info).</summary>
    void PublishBanners(InfoSettings info);

    /// <summary>Crée ou met à jour l'habillage « Info en continu » et l'applique aux chaînes.</summary>
    Task<string> ApplyScene(InfoSettings info, IReadOnlyCollection<int> channelIds, CancellationToken cancellationToken);

    /// <summary>Écrit les flux (sources d'info, OBS) en .yml et les fait analyser : ils deviennent des flux en direct de la régie.</summary>
    Task<string> SyncSources(NounoursSettings settings, CancellationToken cancellationToken);

    string ObsUrl(ObsSettings obs);
}

public sealed class InfoService(IHabillageService habillage, IFolderImportService import) : IInfoService
{
    public const string SceneSlug = "info-en-continu";
    private const string ObsFile = "obs-direct.yml";

    public static string StreamsFolder => Path.Combine(FileSystemLayout.ImportsFolder, FolderImportService.Label(ImportRole.Direct));

    public void PublishBanners(InfoSettings info)
    {
        Directory.CreateDirectory(InfoFiles.Folder);
        Write("titres", info.Headlines);
        Write("flash", info.Flash);
        Write("sujet", info.Topic);
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
        PublishBanners(info);
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
        foreach (InfoSource source in settings.Info.Sources.Where(s => !string.IsNullOrWhiteSpace(s.Url)))
        {
            string name = string.IsNullOrWhiteSpace(source.Name) ? "Flux" : source.Name.Trim();
            wanted[$"flux-{HabillageService.Slugify(name)}.yml"] = (name, source.Url.Trim());
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
            string yaml = $"title: {Quote(title)}\nurl: {Quote(url)}\nis_live: true\n";
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

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
