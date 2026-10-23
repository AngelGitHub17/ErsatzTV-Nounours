using ErsatzTV.Core;
using ErsatzTV.Core.Graphics;
using ErsatzTV.Core.Interfaces.Streaming;
using SkiaSharp;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

/// <summary>
///     Quadrillage années 90 par-dessus le direct.
///     Rideau : au début du programme les cases couvrent l'image puis disparaissent une à une (dans le désordre),
///     à la fin elles reviennent jusqu'à tout couvrir ; leurs couleurs changent par à-coups, comme sur un temps fort.
///     Suivant : dans les dernières secondes, un bloc de cases « TOUT DE SUITE » + titre et heure du programme suivant.
/// </summary>
public sealed class GridElement(GridGraphicsElement grid) : GraphicsElement, IDisposable
{
    private const double Step = 0.32; // les cases changent de couleur toutes les 0,32 s

    private readonly SKBitmap[] _frames = new SKBitmap[2];
    private int _frameIndex;
    private int _width;
    private int _height;
    private int _cols;
    private const int Rows = 3;
    private SKColor[] _palette = [];
    private SKColor? _line;
    private SKTypeface _typeface;
    private int[] _order = [];
    private int _seed;
    private Option<NCalc.Expression> _maybeOpacityExpression;

    // bloc « suivant »
    private SKRect _block;
    private SKPointI _blockAt;

    public override int ZIndex { get; } = grid.ZIndex ?? 50;

    public override string DebugKey { get; } = $"Grid {grid.DebugName()}";

    private bool IsCurtain => !string.Equals(grid.Mode, "suivant", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        foreach (SKBitmap frame in _frames)
        {
            frame?.Dispose();
        }

        _typeface?.Dispose();
    }

