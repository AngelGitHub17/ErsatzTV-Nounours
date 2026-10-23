using ErsatzTV.FFmpeg.State;
using YamlDotNet.Serialization;

namespace ErsatzTV.Core.Graphics;

/// <summary>
///     Quadrillage façon années 90, dessiné par-dessus le programme en direct :
///     « rideau » = des cases de couleur couvrent puis découvrent l'image au début et à la fin de chaque programme ;
///     « suivant » = un bloc de cases « TOUT DE SUITE » + titre du programme suivant, dans les dernières secondes.
/// </summary>
public class GridGraphicsElement : BaseGraphicsElement
{
    /// <summary>« rideau » ou « suivant ».</summary>
    public string Mode { get; set; } = "rideau";

    [YamlMember(Alias = "z_index", ApplyNamingConventions = false)]
    public int? ZIndex { get; set; }

    [YamlMember(Alias = "opacity_percent", ApplyNamingConventions = false)]
    public int? OpacityPercent { get; set; }

    [YamlMember(Alias = "opacity_expression", ApplyNamingConventions = false)]
    public string OpacityExpression { get; set; }

    /// <summary>Couleurs des cases (#RRGGBB) : vive principale, seconde, gris foncé, puis couleurs d'appoint. Noir et blanc toujours présents.</summary>
    public List<string> Palette { get; set; } = ["#FF3200", "#8C0A11", "#3C3632", "#008A22", "#0B2B70", "#FFA800"];

    /// <summary>Couleur des fins liserés entre les cases (vide = aucun).</summary>
    [YamlMember(Alias = "line_color", ApplyNamingConventions = false)]
    public string LineColor { get; set; } = "#E1E8CD";

    /// <summary>Rideau : durée de l'ouverture et de la fermeture, en secondes.</summary>
    [YamlMember(Alias = "curtain_seconds", ApplyNamingConventions = false)]
    public double CurtainSeconds { get; set; } = 1.6;

    /// <summary>Rideau à l'ouverture (début du programme) et/ou à la fermeture (fin).</summary>
    [YamlMember(Alias = "at_start", ApplyNamingConventions = false)]
    public bool AtStart { get; set; } = true;

    [YamlMember(Alias = "at_end", ApplyNamingConventions = false)]
    public bool AtEnd { get; set; } = true;

    /// <summary>Ignorer les programmes plus courts (jingles, pubs) : en secondes.</summary>
    [YamlMember(Alias = "min_content_seconds", ApplyNamingConventions = false)]
    public double MinContentSeconds { get; set; } = 90;

    /// <summary>Suivant : affiché pendant les N dernières secondes du programme.</summary>
    [YamlMember(Alias = "visible_seconds", ApplyNamingConventions = false)]
    public double VisibleSeconds { get; set; } = 20;

    public string Label { get; set; } = "TOUT DE SUITE";

    /// <summary>Titre du programme suivant (rempli par le gabarit Scriban, ex. {{ Epg[1].Title }}).</summary>
    [YamlMember(Alias = "next_title", ApplyNamingConventions = false)]
    public string NextTitle { get; set; }

    /// <summary>Heure du programme suivant (« 21H00 »), facultatif.</summary>
    [YamlMember(Alias = "next_time", ApplyNamingConventions = false)]
    public string NextTime { get; set; }

    public WatermarkLocation Location { get; set; } = WatermarkLocation.BottomLeft;

    [YamlMember(Alias = "horizontal_margin_percent", ApplyNamingConventions = false)]
    public double? HorizontalMarginPercent { get; set; }

    [YamlMember(Alias = "vertical_margin_percent", ApplyNamingConventions = false)]
    public double? VerticalMarginPercent { get; set; }

    /// <summary>Largeur du bloc « suivant » en % de l'image.</summary>
    [YamlMember(Alias = "width_percent", ApplyNamingConventions = false)]
    public double WidthPercent { get; set; } = 46;

    /// <summary>Dossier des polices (Roboto Black y est fournie).</summary>
    [YamlMember(Alias = "font_file", ApplyNamingConventions = false)]
    public string FontFile { get; set; }

    public int Seed { get; set; } = 7;

    /// <summary>Nombre d'entrées du guide chargées pour le gabarit (2 = en cours + suivant).</summary>
    [YamlMember(Alias = "epg_entries", ApplyNamingConventions = false)]
    public int EpgEntries { get; set; }
}
