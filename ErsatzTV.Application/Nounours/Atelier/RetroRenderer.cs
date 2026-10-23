using ErsatzTV.Core;
using SkiaSharp;

namespace ErsatzTV.Application.Nounours.Atelier;

/// <summary>
///     Dessine les jingles « Rétro 95 » image par image : des rectangles de couleur qui grandissent avec
///     les graves / médiums / aigus et changent de place à chaque temps fort, un cadre clair, des bandeaux
///     noirs en capitales italiques. Aucune marque : le style seulement.
/// </summary>
public sealed class RetroRenderer : IDisposable
{
    public const int Width = 1920;
    public const int Height = 1080;

    private readonly AudioAnalysis _audio;
    private readonly int _fps;
    private readonly int _frames;
    private readonly SKColor[] _palette;
    private readonly SKColor _frameColor;
    private readonly SKColor _ink;
    private readonly SKColor _text;
    private readonly SKTypeface _black;
    private readonly SKTypeface _bold;
    private readonly AtelierStyle _style;
    private readonly SKRect _window;
    private readonly int[] _onsetIndex;

    public RetroRenderer(AtelierStyle style, AudioAnalysis audio, int fps, int frames)
    {
        _style = style;
        _audio = audio;
        _fps = fps;
        _frames = Math.Max(1, frames);
        _palette = style.Palette.Select(c => Parse(c, SKColors.Gray)).DefaultIfEmpty(SKColors.Gold).ToArray();
        _frameColor = Parse(style.FrameColor, new SKColor(0xF2, 0xEF, 0xE4));
        _ink = Parse(style.Ink, SKColors.Black);
        _text = Parse(style.TextColor, SKColors.White);
        _black = LoadTypeface("Roboto-BlackItalic.ttf", SKFontStyleWeight.Black);
        _bold = LoadTypeface("Roboto-BoldItalic.ttf", SKFontStyleWeight.Bold);
        _window = style.Frame
            ? new SKRect(Width * 0.055f, Height * 0.07f, Width * 0.945f, Height * 0.91f)
            : new SKRect(0, 0, Width, Height);

        // nombre de temps forts cumulé, image par image
        _onsetIndex = new int[_frames];
        var count = 0;
        for (var i = 0; i < _frames; i++)
        {
            if (audio.OnsetAt(i))
            {
                count++;
            }

            _onsetIndex[i] = count;
        }
    }

    public SKRect Window => _window;

    public void Dispose()
    {
        _black.Dispose();
        _bold.Dispose();
    }

    // ------------------------------------------------------------------ fond animé

    /// <summary>Le fond de la fenêtre et les rectangles qui dansent.</summary>
    public void DrawBlocks(SKCanvas canvas, int frame, float presence = 1f)
    {
        int onsets = _onsetIndex[Math.Clamp(frame, 0, _frames - 1)];

        // le fond de l'écran alterne blanc / noir tous les 4 temps forts
        bool dark = onsets / 4 % 2 == 1;
        using (var bg = new SKPaint { Color = dark ? new SKColor(0x10, 0x10, 0x10) : new SKColor(0xFA, 0xFA, 0xF7) })
        {
            canvas.DrawRect(_window, bg);
        }

        float energy = (float)Math.Clamp(_style.Energy, 0.1, 1.5);
        float bass = _audio.BassAt(frame);
        float mid = _audio.MidAt(frame);
        float high = _audio.HighAt(frame);
        int visible = Math.Clamp(3 + (int)Math.Round(mid * 3 * energy), 3, 6);

        for (var i = 0; i < 6; i++)
        {
            // apparition progressive au début et disparition des derniers blocs quand ça se calme
            float show = Math.Clamp((frame - i * 2) / 6f, 0, 1) * presence;
            if (i >= visible)
            {
                show *= 0.0f;
            }

            if (show <= 0.01f)
            {
                continue;
            }

            float level = (i % 3) switch { 0 => bass, 1 => mid, _ => high };

            // position : nouvelle case à chaque temps fort, transition courte
            SKRect target = Slot(i, onsets);
            SKRect previous = Slot(i, Math.Max(0, onsets - 1));
            int sinceOnset = FramesSinceOnset(frame);
            float t = EaseOut(Math.Clamp(sinceOnset / 5f, 0, 1));
            SKRect cell = Lerp(previous, target, t);

            // taille : le bloc « respire » avec sa bande de fréquences, depuis son coin d'ancrage
            float scale = Math.Clamp(0.35f + 0.65f * level * energy, 0.2f, 1f) * show;
            float w = cell.Width * scale;
            float h = cell.Height * scale;
            SKRect rect = (i % 4) switch
            {
                0 => new SKRect(cell.Left, cell.Top, cell.Left + w, cell.Top + h),
                1 => new SKRect(cell.Right - w, cell.Top, cell.Right, cell.Top + h),
                2 => new SKRect(cell.Left, cell.Bottom - h, cell.Left + w, cell.Bottom),
                _ => new SKRect(cell.Right - w, cell.Bottom - h, cell.Right, cell.Bottom)
            };

            SKColor color = _palette[(i + onsets) % _palette.Length];
            if (dark && color.Red < 40 && color.Green < 40 && color.Blue < 40)
            {
                color = _palette[(i + onsets + 1) % _palette.Length];
            }

            using var paint = new SKPaint { Color = color };
            canvas.DrawRect(rect, paint);
        }
    }