    public override Task InitializeAsync(GraphicsEngineContext context, CancellationToken cancellationToken)
    {
        _width = context.FrameSize.Width;
        _height = context.FrameSize.Height;
        _cols = Math.Max(2, (int)Math.Round(4.0 * _width / _height / (16.0 / 9)));
        _palette = (grid.Palette ?? []).Select(c => SKColor.TryParse(c, out SKColor color) ? color : SKColors.Gray).ToArray();
        if (_palette.Length == 0)
        {
            _palette = [new SKColor(0xFF, 0x32, 0x00), new SKColor(0x8C, 0x0A, 0x11), new SKColor(0x3C, 0x36, 0x32)];
        }

        _line = !string.IsNullOrWhiteSpace(grid.LineColor) && SKColor.TryParse(grid.LineColor, out SKColor line) ? line : null;
        _seed = HashCode.Combine(grid.Seed, context.ContentStartTime.ToUnixTimeSeconds());

        string font = !string.IsNullOrWhiteSpace(grid.FontFile) && File.Exists(grid.FontFile)
            ? grid.FontFile
            : Path.Combine(FileSystemLayout.FontsCacheFolder, "Roboto-Black.ttf");
        _typeface = (File.Exists(font) ? SKTypeface.FromFile(font) : null)
                    ?? SKTypeface.FromFamilyName("Roboto", SKFontStyleWeight.Black, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

        // ordre d'apparition des cases : mélangé, différent à chaque programme
        var random = new Random(_seed);
        _order = Enumerable.Range(0, _cols * Rows).OrderBy(_ => random.Next()).ToArray();

        if (!string.IsNullOrWhiteSpace(grid.OpacityExpression))
        {
            var expression = new NCalc.Expression(grid.OpacityExpression);
            expression.EvaluateFunction += OpacityExpressionHelper.EvaluateFunction;
            _maybeOpacityExpression = expression;
        }

        if (IsCurtain)
        {
            for (var i = 0; i < _frames.Length; i++)
            {
                _frames[i] = new SKBitmap(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
            }
        }
        else
        {
            // bloc : une rangée « étiquette » + une rangée « titre », proportions fixes
            int w = Math.Max(40, (int)Math.Round(Math.Clamp(grid.WidthPercent, 15, 100) / 100.0 * _width));
            int h = (int)Math.Round(w * 0.27);
            _block = new SKRect(0, 0, w, h);
            _blockAt = CalculatePosition(
                grid.Location,
                _width,
                _height,
                w,
                h,
                (int)Math.Round((grid.HorizontalMarginPercent ?? 5) / 100.0 * _width),
                (int)Math.Round((grid.VerticalMarginPercent ?? 8) / 100.0 * _height));
            for (var i = 0; i < _frames.Length; i++)
            {
                _frames[i] = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            }
        }

        return Task.CompletedTask;
    }

    public override ValueTask<Option<PreparedElementImage>> PrepareImage(
        TimeSpan timeOfDay,
        TimeSpan contentTime,
        TimeSpan contentTotalTime,
        TimeSpan channelTime,
        CancellationToken cancellationToken)
    {
        if (contentTotalTime.TotalSeconds < grid.MinContentSeconds)
        {
            return None();
        }

        float opacity = (grid.OpacityPercent ?? 100) / 100f;
        foreach (NCalc.Expression expression in _maybeOpacityExpression)
        {
            opacity = OpacityExpressionHelper.GetOpacity(expression, timeOfDay, contentTime, contentTotalTime, channelTime);
        }

        if (opacity <= 0)
        {
            return None();
        }

        double t = contentTime.TotalSeconds;
        double remaining = contentTotalTime.TotalSeconds - t;
        return IsCurtain ? Curtain(t, remaining, opacity) : UpNext(t, remaining, opacity);
    }

    // ------------------------------------------------------------------ rideau

    private ValueTask<Option<PreparedElementImage>> Curtain(double t, double remaining, float opacity)
    {
        double d = Math.Clamp(grid.CurtainSeconds, 0.4, 6);
        int n = _cols * Rows;
        int covered;
        if (grid.AtStart && t < d)
        {
            covered = n - (int)Math.Floor(t / d * (n + 1)); // tout couvert à 0, plus rien à d
        }
        else if (grid.AtEnd && remaining < d)
        {
            covered = (int)Math.Ceiling((1 - Math.Max(0, remaining) / d) * n);
        }
        else
        {
            return None();
        }

        covered = Math.Clamp(covered, 0, n);
        if (covered == 0)
        {
            return None();
        }

        SKBitmap frame = Next();
        int step = (int)Math.Floor(t / Step);
        float cw = _width / (float)_cols;
        float ch = _height / (float)Rows;
        using (var canvas = new SKCanvas(frame))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint();
            using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2, _height * 0.004f), Color = _line ?? SKColors.Transparent };

            // à l'ouverture, les cases qui restent sont les dernières de l'ordre ; à la fermeture, les premières
            bool opening = grid.AtStart && t < d;
            for (var k = 0; k < covered; k++)
            {
                int cell = opening ? _order[n - 1 - k] : _order[k];
                int c = cell % _cols;
                int r = cell / _cols;
                var rect = new SKRect(c * cw, r * ch, (c + 1) * cw + 1, (r + 1) * ch + 1);
                paint.Color = CellColor(cell, step);
                canvas.DrawRect(rect, paint);
                if (_line.HasValue)
                {
                    canvas.DrawRect(rect, stroke);
                }
            }
        }

