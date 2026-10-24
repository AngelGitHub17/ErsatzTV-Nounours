using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using ErsatzTV.Core.Graphics;
using ErsatzTV.Core.Interfaces.Streaming;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using RichTextKit = Topten.RichTextKit;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

/// <summary>
///     Bandeau défilant. Le texte complet est dessiné une fois dans une longue image ; chaque image du flux
///     n'en recopie qu'une fenêtre décalée selon l'heure, pour un défilement continu même d'un programme à l'autre.
///     RSS et météo sont récupérés en arrière-plan et mis en cache (partagé entre les chaînes).
/// </summary>
public class TickerElement(
    GraphicsEngineFonts graphicsEngineFonts,
    TickerGraphicsElement ticker,
    ILogger logger)
    : GraphicsElement, IDisposable
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, List<string> Lines)> Cache = new();

    private readonly SKBitmap[] _frames = new SKBitmap[2];
    private GraphicsEngineContext _context;
    private SKBitmap _strip;
    private SKBitmap _labelImage;
    private int _barWidth;
    private int _barHeight;
    private SKPointI _location;
    private SKColor _background;
    private float _scale = 1;
    private int _frameIndex;
    private DateTimeOffset _nextRefresh;
    private Task _refreshing;
    private SKBitmap _pendingStrip;
    private bool _pendingReady;
    private Option<NCalc.Expression> _maybeOpacityExpression;
    private float _opacity;

    public override int ZIndex { get; } = ticker.ZIndex ?? 0;

    public override string DebugKey { get; } = $"Ticker {ticker.DebugName()}";

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _strip?.Dispose();
        _pendingStrip?.Dispose();
        _labelImage?.Dispose();
        foreach (SKBitmap frame in _frames)
        {
            frame?.Dispose();
        }
    }

    public override async Task InitializeAsync(GraphicsEngineContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _scale = context.FrameSize.Height / 1080f;
        int frameWidth = context.FrameSize.Width;
        int frameHeight = context.FrameSize.Height;

        _barWidth = (int)Math.Round((ticker.WidthPercent is > 0 ? ticker.WidthPercent.Value : 100) / 100.0 * frameWidth);
        _barHeight = Math.Max(8, (int)Math.Round(Math.Clamp(ticker.HeightPercent, 2, 30) / 100.0 * frameHeight));
        _location = CalculatePosition(
            ticker.Location,
            frameWidth,
            frameHeight,
            _barWidth,
            _barHeight,
            (int)Math.Round((ticker.HorizontalMarginPercent ?? 0) / 100.0 * frameWidth),
            (int)Math.Round((ticker.VerticalMarginPercent ?? 0) / 100.0 * frameHeight));
        _background = ParseColor(ticker.BackgroundColor, SKColors.Transparent);

        if (!string.IsNullOrWhiteSpace(ticker.OpacityExpression))
        {
            var expression = new NCalc.Expression(ticker.OpacityExpression);
            expression.EvaluateFunction += OpacityExpressionHelper.EvaluateFunction;
            _maybeOpacityExpression = expression;
        }
        else
        {
            _opacity = (ticker.OpacityPercent ?? 100) / 100.0f;
        }

        if (!string.IsNullOrWhiteSpace(ticker.IncludeFontsFrom))
        {
            graphicsEngineFonts.LoadFonts(ticker.IncludeFontsFrom);
        }

        for (var i = 0; i < _frames.Length; i++)
        {
            _frames[i] = new SKBitmap(_barWidth, _barHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        }

        _labelImage = string.IsNullOrWhiteSpace(ticker.Label) ? null : RenderLabel(ticker.Label);
        List<string> lines = await CollectLines(cancellationToken);
        _strip = RenderStrip(lines);
        _nextRefresh = DateTimeOffset.Now + RefreshEvery();
    }

    public override ValueTask<Option<PreparedElementImage>> PrepareImage(
        TimeSpan timeOfDay,
        TimeSpan contentTime,
        TimeSpan contentTotalTime,
        TimeSpan channelTime,
        CancellationToken cancellationToken)
    {
        float opacity = _opacity;
        foreach (NCalc.Expression expression in _maybeOpacityExpression)
        {
            opacity = OpacityExpressionHelper.GetOpacity(expression, timeOfDay, contentTime, contentTotalTime, channelTime);
        }

        // relu même quand il est masqué : un flash vide peut se remplir en cours de programme
        RefreshIfNeeded();
        if (opacity == 0 || _strip is null)
        {
            return ValueTask.FromResult(Option<PreparedElementImage>.None);
        }

        SKBitmap frame = _frames[_frameIndex];
        _frameIndex = (_frameIndex + 1) % _frames.Length;

        using (var canvas = new SKCanvas(frame))
        {
            int labelWidth = _labelImage?.Width ?? 0;
            float pad = _barHeight * 0.35f;
            bool fits = ticker.Fixed && _strip.Width + pad <= _barWidth - labelWidth;
            if (fits)
            {
                // texte fixe : le fond s'arrête au texte (pas de bandeau vide), arrondi à 1/32 d'écran
                float snap = _context.FrameSize.Width / 32f;
                float used = Math.Min(_barWidth, (float)Math.Ceiling((labelWidth + pad + _strip.Width) / snap) * snap);
                canvas.Clear(SKColors.Transparent);
                using var fill = new SKPaint { Color = _background };
                canvas.DrawRect(new SKRect(0, 0, used, _barHeight), fill);
            }
            else
            {
                canvas.Clear(_background);
            }

            float loop = _strip.Width;
            double seconds = (_context.ContentStartTime + contentTime).ToUnixTimeMilliseconds() / 1000.0;
            var offset = (float)(seconds * ticker.Speed * _scale % loop);

            canvas.Save();
            canvas.ClipRect(new SKRect(labelWidth, 0, _barWidth, _barHeight));
            if (fits)
            {
                canvas.DrawBitmap(_strip, labelWidth + pad, 0); // tient dans le bandeau : texte fixe
            }
            else
            {
                for (float x = labelWidth - offset; x < _barWidth; x += loop)
                {
                    canvas.DrawBitmap(_strip, x, 0);
                }
            }

            canvas.Restore();
            if (_labelImage is not null)
            {
                canvas.DrawBitmap(_labelImage, 0, 0);
            }
        }

        return ValueTask.FromResult<Option<PreparedElementImage>>(new PreparedElementImage(frame, _location, opacity, ZIndex, false));
    }

    // ------------------------------------------------------------------ contenu

    private void RefreshIfNeeded()
    {
        if (_pendingReady)
        {
            SKBitmap old = _strip;
            _strip = _pendingStrip;
            _pendingStrip = null;
            _pendingReady = false;
            if (!ReferenceEquals(old, _strip))
            {
                old?.Dispose();
            }
        }

        if (DateTimeOffset.Now < _nextRefresh || _refreshing is { IsCompleted: false })
        {
            return;
        }

        _nextRefresh = DateTimeOffset.Now + RefreshEvery();
        _refreshing = Task.Run(async () =>
        {
            try
            {
                List<string> lines = await CollectLines(CancellationToken.None);
                _pendingStrip = RenderStrip(lines);
                _pendingReady = true;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Bandeau défilant : rafraîchissement impossible");
            }
        });
    }

    private TimeSpan RefreshEvery() =>
        ticker.RefreshSeconds > 0 ? TimeSpan.FromSeconds(Math.Max(2, ticker.RefreshSeconds)) : TimeSpan.FromMinutes(Math.Max(1, ticker.RefreshMinutes));

    private async Task<List<string>> CollectLines(CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        lines.AddRange((ticker.Messages ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (!string.IsNullOrWhiteSpace(ticker.MessagesFile))
        {
            try
            {
                if (File.Exists(ticker.MessagesFile))
                {
                    lines.AddRange((await File.ReadAllLinesAsync(ticker.MessagesFile, cancellationToken))
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0));
                }
            }
            catch (IOException ex)
            {
                logger.LogDebug(ex, "Bandeau : fichier {File} illisible", ticker.MessagesFile);
            }
        }

        if (ticker.WeatherLatitude.HasValue && ticker.WeatherLongitude.HasValue)
        {
            string key = FormattableString.Invariant($"meteo:{ticker.WeatherLatitude}:{ticker.WeatherLongitude}");
            lines.AddRange(await Cached(key, () => Weather(ticker.WeatherLatitude.Value, ticker.WeatherLongitude.Value, ticker.WeatherLabel, cancellationToken)));
        }

        if (!string.IsNullOrWhiteSpace(ticker.RssUrl))
        {
            List<string> titles = await Cached("rss:" + ticker.RssUrl, () => Rss(ticker.RssUrl, cancellationToken));
            lines.AddRange(titles.Take(Math.Max(1, ticker.RssItems)));
        }

        if (lines.Count == 0 && !ticker.HideWhenEmpty)
        {
            lines.Add(ticker.Text ?? "…");
        }

        return lines;
    }

    private async Task<List<string>> Cached(string key, Func<Task<List<string>>> fetch)
    {
        if (Cache.TryGetValue(key, out (DateTimeOffset At, List<string> Lines) hit) &&
            DateTimeOffset.Now - hit.At < TimeSpan.FromMinutes(Math.Max(1, ticker.RefreshMinutes)))
        {
            return hit.Lines;
        }

        try
        {
            List<string> lines = await fetch();
            Cache[key] = (DateTimeOffset.Now, lines);
            return lines;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Bandeau défilant : {Source} indisponible ({Error})", key, ex.Message);
            return hit.Lines ?? [];
        }
    }

    private static async Task<List<string>> Rss(string url, CancellationToken cancellationToken)
    {
        string xml = await Http.GetStringAsync(url, cancellationToken);
        XDocument doc = XDocument.Parse(xml);
        // RSS (item/title) ou Atom (entry/title)
        return doc.Descendants()
            .Where(e => e.Name.LocalName is "item" or "entry")
            .Select(e => e.Elements().FirstOrDefault(c => c.Name.LocalName == "title")?.Value?.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => System.Net.WebUtility.HtmlDecode(t))
            .ToList();
    }

    private static async Task<List<string>> Weather(double latitude, double longitude, string label, CancellationToken cancellationToken)
    {
        string url = string.Create(
            CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min,weather_code&forecast_days=2&timezone=auto");
        using JsonDocument json = JsonDocument.Parse(await Http.GetStringAsync(url, cancellationToken));
        JsonElement root = json.RootElement;
        double now = root.GetProperty("current").GetProperty("temperature_2m").GetDouble();
        int code = root.GetProperty("current").GetProperty("weather_code").GetInt32();
        JsonElement daily = root.GetProperty("daily");
        double max = daily.GetProperty("temperature_2m_max")[0].GetDouble();
        double min = daily.GetProperty("temperature_2m_min")[0].GetDouble();
        double maxTomorrow = daily.GetProperty("temperature_2m_max")[1].GetDouble();
        int codeTomorrow = daily.GetProperty("weather_code")[1].GetInt32();
        string place = string.IsNullOrWhiteSpace(label) ? "Météo" : $"Météo {label}";
        return
        [
            string.Create(CultureInfo.GetCultureInfo("fr-FR"), $"{place} : {Math.Round(now)} °C, {Describe(code)} · aujourd'hui {Math.Round(min)} à {Math.Round(max)} °C"),
            string.Create(CultureInfo.GetCultureInfo("fr-FR"), $"Demain : {Describe(codeTomorrow)}, jusqu'à {Math.Round(maxTomorrow)} °C")
        ];
    }

    private static string Describe(int code) =>
        code switch
        {
            0 => "ciel dégagé",
            1 or 2 => "éclaircies",
            3 => "couvert",
            45 or 48 => "brouillard",
            >= 51 and <= 57 => "bruine",
            >= 61 and <= 67 => "pluie",
            >= 71 and <= 77 => "neige",
            >= 80 and <= 82 => "averses",
            85 or 86 => "averses de neige",
            >= 95 => "orages",
            _ => "temps variable"
        };

    // ------------------------------------------------------------------ dessin

    private SKTypeface Typeface()
    {
        StyleDefinition style = ticker.Styles?.FirstOrDefault(s => s.Name == ticker.BaseStyle) ?? ticker.Styles?.FirstOrDefault();
        var richStyle = new RichTextKit.Style
        {
            FontFamily = style?.FontFamily ?? "Roboto",
            FontWeight = style?.FontWeight ?? 700,
            FontItalic = style?.FontItalic ?? false
        };
        return graphicsEngineFonts.Mapper.TypefaceFromStyle(richStyle, false) ?? SKTypeface.Default;
    }

    private SKColor TextColor()
    {
        StyleDefinition style = ticker.Styles?.FirstOrDefault(s => s.Name == ticker.BaseStyle) ?? ticker.Styles?.FirstOrDefault();
        return ParseColor(style?.TextColor, SKColors.White);
    }

    private SKBitmap RenderStrip(List<string> lines)
    {
        if (lines.Count == 0)
        {
            return null; // rien à dire : bandeau masqué
        }

        string text = string.Join(ticker.Separator ?? "  •  ", lines) + (ticker.Fixed ? "    " : ticker.Separator ?? "  •  ");
        using var font = new SKFont(Typeface(), _barHeight * 0.58f);
        float width = Math.Min(30000, font.MeasureText(text) + 1);
        var bitmap = new SKBitmap((int)Math.Ceiling(width), _barHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = TextColor(), IsAntialias = true };
        SKFontMetrics metrics = font.Metrics;
        float baseline = _barHeight / 2f - (metrics.Ascent + metrics.Descent) / 2;
        canvas.DrawText(text, 0, baseline, font, paint);
        return bitmap;
    }

    private SKBitmap RenderLabel(string label)
    {
        using var font = new SKFont(Typeface(), _barHeight * 0.55f);
        float padding = _barHeight * 0.45f;
        int width = (int)Math.Ceiling(font.MeasureText(label) + padding * 2);
        var bitmap = new SKBitmap(width, _barHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(ParseColor(ticker.LabelColor, new SKColor(0xF5, 0xB7, 0x00)));
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        SKFontMetrics metrics = font.Metrics;
        canvas.DrawText(label, padding, _barHeight / 2f - (metrics.Ascent + metrics.Descent) / 2, font, paint);
        return bitmap;
    }

    private static SKColor ParseColor(string value, SKColor fallback) =>
        !string.IsNullOrWhiteSpace(value) && SKColor.TryParse(value, out SKColor color) ? color : fallback;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ErsatzTV-Nounours/1.0");
        return client;
    }
}
