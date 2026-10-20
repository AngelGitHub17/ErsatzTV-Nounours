using System.Globalization;
using System.Text.RegularExpressions;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.Scheduling;

/// <summary>
///     Édition Nounours : coupures publicitaires sans chapitres.
///     Dans l'expression d'un remplissage « milieu de programme », la directive
///     <c>@coupures toutes=15 debut=8 fin=5</c> ou <c>@coupures nombre=2 fin=5</c>
///     crée des points de coupure virtuels (en minutes) au lieu d'utiliser les chapitres du fichier.
///     Les éléments trop courts (pubs, jingles…) ne reçoivent aucune coupure.
///     Le reste de l'expression, après un « ; », est une expression ErsatzTV classique.
/// </summary>
public static partial class MidRollBreaks
{
    public sealed record Settings(double? EveryMinutes, int? Count, double StartMinutes, double EndMinutes);

    public static bool TryParse(string expression, out Settings settings, out string remainder)
    {
        settings = null;
        remainder = expression;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        Match match = DirectiveRegex().Match(expression);
        if (!match.Success)
        {
            return false;
        }

        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (Match pair in PairRegex().Matches(match.Groups["args"].Value))
        {
            if (double.TryParse(pair.Groups[2].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                values[pair.Groups[1].Value] = v;
            }
        }

        double? every = values.TryGetValue("toutes", out double e) && e > 0 ? e : null;
        int? count = values.TryGetValue("nombre", out double n) && n >= 1 ? (int)n : null;
        if (every is null && count is null)
        {
            return false;
        }

        settings = new Settings(
            every,
            count,
            values.GetValueOrDefault("debut", every ?? 5),
            values.GetValueOrDefault("fin", 5));
        remainder = match.Groups["rest"].Value.Trim();
        return true;
    }

    /// <summary>Retire la directive pour ne garder que l'expression ErsatzTV classique.</summary>
    public static string StripDirective(string expression) =>
        TryParse(expression, out _, out string remainder) ? remainder : expression;

    /// <summary>Découpe [inPoint, outPoint] en chapitres virtuels. Moins de 2 chapitres = pas de coupure.</summary>
    public static List<MediaChapter> BuildChapters(Settings settings, TimeSpan inPoint, TimeSpan outPoint)
    {
        TimeSpan duration = outPoint - inPoint;
        TimeSpan first = TimeSpan.FromMinutes(Math.Max(0.5, settings.StartMinutes));
        TimeSpan lastAllowed = duration - TimeSpan.FromMinutes(Math.Max(0, settings.EndMinutes));
        var points = new List<TimeSpan>();

        if (lastAllowed > first)
        {
            if (settings.EveryMinutes is { } every)
            {
                for (TimeSpan p = first; p <= lastAllowed; p += TimeSpan.FromMinutes(every))
                {
                    points.Add(p);
                }
            }
            else if (settings.Count is { } count)
            {
                // coupures réparties régulièrement entre « debut » et la fin
                TimeSpan span = lastAllowed - first;
                for (var i = 0; i < count; i++)
                {
                    points.Add(count == 1 ? first + span / 2 : first + span * i / (count - 1));
                }
            }
        }

        if (points.Count == 0)
        {
            return [];
        }

        var chapters = new List<MediaChapter>();
        TimeSpan start = inPoint;
        foreach (TimeSpan point in points.Distinct().Order())
        {
            chapters.Add(new MediaChapter { StartTime = start, EndTime = inPoint + point });
            start = inPoint + point;
        }

        chapters.Add(new MediaChapter { StartTime = start, EndTime = outPoint });
        return chapters;
    }

    [GeneratedRegex(@"^\s*@coupures\b(?<args>[^;]*)(;(?<rest>.*))?$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DirectiveRegex();

    [GeneratedRegex(@"(\w+)\s*=\s*([0-9]+(?:[.,][0-9]+)?)")]
    private static partial Regex PairRegex();
}
