using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ErsatzTV.Application.Graphics;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace ErsatzTV.Application.Nounours;

public enum HabillageItemKind
{
    Clock,
    Text,
    Image,
    Banner,

    /// <summary>Cadre « passe-partout » : bordure pleine autour de l'image, fenêtre transparente au centre.</summary>
    Frame,

    /// <summary>Bandeau défilant : phrases, flux RSS, météo.</summary>
    Ticker,

    /// <summary>Signalétique jeunesse : rond « -10 / -12 / -16 / -18 » selon la classification du programme.</summary>
    Rating,

    /// <summary>Rideau quadrillé : des cases de couleur couvrent puis découvrent l'image au début et à la fin de chaque programme.</summary>
    Curtain,

    /// <summary>« Tout de suite » : bloc de cases avec le titre et l'heure du programme suivant, à la fin de chaque programme.</summary>
    UpNext
}

public enum HabillageVisibility
{
    /// <summary>Toujours visible.</summary>
    Always,

    /// <summary>Les N premières secondes de chaque programme.</summary>
    ProgramStart,

    /// <summary>Les N dernières secondes de chaque programme.</summary>
    ProgramEnd,

    /// <summary>Toutes les P minutes (calé sur l'horloge) pendant N secondes.</summary>
    Periodic
}

/// <summary>Un calque de la scène. Positions et tailles en % de l'image ; tailles de police en px pour du 1080p.</summary>
public sealed class HabillageItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public HabillageItemKind Kind { get; set; }
    public string Label { get; set; } = "Calque";
    public bool Visible { get; set; } = true;

    public double X { get; set; } = 5;
    public double Y { get; set; } = 5;
    public double Width { get; set; } = 20;
    public double Height { get; set; } = 8;
    public int ZIndex { get; set; } = 10;
    public int Opacity { get; set; } = 100;

    // texte / horloge
    public string Text { get; set; } = string.Empty;
    public string Format { get; set; } = "HH:mm";
    public string TimeZone { get; set; } = "Europe/Paris";
    public string FontFamily { get; set; } = "Roboto";
    public double FontSize { get; set; } = 48;
    public int FontWeight { get; set; } = 700;
    public bool Italic { get; set; }
    public string Color { get; set; } = "#FFFFFF";
    public string HaloColor { get; set; } = "#000000";
    public double HaloWidth { get; set; } = 3;
    public string Align { get; set; } = "Left";
    public int EpgEntries { get; set; }

    /// <summary>Cartouche derrière le texte, à sa taille (vide = aucun) ; aligné sur des huitièmes d'écran si BoxSnap.</summary>
    public string BoxColor { get; set; } = string.Empty;

    public bool BoxSnap { get; set; } = true;

    /// <summary>Compte à rebours : n'afficher que pendant les X dernières minutes avant le programme suivant (0 = toujours).</summary>
    public double CountdownMaxMinutes { get; set; }

    // horloge à aiguilles (Kind = Clock, Analog = true ; diamètre = Width % de la largeur)
    public bool Analog { get; set; }
    public string AnalogTicks { get; set; } = "dots";
    public bool AnalogSeconds { get; set; }
    public double AnalogOutline { get; set; } = 6;
    public string FaceColor { get; set; } = "#00000000";
    public string SecondsColor { get; set; } = "#E53935";

    // cadre (Kind = Frame) : épaisseurs en % ; couleur = BackgroundColor ; arrondi de la fenêtre = CornerRadius
    public double FrameSide { get; set; } = 5.5;
    public double FrameTop { get; set; } = 7;
    public double FrameBottom { get; set; } = 9;
    public bool FrameShadow { get; set; } = true;

    // bandeau défilant (Kind = Ticker) : couleur du fond = BackgroundColor, hauteur = Height
    public string TickerMessages { get; set; } = string.Empty;

    /// <summary>Bandeau d'info en direct : « titres », « flash » ou « sujet » (lu toutes les 5 s, modifiable depuis la page Info) ; vide = non.</summary>
    public string TickerLive { get; set; } = string.Empty;
    public string RssUrl { get; set; } = string.Empty;
    public string WeatherCity { get; set; } = string.Empty;
    public double? WeatherLatitude { get; set; }
    public double? WeatherLongitude { get; set; }
    public double TickerSpeed { get; set; } = 140;
    public string TickerLabel { get; set; } = "INFO";
    public string LabelColor { get; set; } = "#F5B700";

    // quadrillage (Kind = Curtain / UpNext) : couleurs des cases (#RRGGBB séparés par des virgules)
    public string GridPalette { get; set; } = "#FF3200,#8C0A11,#3C3632,#008A22,#0B2B70,#FFA800";
    public string GridLineColor { get; set; } = "#E1E8CD";
    public bool GridSeasonal { get; set; }
    public double CurtainSeconds { get; set; } = 1.6;
    public bool CurtainAtStart { get; set; } = true;
    public bool CurtainAtEnd { get; set; } = true;

    /// <summary>Ne pas jouer sur les programmes plus courts (jingles, pubs), en secondes.</summary>
    public int MinProgramSeconds { get; set; } = 90;

    public string UpNextLabel { get; set; } = "TOUT DE SUITE";

    // image
    public string ImagePath { get; set; } = string.Empty;

    // bandeau
    /// <summary>Couleur CSS (#RRGGBB ou #RRGGBBAA).</summary>
    public string BackgroundColor { get; set; } = "#000000CC";
    public double CornerRadius { get; set; } = 12;

    // apparition
    public HabillageVisibility Visibility { get; set; } = HabillageVisibility.Always;
    public int VisibleSeconds { get; set; } = 10;
    public int PeriodMinutes { get; set; } = 15;
}

public sealed class HabillageScene
{
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = "Nouvel habillage";
    public string Description { get; set; } = string.Empty;
    public bool UseDuringFiller { get; set; } = true;

