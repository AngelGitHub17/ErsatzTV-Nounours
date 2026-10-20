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

    public override int ZIndex { get; } = clockElement.ZIndex ?? 0;

    public override string DebugKey { get; } = $"Clock {clockElement.DebugName()}";

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _current?.Dispose();
        _current = null;
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
