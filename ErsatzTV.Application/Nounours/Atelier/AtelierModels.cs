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
    Annonce,

    /// <summary>Carton seul sur fond noir (signalétique « -12 », « À SUIVRE »…) : le titre est le texte affiché.</summary>
    Carton
}

/// <summary>Le style « années 90 » : fond, encre, palette des rectangles.</summary>
public sealed class AtelierStyle
{
    /// <summary>Couleur du cadre autour de l'image.</summary>
    public string FrameColor { get; set; } = "#FFFFED";

    /// <summary>« grille » : quadrillage de cases pleines qui changent à chaque temps fort (le look des années 90) ; « libre » : rectangles flottants.</summary>
    public string Layout { get; set; } = "grille";

    /// <summary>« 16/9 » ou « 4/3 » (image 4/3 au centre, bandes noires sur les côtés, comme à l'époque).</summary>
    public string Format { get; set; } = "16/9";

    /// <summary>Encre des bandeaux (texte en blanc dessus).</summary>
    public string Ink { get; set; } = "#000000";

    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>Couleurs des rectangles qui dansent.</summary>
    /// <summary>En grille : 1re couleur = couleur vive principale, 2e = seconde couleur, 3e = gris ; noir et blanc sont toujours là.</summary>
    public List<string> Palette { get; set; } = ["#FF4000", "#C00000", "#43433B", "#FFFFFF", "#111111"];

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

    /// <summary>Carton final sur fond noir (ex. « SÉRIE DÉCONSEILLÉE AUX MOINS DE 16 ANS »), vide = aucun.</summary>
    public string EndMessage { get; set; } = string.Empty;

    public double EndSeconds { get; set; } = 2.5;

    /// <summary>Voix off : texte lu par une voix de synthèse, ou fichier son enregistré.</summary>
    public VoiceOver Voice { get; set; } = new();

    public AtelierStyle Style { get; set; } = new();

    /// <summary>Nom de fichier fixe (sans dossier) : la vidéo est remplacée à chaque fabrication. Vide = nom daté.</summary>
    public string OutputName { get; set; } = string.Empty;
}

/// <summary>Habillage complet en un clic : tous les jingles, les cartons pub et la signalétique d'une chaîne.</summary>
public sealed class PackSpec
{
    public string ChannelName { get; set; } = "Ma chaîne";

    /// <summary>Étiquettes des jingles avant programme (FILM, SÉRIE…).</summary>
    public List<string> Labels { get; set; } = ["FILM", "SÉRIE", "CINÉMA", "JEUNESSE"];

    public bool Pubs { get; set; } = true;

    /// <summary>Cartons de signalétique (-10, -12, -16, -18) pour les films et les séries.</summary>
    public bool Ratings { get; set; } = true;

    /// <summary>Musique des jingles (vide = une boucle intégrée « Énergique »).</summary>
    public string Music { get; set; } = string.Empty;

    /// <summary>Musique des cartons pub (vide = boucle intégrée « Pub »).</summary>
    public string PubMusic { get; set; } = string.Empty;

    /// <summary>Durée de chaque jingle en secondes.</summary>
    public double Seconds { get; set; } = 6;

    public AtelierStyle Style { get; set; } = new();
}

public enum VoiceEngine
{
    /// <summary>Pas de voix off.</summary>
    None,

    /// <summary>Voix de Windows (hors ligne, ex. « Microsoft Hortense »).</summary>
    Windows,

    /// <summary>Serveur de synthèse compatible OpenAI (/v1/audio/speech), ex. Chatterbox TTS.</summary>
    Server,

    /// <summary>Un fichier son déjà enregistré (dans les sons de l'atelier).</summary>
    File
}

public sealed class VoiceOver
{
    public VoiceEngine Engine { get; set; } = VoiceEngine.None;
    public string Text { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;

    /// <summary>Début de la voix dans la vidéo, en secondes.</summary>
    public double Start { get; set; } = 0.8;

    /// <summary>Baisser la musique pendant que la voix parle.</summary>
    public bool Duck { get; set; } = true;
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

/// <param name="Kind">« Film », « Série » ou « Vidéo ».</param>
/// <param name="Rating">Classification (champ « classification » de la médiathèque), peut être vide.</param>
public sealed record AtelierItem(int MediaItemId, string Title, string Detail, string Path, double Duration, string Kind = "", string Rating = "");
