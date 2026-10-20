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
}