    /// <summary>Remplacer (Override) ou ajouter (Merge) aux éléments déjà prévus par le planning.</summary>
    public bool MergeWithSchedule { get; set; } = true;

    public string PreviewBackground { get; set; } = string.Empty;

    /// <summary>Plage horaire d'affichage « HH:mm » (vide = toute la journée). Ex. 06:00 → 09:00 pour une matinale.</summary>
    public string ShowFrom { get; set; } = string.Empty;

    public string ShowUntil { get; set; } = string.Empty;

    public List<HabillageItem> Items { get; set; } = [];
    public int? DecoId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed record HabillageSummary(string Slug, string Name, string Description, int Items, DateTimeOffset UpdatedAt, List<string> Channels);

public sealed record HabillageChannelTarget(
    int ChannelId,
    string Number,
    string Name,
    int PlayoutId,
    int? CurrentDecoId,
    string CurrentDecoName,
    List<string> Scenes);

public interface IHabillageService
{
    Task<List<HabillageSummary>> GetAll(CancellationToken cancellationToken);
    Task<Option<HabillageScene>> GetScene(string slug, CancellationToken cancellationToken);

    /// <summary>Enregistre la scène, génère les éléments graphiques du moteur et la deco correspondante.</summary>
    Task<HabillageScene> Save(HabillageScene scene, CancellationToken cancellationToken);

    Task Delete(string slug, CancellationToken cancellationToken);
    Task<List<HabillageChannelTarget>> GetTargets(CancellationToken cancellationToken);

    /// <summary>
    ///     Met la scène sur les chaînes choisies (et la retire des autres). Une chaîne peut cumuler plusieurs habillages :
    ///     ils sont alors réunis dans une deco « Habillage combiné · numéro », chacun avec sa plage horaire.
    /// </summary>
    Task Apply(string slug, IReadOnlyCollection<int> channelIds, CancellationToken cancellationToken);

