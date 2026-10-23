using YamlDotNet.Serialization;

namespace ErsatzTV.Core.Graphics;

/// <summary>
///     Horloge en direct : un élément texte qui se redessine dès que la valeur affichée change.
///     Le texte peut contenir le jeton {clock}, remplacé par l'heure formatée.
/// </summary>
public class ClockGraphicsElement : TextGraphicsElement
{
    /// <summary>Format .NET de date/heure, ex. "HH:mm", "HH:mm:ss", "dddd d MMMM".</summary>
    public string Format { get; set; } = "HH:mm";

    /// <summary>Fuseau horaire IANA ou Windows, ex. "Europe/Paris". Vide = fuseau du serveur.</summary>
    [YamlMember(Alias = "time_zone", ApplyNamingConventions = false)]
    public string TimeZone { get; set; }

    /// <summary>Culture utilisée pour les noms de jours/mois, ex. "fr-FR".</summary>
    public string Culture { get; set; } = "fr-FR";

    /// <summary>
    ///     Cibles du jeton {countdown}, séparées par « ; » (dates ISO 8601) : la première dans le futur est utilisée.
    ///     Typiquement les débuts des prochains programmes du guide.
    /// </summary>
    [YamlMember(Alias = "countdown_to", ApplyNamingConventions = false)]
    public string CountdownTo { get; set; }

    /// <summary>N'affiche l'élément que si le compte à rebours est inférieur à cette durée (minutes). 0 = toujours.</summary>
    [YamlMember(Alias = "countdown_max_minutes", ApplyNamingConventions = false)]
    public double CountdownMaxMinutes { get; set; }

    // ------------------------------------------------------------------ horloge à aiguilles

    /// <summary>Horloge à aiguilles (cadran rond) au lieu de l'heure en texte.</summary>
    public bool Analog { get; set; }

    /// <summary>Diamètre du cadran, en % de la hauteur de l'image.</summary>
    [YamlMember(Alias = "analog_size_percent", ApplyNamingConventions = false)]
    public double AnalogSizePercent { get; set; } = 14;

    /// <summary>Couleur du contour, des repères et des aiguilles (#AARRGGBB).</summary>
    [YamlMember(Alias = "analog_color", ApplyNamingConventions = false)]
    public string AnalogColor { get; set; } = "#FFFFFFFF";

    /// <summary>Couleur de fond du cadran (#AARRGGBB), transparent par défaut.</summary>
    [YamlMember(Alias = "analog_face_color", ApplyNamingConventions = false)]
    public string AnalogFaceColor { get; set; } = "#00000000";

    /// <summary>Couleur de l'aiguille des secondes (#AARRGGBB).</summary>
    [YamlMember(Alias = "analog_seconds_color", ApplyNamingConventions = false)]
    public string AnalogSecondsColor { get; set; } = "#FFE53935";

    /// <summary>Ombre / contour sombre autour des traits pour rester lisible sur une image claire (#AARRGGBB, vide = aucun).</summary>
    [YamlMember(Alias = "analog_shadow_color", ApplyNamingConventions = false)]
    public string AnalogShadowColor { get; set; }

    /// <summary>Épaisseur du contour du cadran en % du diamètre (0 = pas de contour).</summary>
    [YamlMember(Alias = "analog_outline_percent", ApplyNamingConventions = false)]
    public double AnalogOutlinePercent { get; set; } = 6;

    /// <summary>Repères : "dots" (points), "lines" (traits), "none".</summary>
    [YamlMember(Alias = "analog_ticks", ApplyNamingConventions = false)]
    public string AnalogTicks { get; set; } = "dots";

    /// <summary>Afficher l'aiguille des secondes.</summary>
    [YamlMember(Alias = "analog_seconds", ApplyNamingConventions = false)]
    public bool AnalogSeconds { get; set; }
}