    public void DrawFrameBorder(SKCanvas canvas)
    {
        if (!_style.Frame)
        {
            return;
        }

        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        path.AddRect(new SKRect(0, 0, Width, Height));
        path.AddRect(_window);
        using var paint = new SKPaint { Color = _frameColor, IsAntialias = true };
        canvas.DrawPath(path, paint);

        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 70), Style = SKPaintStyle.Stroke, StrokeWidth = 4, IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3)
        };
        canvas.Save();
        canvas.ClipPath(path);
        canvas.DrawRect(_window, shadow);
        canvas.Restore();
    }

    // ------------------------------------------------------------------ bandeaux

    /// <summary>Bandeau noir avec texte en capitales italiques ; <paramref name="reveal" /> 0 → 1 fait glisser le bandeau.</summary>
    public void DrawBar(SKCanvas canvas, string text, float x, float y, float height, float maxWidth, float reveal, bool heavy = true, SKColor? ink = null)
    {
        if (string.IsNullOrWhiteSpace(text) || reveal <= 0)
        {
            return;
        }

        text = text.ToUpperInvariant();
        float size = height * 0.72f;
        using var font = new SKFont(heavy ? _black : _bold, size);
        float textWidth = font.MeasureText(text);
        float padding = height * 0.28f;
        float width = Math.Min(maxWidth, textWidth + padding * 2.2f);
        if (textWidth + padding * 2.2f > maxWidth)
        {
            font.Size = size * (maxWidth - padding * 2.2f) / textWidth;
        }

        float shown = width * EaseOut(Math.Clamp(reveal, 0, 1));
        using var paint = new SKPaint { Color = ink ?? _ink };
        canvas.DrawRect(new SKRect(x, y, x + shown, y + height), paint);

        canvas.Save();
        canvas.ClipRect(new SKRect(x, y, x + shown, y + height));
        using var textPaint = new SKPaint { Color = _text, IsAntialias = true };
        SKFontMetrics metrics = font.Metrics;
        float baseline = y + height / 2 - (metrics.Ascent + metrics.Descent) / 2;
        canvas.DrawText(text, x + padding, baseline, font, textPaint);
        canvas.Restore();
    }

    /// <summary>Petit carré de couleur devant un titre (repère de genre).</summary>
    public void DrawSquare(SKCanvas canvas, float x, float y, float size, int paletteIndex)
    {
        using var paint = new SKPaint { Color = _palette[Math.Abs(paletteIndex) % _palette.Length] };
        canvas.DrawRect(new SKRect(x, y, x + size, y + size), paint);
    }

    // ------------------------------------------------------------------ compositions complètes

    /// <summary>Une image complète d'un jingle / générique / carton.</summary>
    public void DrawJingle(SKCanvas canvas, JingleSpec spec, int frame)
    {
        canvas.Clear(_frameColor);
        float p = frame / (float)Math.Max(1, _frames - 1);
        int tail = Math.Min(_fps, _frames / 5);
        float presence = frame > _frames - tail ? Math.Clamp((_frames - frame) / (float)tail, 0, 1) : 1f;

        DrawBlocks(canvas, frame, spec.Kind == AtelierKind.Generique ? 0.75f * presence : presence);

        float wx = _window.Left;
        float ww = _window.Width;
        float wh = _window.Height;
        float barH = wh * 0.13f;
        float Reveal(float startProgress) => (p - startProgress) * _frames / 8f;

        switch (spec.Kind)
        {
            case AtelierKind.Jingle:
            {
                // l'étiquette d'abord, en grand, puis le titre
                if (p < 0.45f)
                {
                    DrawBar(canvas, spec.Label, wx, _window.Top + wh * 0.42f, wh * 0.2f, ww * 0.6f, Reveal(0.04f));
                }

                DrawBar(canvas, spec.Title, wx, _window.Bottom - wh * 0.33f, barH, ww * 0.9f, Reveal(0.42f));
                DrawBar(canvas, spec.Subtitle, wx, _window.Bottom - wh * 0.33f + barH * 1.06f, barH * 0.58f, ww * 0.75f, Reveal(0.5f), false);
                break;
            }

            case AtelierKind.Annonce:
            {
                DrawBar(canvas, spec.Label, wx, _window.Top, wh * 0.11f, ww * 0.6f, Reveal(0.02f));
                DrawBar(canvas, spec.Title, wx, _window.Bottom - wh * 0.34f, barH, ww * 0.9f, Reveal(0.2f));
                DrawBar(canvas, spec.Subtitle, wx, _window.Bottom - wh * 0.34f + barH * 1.06f, barH * 0.62f, ww * 0.75f, Reveal(0.3f), false);
                break;
            }

            case AtelierKind.DebutPub:
            case AtelierKind.FinPub:
            {
                string text = string.IsNullOrWhiteSpace(spec.Title)
                    ? spec.Kind == AtelierKind.DebutPub ? "PUBLICITÉ" : "FIN DE LA PUBLICITÉ"
                    : spec.Title;
                DrawBar(canvas, text, wx, _window.Top + wh * 0.4f, wh * 0.18f, ww * 0.9f, Reveal(0.05f));
                break;
            }

            case AtelierKind.Generique:
            {
                // panneau noir à gauche, comme une fiche de générique ; les lignes apparaissent une à une
                float panelW = ww * 0.62f;
                using (var panel = new SKPaint { Color = new SKColor(0x6E, 0x6A, 0x66, 235) })
                {
                    float open = EaseOut(Math.Clamp(Reveal(0.02f), 0, 1));
                    canvas.DrawRect(new SKRect(wx + ww - panelW * open, _window.Top, wx + ww, _window.Bottom), panel);
                }

                DrawBar(canvas, spec.Title, wx, _window.Top, wh * 0.14f, ww * 0.36f, Reveal(0.05f));
                List<string> lines = spec.Credits.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                float lineH = Math.Min(wh * 0.075f, wh * 0.8f / Math.Max(1, lines.Count * 2));
                float y = _window.Top + wh * 0.05f;
                for (var i = 0; i < lines.Count; i++)
                {
                    float start = 0.12f + 0.7f * i / Math.Max(1, lines.Count);
                    if (p < start)
                    {
                        break;
                    }

                    string[] parts = lines[i].Split(':', 2);
                    using var roleFont = new SKFont(_bold, lineH * 0.62f);
                    using var nameFont = new SKFont(_black, lineH * 0.78f);
                    using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
                    float x = wx + ww - panelW + ww * 0.03f;
                    if (parts.Length == 2)
                    {
                        canvas.DrawText(parts[0].Trim().ToUpperInvariant(), x, y + lineH * 0.7f, roleFont, white);
                        y += lineH * 0.8f;
                        canvas.DrawText(parts[1].Trim().ToUpperInvariant(), x + ww * 0.01f, y + lineH * 0.8f, nameFont, white);
                    }
                    else
                    {
                        canvas.DrawText(lines[i].Trim().ToUpperInvariant(), x, y + lineH * 0.8f, nameFont, white);
                    }

                    y += lineH * 1.25f;
                }

                break;
            }
        }

        DrawFrameBorder(canvas);

        // cartouche de la chaîne à la fin
        if (!string.IsNullOrWhiteSpace(spec.ChannelName))
        {
            float reveal = (p - 0.72f) * _frames / 8f;
            DrawBar(canvas, spec.ChannelName, _window.Left, _window.Bottom - wh * 0.09f, wh * 0.075f, ww * 0.4f, reveal);
        }
    }

    /// <summary>Calque des bandes-annonces : cadre clair + bandeaux sous l'image (PNG transparent posé sur la vidéo).</summary>
    public void DrawTrailerOverlay(SKCanvas canvas, string title, string dateLine)
    {
        canvas.Clear(SKColors.Transparent);
        DrawFrameBorder(canvas);
        float wh = _window.Height;
        float barH = wh * 0.08f;
        float y = _window.Bottom - barH * 2.4f;
        DrawBar(canvas, "    " + title, _window.Left, y, barH, _window.Width * 0.9f, 1);
        DrawBar(canvas, dateLine, _window.Left, y + barH * 1.06f, barH * 0.78f, _window.Width * 0.9f, 1, false);
        // le carré par-dessus le bandeau
        DrawSquare(canvas, _window.Left + barH * 0.3f, y + barH * 0.2f, barH * 0.6f, 4);
    }

    // ------------------------------------------------------------------ outils

    private int FramesSinceOnset(int frame)
    {
        for (int i = Math.Min(frame, _frames - 1); i >= 0; i--)
        {
            if (_audio.OnsetAt(i))
            {
                return frame - i;
            }
        }

        return 99;
    }

    private SKRect Slot(int block, int onsets)
    {
        // grille 4 × 3 dans la fenêtre, un bloc couvre 1 à 2 colonnes et 1 à 2 lignes
        var random = new Random(HashCode.Combine(_style.Seed, block, onsets));
        int col = random.Next(0, 4);
        int row = random.Next(0, 3);
        int spanX = Math.Min(4 - col, random.Next(1, 3));
        int spanY = Math.Min(3 - row, random.Next(1, 3));
        float cw = _window.Width / 4f;
        float ch = _window.Height / 3f;
        return new SKRect(
            _window.Left + col * cw,
            _window.Top + row * ch,
            _window.Left + (col + spanX) * cw,
            _window.Top + (row + spanY) * ch);
    }

    private static SKRect Lerp(SKRect a, SKRect b, float t) =>
        new(a.Left + (b.Left - a.Left) * t, a.Top + (b.Top - a.Top) * t, a.Right + (b.Right - a.Right) * t, a.Bottom + (b.Bottom - a.Bottom) * t);

    private static float EaseOut(float t) => 1 - (1 - t) * (1 - t) * (1 - t);

    private static SKColor Parse(string value, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string v = value.Trim();
        // CSS #RRGGBBAA → Skia #AARRGGBB
        if (v.Length == 9 && v[0] == '#')
        {
            v = "#" + v[7..9] + v[1..7];
        }

        return SKColor.TryParse(v, out SKColor color) ? color : fallback;
    }

    private static SKTypeface LoadTypeface(string file, SKFontStyleWeight weight)
    {
        string path = Path.Combine(FileSystemLayout.FontsCacheFolder, file);
        if (File.Exists(path))
        {
            SKTypeface typeface = SKTypeface.FromFile(path);
            if (typeface is not null)
            {
                return typeface;
            }
        }

        return SKTypeface.FromFamilyName("Roboto", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Italic)
               ?? SKTypeface.Default;
    }
}
