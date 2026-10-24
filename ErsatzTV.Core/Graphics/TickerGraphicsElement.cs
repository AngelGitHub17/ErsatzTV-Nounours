using YamlDotNet.Serialization;

namespace ErsatzTV.Core.Graphics;

/// <summary>
///     Bandeau défilant (« ticker ») : un texte qui défile en continu, alimenté par des phrases fixes,
///     un flux RSS (titres de l'actu) et/ou la météo d'une ville (Open-Meteo, sans clé).
///     Reprend le style des éléments texte (police, taille, couleur, position).
/// </summary>
public class TickerGraphicsElement : TextGraphicsElement
{
    /// <summary>Phrases fixes, séparées par « | ».</summary>
    public string Messages { get; set; }

    [YamlMember(Alias = "rss_url", ApplyNamingConventions = false)]
    public string RssUrl { get; set; }

    /// <summary>Nombre de titres RSS repris.</summary>
    [YamlMember(Alias = "rss_items", ApplyNamingConventions = false)]
    public int RssItems { get; set; } = 8;

    [YamlMember(Alias = "weather_latitude", ApplyNamingConventions = false)]
    public double? WeatherLatitude { get; set; }

    [YamlMember(Alias = "weather_longitude", ApplyNamingConventions = false)]
    public double? WeatherLongitude { get; set; }

    /// <summary>Nom affiché devant la météo, ex. « Saintes ».</summary>
    [YamlMember(Alias = "weather_label", ApplyNamingConventions = false)]
    public string WeatherLabel { get; set; }

    /// <summary>Vitesse en pixels par seconde (référence 1080p).</summary>
    public double Speed { get; set; } = 140;

    public string Separator { get; set; } = "  •  ";

    /// <summary>Fond du bandeau (#AARRGGBB), vide = transparent.</summary>
    [YamlMember(Alias = "background_color", ApplyNamingConventions = false)]
    public string BackgroundColor { get; set; } = "#E6000000";

    /// <summary>Hauteur du bandeau en % de l'image.</summary>
    [YamlMember(Alias = "height_percent", ApplyNamingConventions = false)]
    public double HeightPercent { get; set; } = 6;

    /// <summary>Étiquette fixe à gauche (« INFO », « MÉTÉO »…), vide = aucune.</summary>
    [YamlMember(Alias = "label", ApplyNamingConventions = false)]
    public string Label { get; set; }

    [YamlMember(Alias = "label_color", ApplyNamingConventions = false)]
    public string LabelColor { get; set; } = "#FFF5B700";

    /// <summary>Rafraîchissement du RSS et de la météo, en minutes.</summary>
    [YamlMember(Alias = "refresh_minutes", ApplyNamingConventions = false)]
    public int RefreshMinutes { get; set; } = 15;

    /// <summary>Fichier texte relu régulièrement (une phrase par ligne) : bandeaux modifiables en direct.</summary>
    [YamlMember(Alias = "messages_file", ApplyNamingConventions = false)]
    public string MessagesFile { get; set; }

    /// <summary>Relecture en secondes (prioritaire sur refresh_minutes), ex. 5 pour un bandeau d'info en direct.</summary>
    [YamlMember(Alias = "refresh_seconds", ApplyNamingConventions = false)]
    public int RefreshSeconds { get; set; }

    /// <summary>Rien à afficher (fichier vide) : le bandeau disparaît (flash info).</summary>
    [YamlMember(Alias = "hide_when_empty", ApplyNamingConventions = false)]
    public bool HideWhenEmpty { get; set; }

    /// <summary>Texte fixe (ne défile que s'il est trop long) : sujet en cours, « EN DIRECT »…</summary>
    [YamlMember(Alias = "fixed", ApplyNamingConventions = false)]
    public bool Fixed { get; set; }
}
