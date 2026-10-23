namespace ErsatzTV.Core.Graphics;

/// <summary>
///     Habillage saisonnier : la palette des carrés change selon la date, toujours avec les couleurs d'époque
///     (rouge vif, bordeaux, chocolat, vert émeraude, bleu marine, orange). Le noir et le blanc restent partout.
/// </summary>
public static class Seasons
{
    public sealed record Season(string Name, string[] Palette);

    public static readonly Season Default = new("Toute l'année", ["#FF3200", "#8C0A11", "#3C3632", "#008A22", "#0B2B70", "#FFA800"]);
    public static readonly Season Noel = new("Noël", ["#8C0A11", "#008A22", "#3C3632", "#FF3200", "#FFA800"]);
    public static readonly Season Hiver = new("Hiver", ["#0B2B70", "#3C3632", "#FF3200", "#008A22"]);
    public static readonly Season Printemps = new("Printemps", ["#008A22", "#FFA800", "#3C3632", "#FF3200"]);
    public static readonly Season Ete = new("Été", ["#FFA800", "#FF3200", "#0B2B70", "#008A22"]);
    public static readonly Season Halloween = new("Halloween", ["#FFA800", "#3C3632", "#8C0A11", "#FF3200"]);

    public static Season For(DateTime date)
    {
        int md = date.Month * 100 + date.Day;
        return md switch
        {
            >= 1201 or <= 106 => Noel,
            <= 228 or 229 => Hiver,
            >= 1015 and <= 1102 => Halloween,
            >= 320 and <= 620 => Printemps,
            >= 621 and <= 831 => Ete,
            _ => Default
        };
    }

    public static Season Current => For(DateTime.Now);
}
