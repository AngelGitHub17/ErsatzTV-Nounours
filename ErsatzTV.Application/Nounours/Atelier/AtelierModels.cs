namespace ErsatzTV.Application.Nounours.Atelier;

public enum AtelierKind
{
    /// <summary>Jingle avant programme : étiquette (« FILM », « SÉRIE »…), puis titre.</summary>
    Jingle,

    /// <summary>Générique : titre et lignes de crédits (« Production : … »).</summary>
    Generique,

    /// <summary>Carton d'entrée de coupure pub.</summary>
    DebutPub,

    /// <summary>Carton de fin de coupure pub.</summary>
    FinPub,

    /// <summary>Annonce simple : titre + date (« CE SOIR 20H40 »).</summary>
    Annonce
}

/// <summary>Le style « années 90 » : fond, encre, palette des rectangles.</summary>
public sealed class AtelierStyle
{
    /// <summary>Couleur du cadre autour de l'image.</summary>
    public string FrameColor { get; set; } = "#F2EFE4";

    /// <summary>Encre des bandeaux (texte en blanc dessus).</summary>
    public string Ink { get; set; } = "#000000";

    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>Couleurs des rectangles qui dansent.</summary>
    public List<string> Palette { get; set; } = ["#F5B700", "#111111", "#FFFFFF", "#8A8A8A", "#1E9E4A", "#2D5BD7"];

    /// <summary>Afficher le cadre clair autour de l'image.</summary>
    public bool Frame { get; set; } = true;

    /// <summary>Change la disposition des rectangles (même musique, autre chorégraphie).</summary>
    public int Seed { get; set; } = 7;

    /// <summary>Intensité du mouvement (0,3 = calme, 1 = très nerveux).</summary>
    public double Energy { get; set; } = 0.8;
}

public sealed class JingleSpec
{
    public AtelierKind Kind { get; set; } = AtelierKind.Jingle;
    public string Title { get; set; } = "TITRE DU PROGRAMME";
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>Étiquette du jingle : « FILM », « SÉRIE », « CINÉMA », « SPORT »…</summary>
    public string Label { get; set; } = "FILM";

    /// <summary>Nom de la chaîne dans le cartouche (vide = pas de cartouche).</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Lignes de générique : « Rôle : Nom ».</summary>
    public List<string> Credits { get; set; } = [];

    /// <summary>Son (fichier dans le dossier « sources » de l'atelier). Vide = silence + pulsation.</summary>
    public string AudioFile { get; set; } = string.Empty;

    public double AudioStart { get; set; }

    /// <summary>Durée en secondes (sans son, ou pour couper le son plus tôt).</summary>
    public double Duration { get; set; } = 6;

    public AtelierStyle Style { get; set; } = new();
}

public sealed class TrailerExtract
{
    public int MediaItemId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public double Start { get; set; }
    public double Length { get; set; } = 4;
    public double ItemDuration { get; set; }
}

public sealed class TrailerSpec
{
    public string Title { get; set; } = "TITRE";

    /// <summary>Ligne de diffusion : « 20H40 LUNDI 20 », « CE SOIR 21H00 »…</summary>
    public string DateLine { get; set; } = string.Empty;

    /// <summary>Étiquette de l'ouverture (« À L'AFFICHE », « PROCHAINEMENT »…).</summary>
    public string Label { get; set; } = "À L'AFFICHE";

    public string ChannelName { get; set; } = string.Empty;
    public List<TrailerExtract> Extracts { get; set; } = [];

    /// <summary>Musique (fichier source). Vide = son des extraits.</summary>
    public string MusicFile { get; set; } = string.Empty;

    /// <summary>Avec une musique : garder aussi le son des extraits, en fond.</summary>
    public bool KeepOriginalAudio { get; set; }

    public AtelierStyle Style { get; set; } = new();
}

public enum AtelierJobState
{
    Running,
    Done,
    Failed
}

public sealed class AtelierJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; init; } = string.Empty;
    public AtelierJobState State { get; set; } = AtelierJobState.Running;
    public double Progress { get; set; }
    public string Step { get; set; } = "Préparation…";
    public string Output { get; set; }
    public string Error { get; set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record AtelierFile(string Name, string Path, ImportRole Role, long Size, DateTime Modified);

public sealed record AtelierSource(string Name, string Path, long Size);

public sealed record AtelierItem(int MediaItemId, string Title, string Detail, string Path, double Duration);
