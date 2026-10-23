using System.Globalization;
using ErsatzTV.Core.Graphics;
using ErsatzTV.Core.Interfaces.Streaming;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using TimeZoneConverter;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

/// <summary>
///     Horloge en direct. L'heure est calculée à partir de l'heure réelle de chaque image
///     (début du programme + position dans le programme), donc elle reste juste même en cas de seek.
///     Le rendu texte est réutilisé depuis <see cref="TextElement" /> et n'est refait que lorsque
///     la chaîne affichée change (une fois par minute ou par seconde selon le format).
/// </summary>
public class ClockElement(
    GraphicsEngineFonts graphicsEngineFonts,
    ClockGraphicsElement clockElement,
    ILogger logger)
    : GraphicsElement, IDisposable
{
    private GraphicsEngineContext _context;
    private TimeZoneInfo _timeZone;
    private CultureInfo _culture;
    private string _lastText;
    private TextElement _current;
    private Option<NCalc.Expression> _maybeOpacityExpression;
    private float _opacity;
    private List<DateTimeOffset> _countdownTargets = [];
    private SKBitmap _analogBitmap;
    private SKPointI _analogLocation;
    private string _analogKey;

    public override int ZIndex { get; } = clockElement.ZIndex ?? 0;

    public override string DebugKey { get; } = $"Clock {clockElement.DebugName()}";

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _current?.Dispose();
        _current = null;
        _analogBitmap?.Dispose();
        _analogBitmap = null;
    }

    public override Task InitializeAsync(GraphicsEngineContext context, CancellationToken cancellationToken)
    {
        _context = context;

        try
        {
            _timeZone = string.IsNullOrWhiteSpace(clockElement.TimeZone)
                ? TimeZoneInfo.Local
                : TZConvert.GetTimeZoneInfo(clockElement.TimeZone);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unknown clock time zone {TimeZone}; using local time", clockElement.TimeZone);
            _timeZone = TimeZoneInfo.Local;
        }

        try
        {
            _culture = string.IsNullOrWhiteSpace(clockElement.Culture)
                ? CultureInfo.CurrentCulture
                : CultureInfo.GetCultureInfo(clockElement.Culture);
        }
        catch (CultureNotFoundException)
        {
            _culture = CultureInfo.CurrentCulture;
        }

        if (!string.IsNullOrWhiteSpace(clockElement.OpacityExpression))
        {
            var expression = new NCalc.Expression(clockElement.OpacityExpression);
            expression.EvaluateFunction += OpacityExpressionHelper.EvaluateFunction;
            _maybeOpacityExpression = expression;
        }
        else
        {
            _opacity = (clockElement.OpacityPercent ?? 100) / 100.0f;
        }

        foreach (string part in (clockElement.CountdownTo ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DateTimeOffset.TryParse(part, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset target))
            {
                _countdownTargets.Add(target);
            }
        }

        _countdownTargets = _countdownTargets.Order().ToList();

        if (string.IsNullOrWhiteSpace(clockElement.Text))
        {
            clockElement.Text = "{clock}";
        }

        if (string.IsNullOrWhiteSpace(clockElement.Format))
        {
            clockElement.Format = "HH:mm";
        }

        return Task.CompletedTask;
    }

    public override async ValueTask<Option<PreparedElementImage>> PrepareImage(
        TimeSpan timeOfDay,
        TimeSpan contentTime,
        TimeSpan contentTotalTime,
        TimeSpan channelTime,
        CancellationToken cancellationToken)
    {
        float opacity = _opacity;
        foreach (NCalc.Expression expression in _maybeOpacityExpression)
        {
            opacity = OpacityExpressionHelper.GetOpacity(
                expression,
                timeOfDay,
                contentTime,
                contentTotalTime,
                channelTime);
        }

        if (opacity == 0)
        {
            return Option<PreparedElementImage>.None;
        }

        DateTimeOffset frameTime = _context.ContentStartTime + contentTime;
        DateTimeOffset local = TimeZoneInfo.ConvertTime(frameTime, _timeZone);

        if (clockElement.Analog)
        {
            return RenderAnalog(local, opacity);
        }
        string text = clockElement.Text.Replace("{clock}", local.ToString(clockElement.Format, _culture), StringComparison.Ordinal);

        if (text.Contains("{countdown}", StringComparison.Ordinal))
        {
            int index = _countdownTargets.FindIndex(t => t > frameTime.AddSeconds(1));
            if (index < 0)
            {
                return Option<PreparedElementImage>.None;
            }

            TimeSpan left = _countdownTargets[index] - frameTime;
            if (clockElement.CountdownMaxMinutes > 0 && left.TotalMinutes > clockElement.CountdownMaxMinutes)
            {
                return Option<PreparedElementImage>.None;
            }

            text = text.Replace("{countdown}", FormatSpan(left), StringComparison.Ordinal);
        }

        if (text.Contains("{restant}", StringComparison.Ordinal))
        {
            text = text.Replace("{restant}", FormatSpan(contentTotalTime - contentTime), StringComparison.Ordinal);
        }

        if (text != _lastText || _current is null)
        {
            _current?.Dispose();

            TextGraphicsElement snapshot = CloneWithText(text);
            _current = new TextElement(graphicsEngineFonts, snapshot, logger);
            await _current.InitializeAsync(_context, cancellationToken);
            _lastText = text;

            // fonts only need to be loaded once
            clockElement.IncludeFontsFrom = null;
        }

        SKBitmap image = _current.RenderedImage;
        if (image is null)
        {
            return Option<PreparedElementImage>.None;
        }

        return new PreparedElementImage(image, _current.RenderedLocation, opacity, ZIndex, false);
    }

    // ------------------------------------------------------------------ horloge à aiguilles

    private Option<PreparedElementImage> RenderAnalog(DateTimeOffset local, float opacity)
    {
        string key = clockElement.AnalogSeconds ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (key != _analogKey || _analogBitmap is null)
        {
            int frameWidth = _context.FrameSize.Width;
            int frameHeight = _context.FrameSize.Height;
            int size = Math.Max(16, (int)Math.Round(Math.Clamp(clockElement.AnalogSizePercent, 2, 100) / 100.0 * frameHeight));

            SKBitmap previous = _analogBitmap;
            _analogBitmap = DrawAnalog(size, local);
            previous?.Dispose();

            _analogLocation = CalculatePosition(
                clockElement.Location,
                frameWidth,
                frameHeight,
                size,
                size,
                (int)Math.Round((clockElement.HorizontalMarginPercent ?? 0) / 100.0 * frameWidth),
                (int)Math.Round((clockElement.VerticalMarginPercent ?? 0) / 100.0 * frameHeight));
            _analogKey = key;
        }

        return new PreparedElementImage(_analogBitmap, _analogLocation, opacity, ZIndex, false);
    }

    private SKBitmap DrawAnalog(int size, DateTimeOffset local)
    {
        var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        SKColor color = ParseColor(clockElement.AnalogColor, SKColors.White);
        SKColor face = ParseColor(clockElement.AnalogFaceColor, SKColors.Transparent);
        SKColor secondsColor = ParseColor(clockElement.AnalogSecondsColor, new SKColor(0xFF, 0x39, 0x35));
        bool hasShadow = !string.IsNullOrWhiteSpace(clockElement.AnalogShadowColor);
        SKColor shadow = ParseColor(clockElement.AnalogShadowColor, new SKColor(0, 0, 0, 160));

        float c = size / 2f;
        float outline = (float)(Math.Clamp(clockElement.AnalogOutlinePercent, 0, 20) / 100.0 * size);
        float shadowExtra = hasShadow ? Math.Max(2f, size * 0.025f) : 0f;
        float radius = c - outline / 2f - shadowExtra - 1f;

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = face };
        canvas.DrawCircle(c, c, radius, fill);

        void Stroke(Action<SKPaint> draw, float width, SKColor strokeColor)
        {
            if (hasShadow)
            {
                using var under = new SKPaint
                {
                    IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round,
                    StrokeWidth = width + shadowExtra * 2, Color = shadow
                };
                draw(under);
            }

            using var paint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round,
                StrokeWidth = width, Color = strokeColor
            };
            draw(paint);
        }

        void Dot(float x, float y, float r)
        {
            if (hasShadow)
            {
                using var under = new SKPaint { IsAntialias = true, Color = shadow };
                canvas.DrawCircle(x, y, r + shadowExtra, under);
            }

            using var paint = new SKPaint { IsAntialias = true, Color = color };
            canvas.DrawCircle(x, y, r, paint);
        }

        if (outline > 0)
        {
            Stroke(p => canvas.DrawCircle(c, c, radius, p), outline, color);
        }

        // repères des heures
        float inner = radius - outline / 2f;
        string ticks = (clockElement.AnalogTicks ?? "dots").ToLowerInvariant();
        for (var i = 0; i < 12; i++)
        {
            double a = i * Math.PI / 6;
            bool major = i % 3 == 0;
            if (ticks == "dots")
            {
                float r = size * (major ? 0.032f : 0.022f);
                float d = inner - size * 0.09f;
                Dot(c + (float)(Math.Sin(a) * d), c - (float)(Math.Cos(a) * d), r);
            }
            else if (ticks == "lines")
            {
                float from = inner - size * (major ? 0.16f : 0.11f);
                float to = inner - size * 0.05f;
                float w = size * (major ? 0.04f : 0.022f);
                Stroke(
                    p => canvas.DrawLine(
                        c + (float)(Math.Sin(a) * from), c - (float)(Math.Cos(a) * from),
                        c + (float)(Math.Sin(a) * to), c - (float)(Math.Cos(a) * to), p),
                    w,
                    color);
            }
        }

        // aiguilles
        double minutes = local.Minute + local.Second / 60.0;
        double hours = local.Hour % 12 + minutes / 60.0;

        void Hand(double angleDegrees, float length, float width, SKColor handColor)
        {
            double a = angleDegrees * Math.PI / 180;
            Stroke(
                p => canvas.DrawLine(c, c, c + (float)(Math.Sin(a) * length), c - (float)(Math.Cos(a) * length), p),
                width,
                handColor);
        }

        Hand(hours * 30, inner * 0.50f, size * 0.075f, color);
        Hand(minutes * 6, inner * 0.78f, size * 0.055f, color);
        if (clockElement.AnalogSeconds)
        {
            Hand(local.Second * 6, inner * 0.82f, size * 0.022f, secondsColor);
        }

        Dot(c, c, size * 0.045f);
        canvas.Flush();
        return bitmap;
    }

    private static SKColor ParseColor(string value, SKColor fallback) =>
        !string.IsNullOrWhiteSpace(value) && SKColor.TryParse(value, out SKColor color) ? color : fallback;

    private static string FormatSpan(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        // arrondi à la seconde supérieure : « 0:01 » reste affiché jusqu'au top
        span = TimeSpan.FromSeconds(Math.Ceiling(span.TotalSeconds));
        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private TextGraphicsElement CloneWithText(string text) =>
        new()
        {
            Name = clockElement.Name,
            SourceFileName = clockElement.SourceFileName,
            OpacityPercent = 100,
            Location = clockElement.Location,
            HorizontalMarginPercent = clockElement.HorizontalMarginPercent,
            VerticalMarginPercent = clockElement.VerticalMarginPercent,
            WidthPercent = clockElement.WidthPercent,
            Fit = clockElement.Fit,
            Align = clockElement.Align,
            ZIndex = clockElement.ZIndex,
            Styles = clockElement.Styles,
            BaseStyle = clockElement.BaseStyle,
            IncludeFontsFrom = clockElement.IncludeFontsFrom,
            Text = text
        };
}
