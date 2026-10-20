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
    Banner
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

    /// <summary>Compte à rebours : n'afficher que pendant les X dernières minutes avant le programme suivant (0 = toujours).</summary>
    public double CountdownMaxMinutes { get; set; }

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
    public List<HabillageItem> Items { get; set; } = [];
    public int? DecoId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed record HabillageSummary(string Slug, string Name, string Description, int Items, DateTimeOffset UpdatedAt, List<string> Channels);

public sealed record HabillageChannelTarget(int ChannelId, string Number, string Name, int PlayoutId, int? CurrentDecoId, string CurrentDecoName);

public interface IHabillageService
{
    Task<List<HabillageSummary>> GetAll(CancellationToken cancellationToken);
    Task<Option<HabillageScene>> GetScene(string slug, CancellationToken cancellationToken);

    /// <summary>Enregistre la scène, génère les éléments graphiques du moteur et la deco correspondante.</summary>
    Task<HabillageScene> Save(HabillageScene scene, CancellationToken cancellationToken);

    Task Delete(string slug, CancellationToken cancellationToken);
    Task<List<HabillageChannelTarget>> GetTargets(CancellationToken cancellationToken);

    /// <summary>Définit la scène comme deco par défaut des chaînes choisies (et la retire des autres).</summary>
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
                    .Where(t => scene.DecoId.HasValue && t.CurrentDecoId == scene.DecoId)
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

        if (deco is null)
        {
            deco = new Deco
            {
                DecoGroupId = group.Id,
                Name = DecoName(scene),
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

        deco.Name = DecoName(scene);
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

        return scene;
    }

    public async Task Delete(string slug, CancellationToken cancellationToken)
    {
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

        return rows
            .OrderBy(p => p.Channel.SortNumber)
            .Select(p => new HabillageChannelTarget(
                p.ChannelId,
                p.Channel.Number,
                p.Channel.Name,
                p.Id,
                p.DecoId,
                p.Deco?.Name))
            .ToList();
    }

    public async Task Apply(string slug, IReadOnlyCollection<int> channelIds, CancellationToken cancellationToken)
    {
        Option<HabillageScene> maybeScene = await GetScene(slug, cancellationToken);
        foreach (HabillageScene loaded in maybeScene)
        {
            HabillageScene scene = loaded.DecoId.HasValue ? loaded : await Save(loaded, cancellationToken);

            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            List<Playout> playouts = await dbContext.Playouts.ToListAsync(cancellationToken);
            foreach (Playout playout in playouts)
            {
                if (channelIds.Contains(playout.ChannelId))
                {
                    playout.DecoId = scene.DecoId;
                }
                else if (playout.DecoId == scene.DecoId)
                {
                    playout.DecoId = null;
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
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

        string opacityExpression = OpacityExpression(item);
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
            case HabillageItemKind.Image:
            case HabillageItemKind.Banner:
                sb.AppendLine(CultureInfo.InvariantCulture, $"image: {Quote(item.ImagePath)}");
                sb.AppendLine("scale: true");
                sb.AppendLine(CultureInfo.InvariantCulture, $"scale_width_percent: {Num(item.Width)}");
                break;

            case HabillageItemKind.Clock:
            case HabillageItemKind.Text:
                if (item.Kind == HabillageItemKind.Clock)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"format: {Quote(string.IsNullOrWhiteSpace(item.Format) ? "HH:mm" : item.Format)}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"time_zone: {Quote(item.TimeZone ?? string.Empty)}");
                    sb.AppendLine("culture: \"fr-FR\"");
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

    // ------------------------------------------------------------------ fichiers

    private static string ElementPath(string slug, HabillageItem item)
    {
        string folder = item.Kind switch
        {
            HabillageItemKind.Clock => FileSystemLayout.GraphicsElementsClockTemplatesFolder,
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
            FileSystemLayout.GraphicsElementsImageTemplatesFolder
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