    Task<string> SaveAsset(string fileName, Stream content, CancellationToken cancellationToken);
    List<string> GetAssets();
    List<HabillageFont> GetFonts();
    string GenerateYamlPreview(HabillageItem item);
}

public sealed record HabillageFont(string Family, string FileName);

public sealed partial class HabillageService(
    IDbContextFactory<TvContext> dbContextFactory,
    IMediator mediator) : IHabillageService
{
    public const string DecoGroupName = "Habillages";
    private bool _rebuilding;
    private const string FilePrefix = "hab_";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<List<HabillageSummary>> GetAll(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(FileSystemLayout.HabillageScenesFolder);

        List<HabillageChannelTarget> targets = await GetTargets(cancellationToken);
        var result = new List<HabillageSummary>();
        foreach (string file in Directory.GetFiles(FileSystemLayout.HabillageScenesFolder, "*.json"))
        {
            Option<HabillageScene> maybeScene = await Load(file, cancellationToken);
            foreach (HabillageScene scene in maybeScene)
            {
                var channels = targets
                    .Where(t => t.Scenes.Contains(scene.Slug))
                    .Select(t => $"{t.Number} · {t.Name}")
                    .ToList();
                result.Add(new HabillageSummary(scene.Slug, scene.Name, scene.Description, scene.Items.Count, scene.UpdatedAt, channels));
            }
        }

        return result.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task<Option<HabillageScene>> GetScene(string slug, CancellationToken cancellationToken) =>
        Load(ScenePath(slug), cancellationToken);

    public async Task<HabillageScene> Save(HabillageScene scene, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scene.Slug))
        {
            scene.Slug = UniqueSlug(Slugify(scene.Name));
        }

        scene.UpdatedAt = DateTimeOffset.Now;

        // 1. fichiers YAML du moteur graphique
        var wanted = new Dictionary<string, string>();
        foreach (HabillageItem item in scene.Items.Where(i => i.Visible))
        {
            if (item.Kind == HabillageItemKind.Image && string.IsNullOrWhiteSpace(item.ImagePath))
            {
                continue;
            }

            if (item.Kind == HabillageItemKind.Banner)
            {
                item.ImagePath = RenderBanner(scene.Slug, item);
            }
            else if (item.Kind == HabillageItemKind.Rating)
            {
                RenderRatingIcons();
            }
            else if (item.Kind == HabillageItemKind.Frame)
            {
                item.X = 0;
                item.Y = 0;
                item.Width = 100;
                item.Height = 100;
                item.ImagePath = RenderFrame(scene.Slug, item);
            }

            string path = ElementPath(scene.Slug, item);
            wanted[path] = BuildYaml(scene, item);
        }

        foreach (string stale in AllGeneratedFiles(scene.Slug).Where(f => !wanted.ContainsKey(f)))
        {
            File.Delete(stale);
        }

        foreach ((string path, string yaml) in wanted)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, yaml, Encoding.UTF8, cancellationToken);
        }

        await mediator.Send(new RefreshGraphicsElements(), cancellationToken);

        // 2. deco « Habillage : nom »
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        DecoGroup group = await dbContext.DecoGroups.FirstOrDefaultAsync(g => g.Name == DecoGroupName, cancellationToken);
        if (group is null)
        {
            group = new DecoGroup { Name = DecoGroupName, Decos = [] };
            await dbContext.DecoGroups.AddAsync(group, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        Deco deco = null;
        if (scene.DecoId.HasValue)
        {
            deco = await dbContext.Decos
                .Include(d => d.DecoGraphicsElements)
                .FirstOrDefaultAsync(d => d.Id == scene.DecoId.Value, cancellationToken);
        }

        // deux habillages du même nom : le nom de la deco doit rester unique dans le groupe
        string decoName = DecoName(scene);
        int currentId = deco?.Id ?? 0;
        if (await dbContext.Decos.AnyAsync(d => d.DecoGroupId == group.Id && d.Name == decoName && d.Id != currentId, cancellationToken))
        {
            decoName = $"{decoName} ({scene.Slug})";
        }

        if (deco is null)
        {
            deco = new Deco
            {
                DecoGroupId = group.Id,
                Name = decoName,
                WatermarkMode = DecoMode.Inherit,
                DefaultFillerMode = DecoMode.Inherit,
                DeadAirFallbackMode = DecoMode.Inherit,
                BreakContentMode = DecoMode.Inherit,
                DecoGraphicsElements = [],
                DecoWatermarks = [],
                BreakContent = []
            };
            await dbContext.Decos.AddAsync(deco, cancellationToken);
        }

        deco.Name = decoName;
        deco.GraphicsElementsMode = wanted.Count == 0
            ? DecoMode.Inherit
            : scene.MergeWithSchedule ? DecoMode.Merge : DecoMode.Override;
        deco.UseGraphicsElementsDuringFiller = scene.UseDuringFiller;

        var paths = wanted.Keys.ToList();
        List<int> elementIds = await dbContext.GraphicsElements
            .Where(ge => paths.Contains(ge.Path))
            .Select(ge => ge.Id)
            .ToListAsync(cancellationToken);

        deco.DecoGraphicsElements.RemoveAll(dge => !elementIds.Contains(dge.GraphicsElementId));
        foreach (int id in elementIds.Where(id => deco.DecoGraphicsElements.All(dge => dge.GraphicsElementId != id)))
        {
            deco.DecoGraphicsElements.Add(new DecoGraphicsElement { GraphicsElementId = id });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        scene.DecoId = deco.Id;

        // 3. la scène elle-même
        Directory.CreateDirectory(FileSystemLayout.HabillageScenesFolder);
        await File.WriteAllTextAsync(
            ScenePath(scene.Slug),
            JsonSerializer.Serialize(scene, JsonOptions),
            cancellationToken);

        if (!_rebuilding)
        {
            _rebuilding = true;
            try
            {
                await RefreshCombined(scene.Slug, cancellationToken);
            }
            finally
            {
                _rebuilding = false;
            }
        }

        return scene;
    }

    public async Task Delete(string slug, CancellationToken cancellationToken)
    {
        Dictionary<int, List<string>> map = await LoadChannelMap(cancellationToken);
        var affected = map.Where(m => m.Value.Remove(slug)).Select(m => m.Key).ToList();
        if (affected.Count > 0)
        {
            await SaveChannelMap(map, cancellationToken);
            foreach (int channelId in affected)
            {
                await RebuildChannelDeco(channelId, map[channelId], cancellationToken);
            }
        }

        Option<HabillageScene> maybeScene = await GetScene(slug, cancellationToken);
        foreach (HabillageScene scene in maybeScene)
        {
            if (scene.DecoId.HasValue)
            {
                await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                List<Playout> playouts = await dbContext.Playouts
                    .Where(p => p.DecoId == scene.DecoId)
                    .ToListAsync(cancellationToken);
                foreach (Playout playout in playouts)
                {
                    playout.DecoId = null;
                }

                Deco deco = await dbContext.Decos.FindAsync([scene.DecoId.Value], cancellationToken);
                if (deco is not null)
                {
                    dbContext.Decos.Remove(deco);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }

            foreach (string file in AllGeneratedFiles(slug))
            {
                File.Delete(file);
            }

            string banners = Path.Combine(FileSystemLayout.HabillageAssetsFolder, "generated");
            if (Directory.Exists(banners))
            {
                foreach (string file in Directory.GetFiles(banners, $"{FilePrefix}{slug}_*"))
                {
                    File.Delete(file);
                }
            }

            File.Delete(ScenePath(slug));
            await mediator.Send(new RefreshGraphicsElements(), cancellationToken);
        }
    }

    public async Task<List<HabillageChannelTarget>> GetTargets(CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await dbContext.Playouts
            .AsNoTracking()
            .Include(p => p.Channel)
            .Include(p => p.Deco)
            .ToListAsync(cancellationToken);

        Dictionary<int, List<string>> map = await LoadChannelMap(cancellationToken);
        return rows
            .OrderBy(p => p.Channel.SortNumber)
            .Select(p => new HabillageChannelTarget(
                p.ChannelId,
                p.Channel.Number,
                p.Channel.Name,
                p.Id,
                p.DecoId,
                p.Deco?.Name,
                map.GetValueOrDefault(p.ChannelId) ?? []))
            .ToList();
    }

    public async Task Apply(string slug, IReadOnlyCollection<int> channelIds, CancellationToken cancellationToken)
    {
        Option<HabillageScene> maybeScene = await GetScene(slug, cancellationToken);
        foreach (HabillageScene loaded in maybeScene)
        {
            if (!loaded.DecoId.HasValue)
            {
                await Save(loaded, cancellationToken);
            }

            Dictionary<int, List<string>> map = await LoadChannelMap(cancellationToken);
            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            List<int> allChannels = await dbContext.Playouts.Select(p => p.ChannelId).ToListAsync(cancellationToken);
            var touched = new System.Collections.Generic.HashSet<int>();
            foreach (int channelId in allChannels)
            {
                List<string> scenes = map.GetValueOrDefault(channelId) ?? [];
                bool has = scenes.Contains(slug);
                bool want = channelIds.Contains(channelId);
                if (has != want)
                {
                    if (want)
                    {
                        scenes.Add(slug);
                    }
                    else
                    {
                        scenes.Remove(slug);
                    }

                    map[channelId] = scenes;
                    touched.Add(channelId);
                }
            }

            await SaveChannelMap(map, cancellationToken);
            foreach (int channelId in touched)
            {
                await RebuildChannelDeco(channelId, map.GetValueOrDefault(channelId) ?? [], cancellationToken);
            }
        }
    }

    // ------------------------------------------------------------------ plusieurs habillages par chaîne

    private static string ChannelMapPath => Path.Combine(FileSystemLayout.HabillageScenesFolder, "_chaines.cfg");

    /// <summary>Quels habillages sur quelle chaîne. Au premier appel, déduit de la deco actuelle des chaînes.</summary>
    private async Task<Dictionary<int, List<string>>> LoadChannelMap(CancellationToken cancellationToken)
    {
        if (File.Exists(ChannelMapPath))
        {
            try
            {
                return JsonSerializer.Deserialize<Dictionary<int, List<string>>>(await File.ReadAllTextAsync(ChannelMapPath, cancellationToken)) ?? [];
            }
            catch (JsonException)
            {
                // fichier abîmé : on le reconstruit
            }
        }

        var map = new Dictionary<int, List<string>>();
        var scenes = new List<HabillageScene>();
        if (Directory.Exists(FileSystemLayout.HabillageScenesFolder))
        {
            foreach (string file in Directory.GetFiles(FileSystemLayout.HabillageScenesFolder, "*.json"))
            {
                foreach (HabillageScene scene in await Load(file, cancellationToken))
                {
                    scenes.Add(scene);
                }
            }
        }

        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        foreach (var playout in await dbContext.Playouts.AsNoTracking().Select(p => new { p.ChannelId, p.DecoId }).ToListAsync(cancellationToken))
        {
            HabillageScene match = scenes.Find(s => s.DecoId.HasValue && s.DecoId == playout.DecoId);
            if (match is not null)
            {
                map[playout.ChannelId] = [match.Slug];
            }
        }

        return map;
    }

    private static async Task SaveChannelMap(Dictionary<int, List<string>> map, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(FileSystemLayout.HabillageScenesFolder);
        await File.WriteAllTextAsync(ChannelMapPath, JsonSerializer.Serialize(map, JsonOptions), cancellationToken);
    }

    /// <summary>Remet la deco d'une chaîne d'accord avec ses habillages : aucune, celle de la scène, ou une deco combinée.</summary>
    private async Task RebuildChannelDeco(int channelId, List<string> slugs, CancellationToken cancellationToken)
    {
        var scenes = new List<HabillageScene>();
        foreach (string slug in slugs)
        {
            foreach (HabillageScene scene in await GetScene(slug, cancellationToken))
            {
                scenes.Add(scene.DecoId.HasValue ? scene : await Save(scene, cancellationToken));
            }
        }

        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Playout playout = await dbContext.Playouts
            .Include(p => p.Channel)
            .Include(p => p.Deco)
            .ThenInclude(d => d.DecoGroup)
            .FirstOrDefaultAsync(p => p.ChannelId == channelId, cancellationToken);
        if (playout is null)
        {
            return;
        }

        string compositeName = $"Habillage combiné · {playout.Channel.Number}";
        Deco composite = await dbContext.Decos
            .Include(d => d.DecoGraphicsElements)
            .FirstOrDefaultAsync(d => d.Name == compositeName, cancellationToken);

        switch (scenes.Count)
        {
            case 0:
                // on ne retire que nos propres decos (une deco faite à la main reste)
                if (playout.Deco?.DecoGroup?.Name == DecoGroupName)
                {
                    playout.DecoId = null;
                }

                break;
            case 1:
                playout.DecoId = scenes[0].DecoId;
                break;
            default:
            {
                var decoIds = scenes.Select(s => s.DecoId!.Value).ToList();
                List<Deco> parts = await dbContext.Decos
                    .AsNoTracking()
                    .Include(d => d.DecoGraphicsElements)
                    .Where(d => decoIds.Contains(d.Id))
                    .ToListAsync(cancellationToken);
                var elementIds = parts.SelectMany(d => d.DecoGraphicsElements.Select(e => e.GraphicsElementId)).Distinct().ToList();

                if (composite is null)
                {
                    DecoGroup group = await dbContext.DecoGroups.FirstAsync(g => g.Name == DecoGroupName, cancellationToken);
                    composite = new Deco
                    {
                        DecoGroupId = group.Id,
                        Name = compositeName,
                        WatermarkMode = DecoMode.Inherit,
                        DefaultFillerMode = DecoMode.Inherit,
                        DeadAirFallbackMode = DecoMode.Inherit,
                        BreakContentMode = DecoMode.Inherit,
                        DecoGraphicsElements = [],
                        DecoWatermarks = [],
                        BreakContent = []
                    };
                    await dbContext.Decos.AddAsync(composite, cancellationToken);
                }

                composite.GraphicsElementsMode = scenes.Any(s => s.MergeWithSchedule) ? DecoMode.Merge : DecoMode.Override;
                composite.UseGraphicsElementsDuringFiller = scenes.Any(s => s.UseDuringFiller);
                composite.DecoGraphicsElements.RemoveAll(e => !elementIds.Contains(e.GraphicsElementId));
                foreach (int id in elementIds.Where(id => composite.DecoGraphicsElements.All(e => e.GraphicsElementId != id)))
                {
                    composite.DecoGraphicsElements.Add(new DecoGraphicsElement { GraphicsElementId = id });
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                playout.DecoId = composite.Id;
                break;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Après l'enregistrement d'une scène, met à jour les decos combinées qui l'utilisent.</summary>
    private async Task RefreshCombined(string slug, CancellationToken cancellationToken)
    {
        if (!File.Exists(ChannelMapPath))
        {
            return;
        }

        Dictionary<int, List<string>> map = await LoadChannelMap(cancellationToken);
        foreach ((int channelId, List<string> slugs) in map.Where(m => m.Value.Count > 1 && m.Value.Contains(slug)))
        {
            await RebuildChannelDeco(channelId, slugs, cancellationToken);
        }
    }

    public async Task<string> SaveAsset(string fileName, Stream content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(FileSystemLayout.HabillageAssetsFolder);
        string safe = SafeFileNameRegex().Replace(Path.GetFileName(fileName), "_");
        string target = Path.Combine(FileSystemLayout.HabillageAssetsFolder, safe);
        await using FileStream stream = File.Create(target);
        await content.CopyToAsync(stream, cancellationToken);
        return target;
    }

    public List<string> GetAssets()
    {
        if (!Directory.Exists(FileSystemLayout.HabillageAssetsFolder))
        {
            return [];
        }

        string[] extensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];
        return Directory.GetFiles(FileSystemLayout.HabillageAssetsFolder)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Order()
            .ToList();
    }

    public List<HabillageFont> GetFonts()
    {
        var result = new List<HabillageFont>();
        if (!Directory.Exists(FileSystemLayout.FontsCacheFolder))
        {
            return result;
        }

        foreach (string file in Directory.GetFiles(FileSystemLayout.FontsCacheFolder))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".ttf" or ".otf"))
            {
                continue;
            }

            try
            {
                using SKTypeface typeface = SKTypeface.FromFile(file);
                if (typeface is not null && result.All(f => f.Family != typeface.FamilyName))
                {
                    result.Add(new HabillageFont(typeface.FamilyName, Path.GetFileName(file)));
                }
            }
            catch
            {
                // police illisible : ignorée
            }
        }

        return result.OrderBy(f => f.Family).ToList();
    }

    private static readonly HttpClient Geo = new() { Timeout = TimeSpan.FromSeconds(6) };

    /// <summary>Coordonnées d'une ville pour la météo du bandeau (service gratuit Open-Meteo).</summary>
    public static async Task<(string Name, double Latitude, double Longitude)?> FindCity(string city, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(city))
        {
            return null;
        }

        string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city.Trim())}&count=1&language=fr&format=json";
        using JsonDocument json = JsonDocument.Parse(await Geo.GetStringAsync(url, cancellationToken));
        if (!json.RootElement.TryGetProperty("results", out JsonElement results) || results.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement first = results[0];
        return (first.GetProperty("name").GetString(), first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
    }

    public static List<string> GridColors(string palette) =>
        (palette ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(c => SKColor.TryParse(c, out _))
        .DefaultIfEmpty("#FF3200")
        .ToList();

    public string GenerateYamlPreview(HabillageItem item) => BuildYaml(new HabillageScene { Name = "aperçu" }, item);

    // ------------------------------------------------------------------ YAML

    private static string BuildYaml(HabillageScene scene, HabillageItem item)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Généré par le studio d'habillage ({scene.Name}). Ne pas modifier : enregistrez depuis le studio.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"name: {Quote($"{scene.Name} · {item.Label}")}");

        // positionnement : coin haut-gauche + marges en %
        sb.AppendLine("location: TopLeft");
        sb.AppendLine(CultureInfo.InvariantCulture, $"horizontal_margin_percent: {Num(item.X)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"vertical_margin_percent: {Num(item.Y)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"z_index: {item.ZIndex}");

        // le quadrillage gère lui-même ses moments d'apparition : seule la plage horaire de la scène s'applique
        string opacityExpression = item.Kind is HabillageItemKind.Curtain or HabillageItemKind.UpNext
            ? ScheduleGate(scene)
            : OpacityExpression(scene, item);
        if (string.IsNullOrEmpty(opacityExpression))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"opacity_percent: {Math.Clamp(item.Opacity, 0, 100)}");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"opacity_expression: {Quote(opacityExpression)}");
        }

        switch (item.Kind)
        {
            case HabillageItemKind.Curtain:
            case HabillageItemKind.UpNext:
                sb.AppendLine(CultureInfo.InvariantCulture, $"mode: {(item.Kind == HabillageItemKind.Curtain ? "rideau" : "suivant")}");
                sb.AppendLine("palette:");
                foreach (string color in GridColors(item.GridPalette))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  - {Quote(color)}");
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"line_color: {Quote(item.GridLineColor ?? string.Empty)}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"seasonal: {(item.GridSeasonal ? "true" : "false")}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"min_content_seconds: {Math.Max(0, item.MinProgramSeconds)}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"seed: {item.Id.Aggregate(7, (h, c) => (h * 31 + c) % 1000)}");
                if (item.Kind == HabillageItemKind.Curtain)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"curtain_seconds: {Num(item.CurtainSeconds)}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"at_start: {(item.CurtainAtStart ? "true" : "false")}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"at_end: {(item.CurtainAtEnd ? "true" : "false")}");
                }
                else
                {
                    sb.AppendLine("epg_entries: 2");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"visible_seconds: {Math.Max(3, item.VisibleSeconds)}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"label: {Quote(string.IsNullOrWhiteSpace(item.UpNextLabel) ? "TOUT DE SUITE" : item.UpNextLabel)}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"width_percent: {Num(Math.Max(15, item.Width))}");
                    // Epg[0] = le programme en cours, Epg[1] = le suivant
                    sb.AppendLine("next_title: \"{{ if Epg.size > 1 }}{{ Epg[1].Title | string.replace '\\\"' '' }}{{ end }}\"");
                    sb.AppendLine("next_time: \"{{ if Epg.size > 1 }}{{ format_datetime Epg[1].Start 'Europe/Paris' 'HH\\'H\\'mm' }}{{ end }}\"");
                }

                break;

            case HabillageItemKind.Rating:
                // l'image dépend de la classification du programme (Scriban) ; rien n'est affiché sans classification
                sb.AppendLine("image: \"{{ r = (MediaItem_ContentRating ?? MediaItem_ShowContentRating ?? '') | string.downcase }}"
                              + $"{{{{ if (r | string.contains '18') || r == 'nc-17' }}}}{RatingIcon("18")}"
                              + $"{{{{ else if (r | string.contains '16') || r == 'tv-ma' || r == 'r' }}}}{RatingIcon("16")}"
                              + $"{{{{ else if (r | string.contains '12') || (r | string.contains '13') || (r | string.contains '14') }}}}{RatingIcon("12")}"
                              + $"{{{{ else if (r | string.contains '10') || r == 'tv-pg' || r == 'pg' }}}}{RatingIcon("10")}"
                              + $"{{{{ else }}}}{RatingIcon("aucune")}{{{{ end }}}}\"");
                sb.AppendLine("scale: true");
                sb.AppendLine(CultureInfo.InvariantCulture, $"scale_width_percent: {Num(item.Width)}");
                break;

            case HabillageItemKind.Image:
            case HabillageItemKind.Banner:
            case HabillageItemKind.Frame:
                sb.AppendLine(CultureInfo.InvariantCulture, $"image: {Quote(item.ImagePath)}");
                sb.AppendLine("scale: true");
                sb.AppendLine(CultureInfo.InvariantCulture, $"scale_width_percent: {Num(item.Width)}");
                break;

            case HabillageItemKind.Ticker:
                sb.AppendLine(CultureInfo.InvariantCulture, $"width_percent: {Num(Math.Max(5, item.Width))}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"height_percent: {Num(Math.Max(2, item.Height))}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"background_color: {Quote(ToSkiaColor(item.BackgroundColor))}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"speed: {Num(item.TickerSpeed)}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"messages: {Quote((item.TickerMessages ?? string.Empty).Replace("\r", string.Empty).Replace('\n', '|'))}");
                if (!string.IsNullOrWhiteSpace(item.TickerLive))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"messages_file: {Quote(InfoFiles.For(item.TickerLive))}");
                    sb.AppendLine("refresh_seconds: 5");
                    if (item.TickerLive is "flash" or "sujet")
                    {
                        sb.AppendLine("hide_when_empty: true");
                    }

                    if (item.TickerLive == "sujet")
                    {
                        sb.AppendLine("fixed: true");
                    }
                }
                if (!string.IsNullOrWhiteSpace(item.RssUrl))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"rss_url: {Quote(item.RssUrl.Trim())}");
                }

                if (item.WeatherLatitude.HasValue && item.WeatherLongitude.HasValue)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"weather_latitude: {Num(item.WeatherLatitude.Value)}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"weather_longitude: {Num(item.WeatherLongitude.Value)}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"weather_label: {Quote(item.WeatherCity ?? string.Empty)}");
                }

                if (!string.IsNullOrWhiteSpace(item.TickerLabel))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"label: {Quote(item.TickerLabel)}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"label_color: {Quote(ToSkiaColor(item.LabelColor))}");
                }

                sb.AppendLine("include_fonts_from: " + Quote(FileSystemLayout.FontsCacheFolder));
                sb.AppendLine("base_style: base");
                sb.AppendLine("styles:");
                sb.AppendLine("  - name: base");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    font_family: {Quote(string.IsNullOrWhiteSpace(item.FontFamily) ? "Roboto" : item.FontFamily)}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    font_weight: {item.FontWeight}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    font_italic: {(item.Italic ? "true" : "false")}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    text_color: {Quote(ToSkiaColor(item.Color))}");
                sb.AppendLine("text: \"\"");
                break;

            case HabillageItemKind.Clock:
            case HabillageItemKind.Text:
                if (item.Kind == HabillageItemKind.Clock)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"format: {Quote(string.IsNullOrWhiteSpace(item.Format) ? "HH:mm" : item.Format)}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"time_zone: {Quote(item.TimeZone ?? string.Empty)}");
                    sb.AppendLine("culture: \"fr-FR\"");
                    if (item.Analog)
                    {
                        // le diamètre suit la largeur du calque (en % de la largeur) ; le moteur l'attend en % de la hauteur
                        sb.AppendLine("analog: true");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"analog_size_percent: {Num(Math.Max(1, item.Width) * 16 / 9)}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"analog_color: {Quote(ToSkiaColor(item.Color))}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"analog_face_color: {Quote(ToSkiaColor(item.FaceColor))}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"analog_seconds_color: {Quote(ToSkiaColor(item.SecondsColor))}");
                        if (item.HaloWidth > 0)
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"analog_shadow_color: {Quote(ToSkiaColor(item.HaloColor))}");
                        }

                        sb.AppendLine(CultureInfo.InvariantCulture, $"analog_outline_percent: {Num(item.AnalogOutline)}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"analog_ticks: {Quote(item.AnalogTicks ?? "dots")}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"analog_seconds: {(item.AnalogSeconds ? "true" : "false")}");
                    }
                }

                int epgEntries = item.EpgEntries;
                if (item.Kind == HabillageItemKind.Clock && (item.Text ?? string.Empty).Contains("{countdown}", StringComparison.Ordinal))
                {
                    // débuts des prochains programmes du guide ; l'horloge choisit le premier dans le futur
                    epgEntries = Math.Max(epgEntries, 4);
                    sb.AppendLine("countdown_to: \"{{ for e in Epg }}{{ format_datetime e.Start 'UTC' 'o' }};{{ end }}\"");
                    if (item.CountdownMaxMinutes > 0)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"countdown_max_minutes: {Num(item.CountdownMaxMinutes)}");
                    }
                }

                if (epgEntries > 0)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"epg_entries: {epgEntries}");
                }

                if (item.Width > 0)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"width_percent: {Num(item.Width)}");
                    sb.AppendLine("text_fit: Scale");
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"text_align: {item.Align switch { "Center" => "Center", "Right" => "Right", _ => "Left" }}");
                if (!string.IsNullOrWhiteSpace(item.BoxColor))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"box_color: {Quote(ToSkiaColor(item.BoxColor))}");
                    sb.AppendLine("box_padding: 0.32");
                    if (item.BoxSnap)
                    {
                        sb.AppendLine("box_snap_percent: 6.25");
                    }
                }
                sb.AppendLine("include_fonts_from: " + Quote(FileSystemLayout.FontsCacheFolder));
                sb.AppendLine("base_style: base");
                sb.AppendLine("styles:");
                sb.AppendLine("  - name: base");
                if (!string.IsNullOrWhiteSpace(item.FontFamily))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    font_family: {Quote(item.FontFamily)}");
                }

                // taille relative à la hauteur réelle de sortie (référence 1080p)
                sb.AppendLine(CultureInfo.InvariantCulture, $"    font_size: {{{{ {Num(item.FontSize)} * Resolution.Height / 1080 }}}}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    font_weight: {item.FontWeight}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    font_italic: {(item.Italic ? "true" : "false")}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    text_color: {Quote(ToSkiaColor(item.Color))}");
                if (item.HaloWidth > 0)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    halo_color: {Quote(ToSkiaColor(item.HaloColor))}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    halo_width: {Num(item.HaloWidth)}");
                }

                string text = item.Kind == HabillageItemKind.Clock && string.IsNullOrWhiteSpace(item.Text)
                    ? "{clock}"
                    : item.Text ?? string.Empty;

                // indentation explicite : le fichier reste du YAML valide même quand le texte ne contient que des variables
                sb.AppendLine("text: |2-");
                foreach (string line in text.Replace("\r", string.Empty).Split('\n'))
                {
                    sb.Append("  ").AppendLine(line);
                }

                break;
        }

        return sb.ToString();
    }

    private static string OpacityExpression(HabillageScene scene, HabillageItem item)
    {
        string inner = OpacityExpression(item);
        string gate = ScheduleGate(scene);
        if (string.IsNullOrEmpty(gate))
        {
            return inner;
        }

        string itemPart = string.IsNullOrEmpty(inner) ? Num(Math.Clamp(item.Opacity, 0, 100) / 100.0) : $"({inner})";
        return $"{itemPart} * ({gate})";
    }

    /// <summary>1 pendant la plage horaire de la scène, 0 sinon (fondu d'une seconde) ; gère le passage de minuit.</summary>
    private static string ScheduleGate(HabillageScene scene)
    {
        if (!TimeSpan.TryParse(scene.ShowFrom, CultureInfo.InvariantCulture, out TimeSpan from) ||
            !TimeSpan.TryParse(scene.ShowUntil, CultureInfo.InvariantCulture, out TimeSpan until) ||
            from == until)
        {
            return string.Empty;
        }

        int f = (int)from.TotalSeconds;
        int u = (int)until.TotalSeconds;
        return f < u
            ? $"LinearFadePoints(time_of_day_seconds, {f}, {f + 1}, {u - 1}, {u})"
            : $"LinearFadePoints(time_of_day_seconds, {f}, {f + 1}, 86400, 86401) + LinearFadePoints(time_of_day_seconds, -1, 0, {u - 1}, {u})";
    }

    private static string OpacityExpression(HabillageItem item)
    {
        double factor = Math.Clamp(item.Opacity, 0, 100) / 100.0;
        int seconds = Math.Max(1, item.VisibleSeconds);
        string inner = item.Visibility switch
        {
            HabillageVisibility.ProgramStart =>
                $"LinearFadeDuration(content_seconds, 0, 1, {seconds})",
            HabillageVisibility.ProgramEnd =>
                $"LinearFadeDuration(content_seconds, content_total_seconds - {seconds + 2}, 1, {seconds})",
            HabillageVisibility.Periodic =>
                $"LinearFadeDuration(time_of_day_seconds % {Math.Max(1, item.PeriodMinutes) * 60}, 0, 1, {seconds})",
            _ => string.Empty
        };

        if (string.IsNullOrEmpty(inner))
        {
            return string.Empty;
        }

        return factor >= 0.999 ? inner : $"({inner}) * {Num(factor)}";
    }

    // ------------------------------------------------------------------ bandeau (rectangle coloré)

    private static string RenderBanner(string slug, HabillageItem item)
    {
        // rendu en 1920x1080 de référence ; le moteur le met à l'échelle via scale_width_percent
        int width = Math.Max(4, (int)Math.Round(item.Width / 100.0 * 1920));
        int height = Math.Max(4, (int)Math.Round(item.Height / 100.0 * 1080));

        string folder = Path.Combine(FileSystemLayout.HabillageAssetsFolder, "generated");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"{FilePrefix}{slug}_{item.Id}.png");

        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            if (!SKColor.TryParse(ToSkiaColor(item.BackgroundColor), out SKColor color))
            {
                color = new SKColor(0, 0, 0, 200);
            }

            using var paint = new SKPaint();
            paint.Color = color;
            paint.IsAntialias = true;
            var radius = (float)Math.Max(0, item.CornerRadius);
            canvas.DrawRoundRect(new SKRect(0, 0, width, height), radius, radius, paint);
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);

        return path;
    }

    // ------------------------------------------------------------------ signalétique jeunesse

    public static string RatingIcon(string rating) =>
        Path.Combine(FileSystemLayout.HabillageAssetsFolder, "generated", $"{FilePrefix}signaletique_{rating}.png").Replace('\\', '/');

    /// <summary>Les ronds -10, -12, -16, -18 (disque blanc, cerclé de noir) et une image vide.</summary>
    private static void RenderRatingIcons()
    {
        Directory.CreateDirectory(Path.Combine(FileSystemLayout.HabillageAssetsFolder, "generated"));
        const int size = 256;
        string fontPath = Path.Combine(FileSystemLayout.FontsCacheFolder, "Roboto-Black.ttf");
        using SKTypeface typeface = File.Exists(fontPath) ? SKTypeface.FromFile(fontPath) : SKTypeface.FromFamilyName("Roboto", SKFontStyle.Bold);
        foreach (string rating in new[] { "10", "12", "16", "18", "aucune" })
        {
            using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                if (rating != "aucune")
                {
                    using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
                    using var ring = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = size * 0.07f };
                    canvas.DrawCircle(size / 2f, size / 2f, size * 0.44f, white);
                    canvas.DrawCircle(size / 2f, size / 2f, size * 0.44f, ring);
                    using var font = new SKFont(typeface, size * 0.4f);
                    using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                    string text = "-" + rating;
                    float width = font.MeasureText(text);
                    SKFontMetrics metrics = font.Metrics;
                    canvas.DrawText(text, (size - width) / 2f, size / 2f - (metrics.Ascent + metrics.Descent) / 2f, font, ink);
                }
            }

            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(RatingIcon(rating), data.ToArray());
        }
    }

    // ------------------------------------------------------------------ cadre (passe-partout)

    private static string RenderFrame(string slug, HabillageItem item)
    {
        const int width = 1920;
        const int height = 1080;
        string folder = Path.Combine(FileSystemLayout.HabillageAssetsFolder, "generated");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"{FilePrefix}{slug}_{item.Id}.png");

        if (!SKColor.TryParse(ToSkiaColor(item.BackgroundColor), out SKColor color))
        {
            color = new SKColor(0xF2, 0xEF, 0xE4);
        }

        float side = (float)(Math.Clamp(item.FrameSide, 0, 45) / 100.0 * width);
        float top = (float)(Math.Clamp(item.FrameTop, 0, 45) / 100.0 * height);
        float bottom = (float)(Math.Clamp(item.FrameBottom, 0, 45) / 100.0 * height);
        var window = new SKRect(side, top, width - side, height - bottom);
        var radius = (float)Math.Max(0, item.CornerRadius);

        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var path2 = new SKPath { FillType = SKPathFillType.EvenOdd };
            path2.AddRect(new SKRect(0, 0, width, height));
            path2.AddRoundRect(window, radius, radius);

            using var paint = new SKPaint { Color = color, IsAntialias = true };
            canvas.DrawPath(path2, paint);

            if (item.FrameShadow)
            {
                // fine ombre intérieure : donne l'effet « image posée dans le cadre »
                using var shadow = new SKPaint
                {
                    Color = new SKColor(0, 0, 0, 90),
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 4,
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3)
                };
                canvas.Save();
                canvas.ClipPath(path2);
                canvas.DrawRoundRect(window, radius, radius, shadow);
                canvas.Restore();
            }
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    // ------------------------------------------------------------------ fichiers

    private static string ElementPath(string slug, HabillageItem item)
    {
        string folder = item.Kind switch
        {
            HabillageItemKind.Clock => FileSystemLayout.GraphicsElementsClockTemplatesFolder,
            HabillageItemKind.Ticker => FileSystemLayout.GraphicsElementsTickerTemplatesFolder,
            HabillageItemKind.Curtain or HabillageItemKind.UpNext => FileSystemLayout.GraphicsElementsGridTemplatesFolder,
            HabillageItemKind.Text => FileSystemLayout.GraphicsElementsTextTemplatesFolder,
            _ => FileSystemLayout.GraphicsElementsImageTemplatesFolder
        };

        return Path.Combine(folder, $"{FilePrefix}{slug}_{item.Id}.yml");
    }

    private static List<string> AllGeneratedFiles(string slug)
    {
        string[] folders =
        [
            FileSystemLayout.GraphicsElementsClockTemplatesFolder,
            FileSystemLayout.GraphicsElementsTextTemplatesFolder,
            FileSystemLayout.GraphicsElementsImageTemplatesFolder,
            FileSystemLayout.GraphicsElementsTickerTemplatesFolder,
            FileSystemLayout.GraphicsElementsGridTemplatesFolder
        ];

        return folders
            .Where(Directory.Exists)
            .SelectMany(f => Directory.GetFiles(f, $"{FilePrefix}{slug}_*.yml"))
            .ToList();
    }

    private static string ScenePath(string slug) =>
        Path.Combine(FileSystemLayout.HabillageScenesFolder, $"{Slugify(slug)}.json");

    private static async Task<Option<HabillageScene>> Load(string file, CancellationToken cancellationToken)
    {
        if (!File.Exists(file))
        {
            return Option<HabillageScene>.None;
        }

        try
        {
            string json = await File.ReadAllTextAsync(file, cancellationToken);
            HabillageScene scene = JsonSerializer.Deserialize<HabillageScene>(json, JsonOptions);
            if (scene is null)
            {
                return Option<HabillageScene>.None;
            }

            scene.Slug = Path.GetFileNameWithoutExtension(file);
            return scene;
        }
        catch (JsonException)
        {
            return Option<HabillageScene>.None;
        }
    }

    private static string DecoName(HabillageScene scene) => $"Habillage : {scene.Name}";

    private static string UniqueSlug(string slug)
    {
        string candidate = slug;
        var i = 2;
        while (File.Exists(ScenePath(candidate)))
        {
            candidate = $"{slug}-{i++}";
        }

        return candidate;
    }

    public static string Slugify(string value)
    {
        string normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (char c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        string slug = MultiDashRegex().Replace(sb.ToString(), "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "habillage" : slug;
    }

    /// <summary>#RRGGBBAA (CSS) → #AARRGGBB (Skia). Les autres formats passent tels quels.</summary>
    public static string ToSkiaColor(string css)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            return "#FFFFFFFF";
        }

        css = css.Trim();
        return css.Length == 9 && css[0] == '#' ? string.Concat("#", css.AsSpan(7, 2), css.AsSpan(1, 6)) : css;
    }

    private static string Quote(string value) =>
        "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultiDashRegex();

    [GeneratedRegex(@"[^\w\.\-]")]
    private static partial Regex SafeFileNameRegex();
}
