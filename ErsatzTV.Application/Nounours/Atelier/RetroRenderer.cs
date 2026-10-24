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
    private readonly SKRect _screen;
    private readonly int[] _onsetIndex;
    private readonly int[] _beat;
    private readonly SKTypeface _upright;
    private readonly bool _grid;
    private readonly int _cols;
    private const int Rows = 3;
    private int _currentBeat;

    public RetroRenderer(AtelierStyle style, AudioAnalysis audio, int fps, int frames)
    {
        _style = style;
        _audio = audio;
        _fps = fps;
        _frames = Math.Max(1, frames);
        _palette = (style.Seasonal ? ErsatzTV.Core.Graphics.Seasons.Current.Palette.ToList() : style.Palette).Select(c => Parse(c, SKColors.Gray)).DefaultIfEmpty(SKColors.Gold).ToArray();
        _frameColor = Parse(style.FrameColor, new SKColor(0xE1, 0xE8, 0xCD));
        _ink = Parse(style.Ink, SKColors.Black);
        _text = Parse(style.TextColor, SKColors.White);
        _black = LoadTypeface("Roboto-BlackItalic.ttf", SKFontStyleWeight.Black);
        _bold = LoadTypeface("Roboto-BoldItalic.ttf", SKFontStyleWeight.Bold);
        _upright = LoadTypeface("Roboto-Black.ttf", SKFontStyleWeight.Black, false);
        _grid = !string.Equals(style.Layout, "libre", StringComparison.OrdinalIgnoreCase);
        bool fourThree = string.Equals(style.Format, "4/3", StringComparison.Ordinal);

        // écran : 16/9 plein cadre, ou 4/3 centré avec bandes noires ; fenêtre = écran moins 1/12 de chaque côté
        _screen = fourThree ? new SKRect((Width - Height * 4f / 3) / 2, 0, (Width + Height * 4f / 3) / 2, Height) : new SKRect(0, 0, Width, Height);
        _window = !style.Frame
            ? _screen
            : _grid
                ? new SKRect(_screen.Left + _screen.Width / 12, _screen.Top + _screen.Height / 12, _screen.Right - _screen.Width / 12, _screen.Bottom - _screen.Height / 12)
                : new SKRect(_screen.Left + _screen.Width * 0.055f, _screen.Height * 0.07f, _screen.Right - _screen.Width * 0.055f, _screen.Height * 0.91f);
        _cols = fourThree ? 3 : 4;

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

        // en grille, la composition change à chaque temps fort, et au moins toutes les 0,7 s
        _beat = new int[_frames];
        int beat = 0;
        int last = 0;
        int maxGap = (int)(fps * Math.Clamp(1.1 - style.Energy * 0.5, 0.35, 1.2));
        for (var i = 0; i < _frames; i++)
        {
            if (i > 0 && (audio.OnsetAt(i) || i - last >= maxGap))
            {
                beat++;
                last = i;
            }

            _beat[i] = beat;
        }
    }

    public SKRect Screen => _screen;

    public SKRect Window => _window;

    public void Dispose()
    {
        _black.Dispose();
        _bold.Dispose();
        _upright.Dispose();
    }

    // ------------------------------------------------------------------ fond animé

    /// <summary>Le fond de la fenêtre et les rectangles qui dansent.</summary>
    public void DrawBlocks(SKCanvas canvas, int frame, float presence = 1f)
    {
        if (_grid)
        {
            DrawGrid(canvas, frame, null);
            return;
        }

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

    // ------------------------------------------------------------------ grille (quadrillage)

    private enum Cell
    {
        Paper,
        Ink,
        Accent,
        Accent2,
        Gray,
        Frame,

        /// <summary>Couleur d'appoint (4e couleur et suivantes de la palette), qui change à chaque temps.</summary>
        Extra
    }

    private SKColor Color(Cell cell) =>
        cell switch
        {
            Cell.Ink => _ink,
            Cell.Accent => _palette[0],
            Cell.Accent2 => _palette.Length > 1 ? _palette[1] : _palette[0],
            Cell.Gray => _palette.Length > 2 ? _palette[2] : new SKColor(0x3C, 0x36, 0x32),
            Cell.Extra => _palette.Length > 3 ? _palette[3 + _currentBeat % (_palette.Length - 3)] : _palette[0],
            Cell.Frame => _frameColor,
            _ => SKColors.White
        };

    private SKRect CellRect(int col, int row, int spanCols = 1, int spanRows = 1)
    {
        float cw = _window.Width / _cols;
        float ch = _window.Height / Rows;
        return new SKRect(
            _window.Left + col * cw,
            _window.Top + row * ch,
            _window.Left + (col + spanCols) * cw,
            _window.Top + (row + spanRows) * ch);
    }

    /// <summary>La composition de cases pour un « temps » : aplat, damier, croix, gros bloc, bande…</summary>
    private Cell[,] Composition(int beat, bool withLabel, out (int Col, int Row) labelAt)
    {
        var random = new Random(HashCode.Combine(_style.Seed, beat, withLabel));
        var cells = new Cell[_cols, Rows];
        labelAt = (-1, -1);

        if (withLabel)
        {
            // un bloc noir de 2 × 2 cases porte l'étiquette ; il change de coin à chaque temps
            int[] cornersX = [0, _cols - 2];
            int[] cornersY = [0, Rows - 2];
            int corner = (beat + _style.Seed) % 4;
            labelAt = (cornersX[corner % 2], cornersY[corner / 2]);
            for (var c = 0; c < _cols; c++)
            {
                for (var r = 0; r < Rows; r++)
                {
                    double roll = random.NextDouble();
                    cells[c, r] = roll < 0.55 ? Cell.Paper : roll < 0.82 ? Cell.Accent : roll < 0.9 ? Cell.Ink : roll < 0.95 ? Cell.Accent2 : Cell.Extra;
                }
            }

            // toujours au moins une case de couleur vive
            cells[(labelAt.Col + 2) % _cols, (labelAt.Row + 2) % Rows] = Cell.Accent;
            for (int c = labelAt.Col; c < labelAt.Col + 2; c++)
            {
                for (int r = labelAt.Row; r < labelAt.Row + 2; r++)
                {
                    cells[c, r] = Cell.Ink;
                }
            }

            return cells;
        }

        double pick = random.NextDouble();
        Cell a = pick < 0.62 ? Cell.Accent : pick < 0.8 ? Cell.Accent2 : Cell.Extra;
        Cell b = random.NextDouble() < 0.6 ? Cell.Paper : Cell.Ink;
        switch (random.Next(0, 7))
        {
            case 0: // aplat
                Fill(cells, random.NextDouble() switch { < 0.35 => Cell.Ink, < 0.65 => a, < 0.8 => Cell.Gray, _ => Cell.Paper });
                break;
            case 1: // damier
            case 2:
                for (var c = 0; c < _cols; c++)
                {
                    for (var r = 0; r < Rows; r++)
                    {
                        cells[c, r] = (c + r + beat) % 2 == 0 ? a : b;
                    }
                }

                break;
            case 3: // gros bloc noir dans un coin, une case vive
            {
                Fill(cells, Cell.Paper);
                int x = random.Next(0, 2) == 0 ? 0 : _cols - 2;
                int y = random.Next(0, 2) == 0 ? 0 : 1;
                for (int c = x; c < x + 2; c++)
                {
                    for (int r = y; r < y + 2; r++)
                    {
                        cells[c, r] = Cell.Ink;
                    }
                }

                cells[x == 0 ? _cols - 1 : 0, y == 0 ? Rows - 1 : 0] = a;
                if (random.NextDouble() < 0.5)
                {
                    cells[x, y] = a;
                }

                break;
            }
            case 4: // bande horizontale
            {
                Fill(cells, b == Cell.Paper ? Cell.Ink : Cell.Paper);
                int row = random.Next(0, Rows);
                for (var c = 0; c < _cols; c++)
                {
                    cells[c, row] = a;
                }

                break;
            }
            case 5: // colonne
            {
                Fill(cells, Cell.Ink);
                int col = random.Next(0, _cols);
                for (var r = 0; r < Rows; r++)
                {
                    cells[col, r] = r == 1 ? Cell.Paper : a;
                }

                break;
            }
            default: // cases éparses
                for (var c = 0; c < _cols; c++)
                {
                    for (var r = 0; r < Rows; r++)
                    {
                        double roll = random.NextDouble();
                        cells[c, r] = roll < 0.42 ? Cell.Ink : roll < 0.7 ? Cell.Paper : roll < 0.88 ? a : roll < 0.95 ? Cell.Gray : Cell.Extra;
                    }
                }

                break;
        }

        return cells;
    }

    private void Fill(Cell[,] cells, Cell value)
    {
        for (var c = 0; c < _cols; c++)
        {
            for (var r = 0; r < Rows; r++)
            {
                cells[c, r] = value;
            }
        }
    }

    /// <summary>Dessine la grille ; avec <paramref name="label" />, le bloc noir 2 × 2 porte ce texte (en capitales droites).</summary>
    public void DrawGrid(SKCanvas canvas, int frame, string label)
    {
        int beat = _beat[Math.Clamp(frame, 0, _frames - 1)];
        _currentBeat = beat;
        Cell[,] cells = Composition(beat, !string.IsNullOrWhiteSpace(label), out (int Col, int Row) labelAt);
        using var paint = new SKPaint();
        for (var c = 0; c < _cols; c++)
        {
            for (var r = 0; r < Rows; r++)
            {
                paint.Color = Color(cells[c, r]);
                SKRect rect = CellRect(c, r);
                rect.Right += 1; // pas de liseré entre les cases
                rect.Bottom += 1;
                canvas.DrawRect(rect, paint);
            }
        }

        if (labelAt.Col >= 0)
        {
            DrawBlockText(canvas, label, CellRect(labelAt.Col, labelAt.Row, 2, 2));
        }
    }

    /// <summary>Texte blanc en capitales grasses, centré et ajusté dans un bloc.</summary>
    private void DrawBlockText(SKCanvas canvas, string text, SKRect block)
    {
        text = text.ToUpperInvariant();
        using var font = new SKFont(_upright, block.Height * 0.32f);
        float width = font.MeasureText(text);
        float max = block.Width * 0.84f;
        if (width > max)
        {
            font.Size *= max / width;
            width = max;
        }

        using var white = new SKPaint { Color = _text, IsAntialias = true };
        SKFontMetrics metrics = font.Metrics;
        canvas.DrawText(text, block.MidX - width / 2, block.MidY - (metrics.Ascent + metrics.Descent) / 2, font, white);
    }

    /// <summary>Petit cartouche noir avec le nom de la chaîne, en bas à gauche de la fenêtre.</summary>
    public void DrawChannelTag(SKCanvas canvas, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (!_grid)
        {
            float h = _window.Height * 0.065f;
            DrawBar(canvas, name, _window.Left, _window.Bottom - h, h, _window.Width * 0.45f, 1);
            return;
        }

        // en grille : une bande en bas à droite, de la largeur d'une case (deux si le nom est long)
        float ch = _window.Height / Rows;
        float strip = ch * 0.24f;
        int span = ColumnsFor(name, strip, 0.62f, 1, Math.Min(2, _cols));
        float cw = _window.Width / _cols;
        var box = new SKRect(_window.Right - cw * span, _window.Bottom - strip, _window.Right, _window.Bottom);
        using (var ink = new SKPaint { Color = _ink })
        {
            canvas.DrawRect(box, ink);
        }

        DrawFitted(canvas, name, box, _text, 0.62f, 1);
    }

    /// <summary>Carton final : texte en capitales italiques sur fond noir (ex. signalétique).</summary>
    public void DrawEndCard(SKCanvas canvas, string message)
    {
        canvas.Clear(SKColors.Black);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string[] words = message.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        using var font = new SKFont(_black, _screen.Height * 0.06f);
        float max = _screen.Width * 0.7f;
        var lines = new List<string>();
        string current = string.Empty;
        foreach (string word in words)
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(candidate) > max && current.Length > 0)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        lines.Add(current);
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        float lineHeight = font.Size * 1.2f;
        float y = _screen.MidY - lines.Count * lineHeight / 2 + font.Size;
        foreach (string line in lines)
        {
            canvas.DrawText(line, _screen.MidX - font.MeasureText(line) / 2, y, font, white);
            y += lineHeight;
        }
    }

    public void DrawFrameBorder(SKCanvas canvas) => DrawFrameBorder(canvas, true);

    public void DrawFrameBorder(SKCanvas canvas, bool frame)
    {
        using (var black = new SKPaint { Color = SKColors.Black })
        {
            // bandes noires du 4/3
            if (_screen.Left > 0)
            {
                canvas.DrawRect(new SKRect(0, 0, _screen.Left, Height), black);
                canvas.DrawRect(new SKRect(_screen.Right, 0, Width, Height), black);
            }
        }

        if (!_style.Frame || !frame)
        {
            return;
        }

        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        path.AddRect(_screen);
        path.AddRect(_window);
        using var paint = new SKPaint { Color = _frameColor, IsAntialias = true };
        canvas.DrawPath(path, paint);

        if (_grid)
        {
            return; // en grille : pas d'ombre, des aplats nets comme à l'époque
        }

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

        if (_grid)
        {
            // en grille, les bandeaux s'arrêtent sur une demi-case : tout reste aligné sur le quadrillage
            float half = _window.Width / _cols / 2;
            width = Math.Min(Math.Max(maxWidth, half), (float)Math.Ceiling(width / half) * half);
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
        if (spec.Kind == AtelierKind.Carton)
        {
            DrawEndCard(canvas, spec.Title);
            DrawFrameBorder(canvas, false);
            return;
        }

        int endFrames = string.IsNullOrWhiteSpace(spec.EndMessage) ? 0 : (int)Math.Round(Math.Clamp(spec.EndSeconds, 0.5, 10) * _fps);
        int main = Math.Max(1, _frames - endFrames);
        if (frame >= main)
        {
            DrawEndCard(canvas, spec.EndMessage);
            DrawFrameBorder(canvas, false);
            return;
        }

        if (_grid && spec.Kind == AtelierKind.Jingle)
        {
            DrawGridJingle(canvas, spec, frame, main);
            return;
        }

        canvas.Clear(_frameColor);
        float p = frame / (float)Math.Max(1, main - 1);
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

    /// <summary>
    ///     Jingle en grille, comme un générique d'époque : compositions abstraites au rythme, puis l'étiquette
    ///     dans un bloc noir qui change de coin, puis le titre ; cartouche de la chaîne en bas à gauche.
    /// </summary>
    private void DrawGridJingle(SKCanvas canvas, JingleSpec spec, int frame, int main)
    {
        canvas.Clear(SKColors.Black);
        float p = frame / (float)Math.Max(1, main - 1);
        bool hasTitle = !string.IsNullOrWhiteSpace(spec.Title);
        float labelFrom = 0.35f;
        float titleFrom = hasTitle ? 0.78f : 2f;

        if (p < labelFrom || string.IsNullOrWhiteSpace(spec.Label) && p < titleFrom)
        {
            DrawGrid(canvas, frame, null);
        }
        else if (p < titleFrom)
        {
            DrawGrid(canvas, frame, spec.Label);
            DrawChannelTag(canvas, spec.ChannelName);
        }
        else
        {
            DrawTitleGrid(canvas, frame, spec, (p - titleFrom) / Math.Max(0.01f, 1 - titleFrom));
        }

        DrawFrameBorder(canvas);
    }

    /// <summary>
    ///     Phase titre, calée sur la grille : l'étiquette en haut à gauche, le titre dans un bloc noir de la largeur
    ///     qu'il lui faut (en cases entières, sur deux lignes au besoin), le sous-titre dans un bloc de couleur dessous,
    ///     les autres cases continuent de changer au rythme. Les blocs apparaissent case par case.
    /// </summary>
    private void DrawTitleGrid(SKCanvas canvas, int frame, JingleSpec spec, float t)
    {
        int beat = _beat[Math.Clamp(frame, 0, _frames - 1)];
        _currentBeat = beat;
        Cell[,] cells = Composition(beat, false, out _);
        float cw = _window.Width / _cols;
        float ch = _window.Height / Rows;

        // blocs réservés : étiquette (rangée 0), titre (rangée 1), sous-titre (rangée 2)
        bool hasLabel = !string.IsNullOrWhiteSpace(spec.Label);
        bool hasSubtitle = !string.IsNullOrWhiteSpace(spec.Subtitle);
        int labelSpan = hasLabel ? ColumnsFor(spec.Label, ch, 0.42f, 1, Math.Min(2, _cols)) : 0;
        int titleSpan = ColumnsFor(spec.Title, ch, 0.36f, 2, _cols);
        int subtitleSpan = hasSubtitle ? ColumnsFor(spec.Subtitle, ch, 0.26f, 2, _cols - 1) : 0;

        using var paint = new SKPaint();
        for (var c = 0; c < _cols; c++)
        {
            for (var r = 0; r < Rows; r++)
            {
                Cell cell = cells[c, r] == Cell.Ink ? Cell.Paper : cells[c, r]; // le noir est réservé aux textes
                paint.Color = Color(cell);
                SKRect rect = CellRect(c, r);
                rect.Right += 1;
                rect.Bottom += 1;
                canvas.DrawRect(rect, paint);
            }
        }

        // apparition case par case, comme les blocs du générique
        float Shown(int column, float from) => Math.Clamp((t - from - column * 0.05f) * 12, 0, 1);

        if (hasLabel)
        {
            DrawRevealBlock(canvas, CellRect(0, 0, labelSpan, 1), labelSpan, c => Shown(c, 0), _ink);
            if (Shown(labelSpan - 1, 0) >= 1)
            {
                DrawFitted(canvas, spec.Label, CellRect(0, 0, labelSpan, 1), _text, 0.42f, 1);
            }
        }

        DrawRevealBlock(canvas, CellRect(0, 1, titleSpan, 1), titleSpan, c => Shown(c, 0.08f), _ink);
        if (Shown(titleSpan - 1, 0.08f) >= 1)
        {
            DrawFitted(canvas, spec.Title, CellRect(0, 1, titleSpan, 1), _text, 0.36f, 2);
        }

        if (hasSubtitle)
        {
            DrawRevealBlock(canvas, CellRect(0, 2, subtitleSpan, 1), subtitleSpan, c => Shown(c, 0.22f), Color(Cell.Accent));
            if (Shown(subtitleSpan - 1, 0.22f) >= 1)
            {
                DrawFitted(canvas, spec.Subtitle, CellRect(0, 2, subtitleSpan, 1), _text, 0.26f, 2);
            }
        }

        DrawChannelTag(canvas, spec.ChannelName);
    }

    /// <summary>Le nombre de cases (entre 1 et <paramref name="max" />) qu'il faut pour que le texte soit lisible.</summary>
    private int ColumnsFor(string text, float cellHeight, float sizeRatio, int maxLines, int max)
    {
        float cw = _window.Width / _cols;
        for (var k = 1; k < max; k++)
        {
            if (FittedSize(text, new SKRect(0, 0, cw * k, cellHeight), sizeRatio, maxLines, out _) >= cellHeight * sizeRatio * 0.8f)
            {
                return k;
            }
        }

        return Math.Max(1, max);
    }

    private static void DrawRevealBlock(SKCanvas canvas, SKRect block, int span, Func<int, float> shown, SKColor color)
    {
        float cw = block.Width / Math.Max(1, span);
        using var paint = new SKPaint { Color = color };
        for (var c = 0; c < span; c++)
        {
            float s = shown(c);
            if (s <= 0)
            {
                continue;
            }

            // la case « tombe » de haut en bas en deux images : coupe franche, pas de glissé
            float bottom = block.Top + block.Height * (s < 0.5f ? 0.5f : 1);
            canvas.DrawRect(new SKRect(block.Left + c * cw, block.Top, block.Left + (c + 1) * cw + 1, bottom + 1), paint);
        }
    }

    /// <summary>Taille de police la plus grande qui tient dans la boîte, sur 1 à <paramref name="maxLines" /> lignes.</summary>
    private float FittedSize(string text, SKRect box, float sizeRatio, int maxLines, out List<string> lines)
    {
        lines = [];
        string[] words = (text ?? string.Empty).ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return 0;
        }

        float best = 0;
        using var font = new SKFont(_upright, 100);
        for (var n = 1; n <= Math.Min(maxLines, words.Length); n++)
        {
            List<string> candidate = Balance(words, n, font);
            float widest = candidate.Max(l => font.MeasureText(l)) / 100;
            float byWidth = box.Width * 0.86f / Math.Max(0.01f, widest);
            float byHeight = Math.Min(box.Height * sizeRatio * (n == 1 ? 1 : 0.82f), box.Height * 0.8f / (n * 1.12f));
            float size = Math.Min(byWidth, byHeight);
            if (size > best * 1.08f)
            {
                best = size;
                lines = candidate;
            }
        }

        return best;
    }

    /// <summary>Répartit les mots sur n lignes de longueurs proches.</summary>
    private static List<string> Balance(string[] words, int n, SKFont font)
    {
        if (n == 1)
        {
            return [string.Join(' ', words)];
        }

        float total = font.MeasureText(string.Join(' ', words));
        var lines = new List<string>();
        var current = new List<string>();
        foreach (string word in words)
        {
            current.Add(word);
            if (lines.Count < n - 1 && font.MeasureText(string.Join(' ', current)) >= total / n * 0.92f)
            {
                lines.Add(string.Join(' ', current));
                current.Clear();
            }
        }

        if (current.Count > 0)
        {
            lines.Add(string.Join(' ', current));
        }

        return lines;
    }

    /// <summary>Texte en capitales droites, ajusté dans la boîte (une ou deux lignes), aligné à gauche, centré en hauteur.</summary>
    private void DrawFitted(SKCanvas canvas, string text, SKRect box, SKColor color, float sizeRatio, int maxLines)
    {
        float size = FittedSize(text, box, sizeRatio, maxLines, out List<string> lines);
        if (size <= 0)
        {
            return;
        }

        using var font = new SKFont(_upright, size);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        SKFontMetrics metrics = font.Metrics;
        float lineHeight = size * 1.08f;
        float height = lineHeight * (lines.Count - 1) - metrics.Ascent;
        float y = box.MidY - height / 2 - metrics.Ascent;
        float x = box.Left + Math.Min(box.Width * 0.07f, box.Height * 0.2f);
        foreach (string line in lines)
        {
            canvas.DrawText(line, x, y, font, paint);
            y += lineHeight;
        }
    }

    /// <summary>
    ///     Carton « Ce soir » : étiquette dans un bloc noir, puis les programmes de la soirée qui arrivent un à un
    ///     sur des bandeaux noirs (« 20H40 · TITRE »), sur un fond quadrillé clair.
    /// </summary>
    public void DrawTonight(SKCanvas canvas, int frame, string label, IReadOnlyList<(string Time, string Title, string Channel)> rows, string channelName)
    {
        canvas.Clear(SKColors.Black);
        float p = frame / (float)Math.Max(1, _frames - 1);
        int beat = _beat[Math.Clamp(frame, 0, _frames - 1)];

        using (var paper = new SKPaint { Color = SKColors.White })
        {
            canvas.DrawRect(_window, paper);
        }

        // deux cases vives qui changent de place au rythme, à droite
        var random = new Random(HashCode.Combine(_style.Seed, beat));
        using (var accent = new SKPaint { Color = Color(Cell.Accent) })
        {
            canvas.DrawRect(CellRect(_cols - 1, random.Next(0, Rows)), accent);
            accent.Color = Color(random.NextDouble() < 0.5 ? Cell.Accent2 : Cell.Gray);
            canvas.DrawRect(CellRect(_cols - 1 - (_cols > 3 ? 1 : 0), random.Next(0, Rows)), accent);
        }

        using (var ink = new SKPaint { Color = _ink })
        {
            canvas.DrawRect(CellRect(0, 0, 2, 1), ink);
        }

        DrawBlockText(canvas, string.IsNullOrWhiteSpace(label) ? "CE SOIR" : label, CellRect(0, 0, 2, 1));

        SKRect area = CellRect(0, 1, _cols, 2);
        int count = Math.Max(1, rows.Count);
        float rowH = Math.Min(area.Height / count * 0.82f, _window.Height * 0.11f);
        float gap = (area.Height - rowH * count) / (count + 1);
        for (var i = 0; i < rows.Count; i++)
        {
            float start = 0.08f + 0.55f * i / Math.Max(1, rows.Count);
            float reveal = (p - start) * _frames / 8f;
            float y = area.Top + gap + i * (rowH + gap);
            (string time, string title, string channel) = rows[i];
            string text = string.IsNullOrWhiteSpace(channel) ? $"{time} · {title}" : $"{time} · {title} · {channel}";
            DrawSquare(canvas, area.Left, y, rowH, i);
            DrawBar(canvas, text, area.Left + rowH * 1.15f, y, rowH, _window.Width - rowH * 1.3f, reveal);
        }

        DrawChannelTag(canvas, channelName);
        DrawFrameBorder(canvas);
    }

    /// <summary>
    ///     Carte météo : bloc « MÉTÉO », puis une ligne par ville — case de couleur avec la température,
    ///     bandeau noir avec le nom de la ville, bandeau plus fin avec le temps et les extrêmes.
    /// </summary>
    public void DrawWeather(SKCanvas canvas, int frame, string label, IReadOnlyList<WeatherRow> rows, string channelName)
    {
        canvas.Clear(SKColors.Black);
        float p = frame / (float)Math.Max(1, _frames - 1);
        _currentBeat = _beat[Math.Clamp(frame, 0, _frames - 1)];
        using (var paper = new SKPaint { Color = SKColors.White })
        {
            canvas.DrawRect(_window, paper);
        }

        // une case vive qui saute d'une rangée à l'autre au rythme, en haut à droite
        var random = new Random(HashCode.Combine(_style.Seed, _currentBeat));
        using (var accent = new SKPaint { Color = Color(random.NextDouble() < 0.7 ? Cell.Accent : Cell.Extra) })
        {
            canvas.DrawRect(CellRect(_cols - 1, 0), accent);
        }

        using (var ink = new SKPaint { Color = _ink })
        {
            canvas.DrawRect(CellRect(0, 0, 2, 1), ink);
        }

        DrawBlockText(canvas, string.IsNullOrWhiteSpace(label) ? "MÉTÉO" : label, CellRect(0, 0, 2, 1));

        SKRect area = CellRect(0, 1, _cols, 2);
        int count = Math.Max(1, rows.Count);
        float rowH = Math.Min(area.Height / count * 0.86f, _window.Height * 0.17f);
        float gap = (area.Height - rowH * count) / (count + 1);
        Cell[] squares = [Cell.Accent, Cell.Extra, Cell.Accent2, Cell.Gray];
        for (var i = 0; i < rows.Count; i++)
        {
            float start = 0.06f + 0.5f * i / count;
            float reveal = (p - start) * _frames / 8f;
            if (reveal <= 0)
            {
                continue;
            }

            float y = area.Top + gap + i * (rowH + gap);
            WeatherRow row = rows[i];
            float squareW = rowH * 1.7f;
            using (var square = new SKPaint { Color = Color(squares[i % squares.Length]) })
            {
                canvas.DrawRect(new SKRect(area.Left, y, area.Left + squareW, y + rowH), square);
            }

            DrawBlockText(canvas, row.Temperature, new SKRect(area.Left, y - rowH * 0.5f, area.Left + squareW, y + rowH * 1.5f));
            float x = area.Left + squareW + rowH * 0.15f;
            DrawBar(canvas, row.City, x, y, rowH * 0.62f, _window.Right - x, reveal);
            DrawBar(canvas, row.Detail, x, y + rowH * 0.64f, rowH * 0.36f, _window.Right - x, reveal - 0.5f, false, Color(Cell.Gray));
        }

        DrawChannelTag(canvas, channelName);
        DrawFrameBorder(canvas);
    }

    /// <summary>Interlude : le quadrillage tourne en boucle, l'étiquette passe d'un coin à l'autre, cartouche de la chaîne.</summary>
    public void DrawInterlude(SKCanvas canvas, int frame, string label, string channelName)
    {
        canvas.Clear(SKColors.Black);
        DrawGrid(canvas, frame, label);
        DrawChannelTag(canvas, channelName);
        DrawFrameBorder(canvas);
    }

    /// <summary>Calque des bandes-annonces : cadre clair + bandeaux sous l'image (PNG transparent posé sur la vidéo).</summary>
    public void DrawTrailerOverlay(SKCanvas canvas, string title, string dateLine)
    {
        canvas.Clear(SKColors.Transparent);
        DrawFrameBorder(canvas);
        float ch = _window.Height / Rows;
        float cw = _window.Width / _cols;

        // en bas à gauche de l'image : le titre dans un bloc noir, la diffusion dans un bloc de couleur dessous,
        // chacun de la largeur qu'il lui faut, en demi-cases
        float titleH = ch * 0.34f;
        float lineH = ch * 0.22f;
        float bottom = _window.Bottom - ch * 0.12f;
        bool hasLine = !string.IsNullOrWhiteSpace(dateLine);
        var titleBox = new SKRect(_window.Left, bottom - titleH - (hasLine ? lineH : 0), _window.Left + HalfCells(title, titleH, 0.62f, cw), bottom - (hasLine ? lineH : 0));
        using (var ink = new SKPaint { Color = _ink })
        {
            canvas.DrawRect(titleBox, ink);
        }

        DrawFitted(canvas, title, titleBox, _text, 0.62f, 1);
        if (hasLine)
        {
            var lineBox = new SKRect(_window.Left, bottom - lineH, _window.Left + HalfCells(dateLine, lineH, 0.6f, cw), bottom);
            using var accent = new SKPaint { Color = Color(Cell.Accent) };
            canvas.DrawRect(lineBox, accent);
            DrawFitted(canvas, dateLine, lineBox, _text, 0.6f, 1);
        }
    }

    /// <summary>Largeur (en demi-cases) qu'il faut pour écrire le texte à la taille voulue.</summary>
    private float HalfCells(string text, float height, float sizeRatio, float cellWidth)
    {
        using var font = new SKFont(_upright, height * sizeRatio);
        float needed = font.MeasureText((text ?? string.Empty).ToUpperInvariant()) + height * 0.5f;
        float half = cellWidth / 2;
        return Math.Clamp((float)Math.Ceiling(needed / half) * half, half, _window.Width);
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

    private static SKTypeface LoadTypeface(string file, SKFontStyleWeight weight, bool italic = true)
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

        return SKTypeface.FromFamilyName("Roboto", weight, SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright)
               ?? SKTypeface.Default;
    }
}