        return Some(frame, new SKPointI(0, 0), opacity);
    }

    /// <summary>Couleur d'une case : noir, blanc, couleur vive, gris… tirée à chaque « temps ».</summary>
    private SKColor CellColor(int cell, int step)
    {
        var random = new Random(HashCode.Combine(_seed, cell, step));
        double roll = random.NextDouble();
        return roll switch
        {
            < 0.32 => _palette[0],
            < 0.54 => SKColors.Black,
            < 0.72 => SKColors.White,
            < 0.82 => _palette.Length > 1 ? _palette[1] : _palette[0],
            < 0.9 => _palette.Length > 2 ? _palette[2] : SKColors.Gray,
            // couleurs d'appoint (vert, marine, orange…), plus rares
            _ => _palette.Length > 3 ? _palette[3 + random.Next(_palette.Length - 3)] : _palette[0]
        };
    }

    // ------------------------------------------------------------------ tout de suite

    private ValueTask<Option<PreparedElementImage>> UpNext(double t, double remaining, float opacity)
    {
        string title = (grid.NextTitle ?? string.Empty).Trim();
        double visible = Math.Clamp(grid.VisibleSeconds, 3, 600);
        if (title.Length == 0 || remaining > visible || remaining < 0.2)
        {
            return None();
        }

        double shown = visible - remaining; // depuis quand le bloc est là
        // apparition case par case (0 → 0,45 s), disparition dans l'ordre inverse (0,4 dernières secondes)
        int pieces = shown < 0.15 ? 1 : shown < 0.3 ? 2 : 3;
        if (remaining < 0.4)
        {
            pieces = remaining < 0.27 ? 1 : 2;
        }

        SKBitmap frame = Next();
        float w = _block.Width;
        float h = _block.Height;
        float top = h * 0.38f; // rangée étiquette
        float unit = top; // case carrée qui clignote
        int step = (int)Math.Floor(t / Step);
        using (var canvas = new SKCanvas(frame))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { IsAntialias = true };

            // 1. étiquette (couleur vive, texte blanc)
            string label = string.IsNullOrWhiteSpace(grid.Label) ? "TOUT DE SUITE" : grid.Label.ToUpperInvariant();
            using var labelFont = new SKFont(_typeface, top * 0.56f);
            float labelWidth = Math.Min(w - unit, labelFont.MeasureText(label) + top * 0.8f);
            paint.Color = _palette[0];
            canvas.DrawRect(new SKRect(0, 0, labelWidth, top), paint);
            DrawText(canvas, label, labelFont, new SKRect(top * 0.4f, 0, labelWidth - top * 0.2f, top), SKColors.White, false);

            // 2. petite case qui change de couleur au rythme
            if (pieces >= 2)
            {
                SKColor square = CellColor(99, step);
                paint.Color = square == _palette[0] ? SKColors.White : square;
                canvas.DrawRect(new SKRect(labelWidth, 0, labelWidth + unit, top), paint);
            }

            // 3. bloc noir : titre (et heure à droite, en couleur vive)
            if (pieces >= 3)
            {
                paint.Color = SKColors.Black;
                canvas.DrawRect(new SKRect(0, top, w, h), paint);
                string time = (grid.NextTime ?? string.Empty).Trim().ToUpperInvariant();
                float right = w - h * 0.12f;
                if (time.Length > 0)
                {
                    using var timeFont = new SKFont(_typeface, (h - top) * 0.5f);
                    float timeWidth = timeFont.MeasureText(time);
                    DrawText(canvas, time, timeFont, new SKRect(right - timeWidth, top, right, h), _palette[0], false);
                    right -= timeWidth + h * 0.15f;
                }

                using var titleFont = new SKFont(_typeface, (h - top) * 0.5f);
                DrawText(canvas, title.ToUpperInvariant(), titleFont, new SKRect(h * 0.12f, top, right, h), SKColors.White, true);
            }
        }

        return Some(frame, _blockAt, opacity);
    }

    /// <summary>Texte centré verticalement, aligné à gauche ; rétréci pour tenir dans la largeur si besoin.</summary>
    private static void DrawText(SKCanvas canvas, string text, SKFont font, SKRect box, SKColor color, bool fit)
    {
        float width = font.MeasureText(text);
        if (fit && width > box.Width && width > 0)
        {
            font.Size *= box.Width / width;
        }

        using var paint = new SKPaint { Color = color, IsAntialias = true };
        SKFontMetrics metrics = font.Metrics;
        canvas.DrawText(text, box.Left, box.MidY - (metrics.Ascent + metrics.Descent) / 2, font, paint);
    }

    // ------------------------------------------------------------------ outils

    private SKBitmap Next()
    {
        SKBitmap frame = _frames[_frameIndex];
        _frameIndex = (_frameIndex + 1) % _frames.Length;
        return frame;
    }

    private static ValueTask<Option<PreparedElementImage>> None() => ValueTask.FromResult(Option<PreparedElementImage>.None);

    private ValueTask<Option<PreparedElementImage>> Some(SKBitmap frame, SKPointI at, float opacity) =>
        ValueTask.FromResult<Option<PreparedElementImage>>(new PreparedElementImage(frame, at, opacity, ZIndex, false));
}
