using System.Text.Json;
using ErsatzTV.Core;

namespace ErsatzTV.Application.Nounours;

/// <summary>Réglages des services de l'édition Nounours (fichier <c>nounours-reglages.json</c> du dossier de config).</summary>
public sealed class NounoursSettings
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Adresse d'ErsatzTV vue depuis l'extérieur (logos et liens de flux du guide publié), ex. http://mon-ip:8409.</summary>
    public string PublicAddress { get; set; } = "http://localhost:8409";

    public VoiceSettings Voice { get; set; } = new();
    public SubtitleSettings Subtitles { get; set; } = new();
    public PublishSettings Publish { get; set; } = new();
    public TonightSettings Tonight { get; set; } = new();
    public WeeklySettings Weekly { get; set; } = new();
    public WeatherSettings Weather { get; set; } = new();
    public EssentialSettings Essential { get; set; } = new();
    public List<Appointment> Appointments { get; set; } = [];

    /// <summary>Diffusion automatique de l'interlude à heures fixes (en plus du remplissage de secours).</summary>
    public BroadcastSettings InterludeBroadcast { get; set; } = new();

    /// <summary>Derniers passages des diffusions automatiques (clé = service:heure:chaîne).</summary>
    public Dictionary<string, DateTimeOffset> BroadcastRuns { get; set; } = [];

    /// <summary>Source OBS pour les directs (Régie et Info).</summary>
    public ObsSettings Obs { get; set; } = new();

    /// <summary>Bandeaux de la chaîne d'info, modifiables en direct.</summary>
    public InfoSettings Info { get; set; } = new();

    /// <summary>Chaînes éphémères : identifiant de chaîne → date de suppression automatique.</summary>
    public Dictionary<int, DateTimeOffset> EphemeralChannels { get; set; } = [];

    public static string FilePath => Path.Combine(FileSystemLayout.AppDataFolder, "nounours-reglages.json");

    public static NounoursSettings Load()
    {
        lock (Sync)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    return JsonSerializer.Deserialize<NounoursSettings>(File.ReadAllText(FilePath), Options) ?? new NounoursSettings();
                }
            }
            catch (JsonException)
            {
                // fichier abîmé : valeurs par défaut
            }

            return new NounoursSettings();
        }
    }

    public static void Update(Action<NounoursSettings> change)
    {
        lock (Sync)
        {
            NounoursSettings settings = Load();
            change(settings);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
    }
}

/// <summary>Synthèse vocale par un serveur compatible OpenAI (/v1/audio/speech), ex. Chatterbox TTS API.</summary>
public sealed class VoiceSettings
{
    public string Url { get; set; } = "http://localhost:4123/v1/audio/speech";
    public string Model { get; set; } = "tts-1";
    public string VoiceName { get; set; } = "alloy";
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>Transcription par un serveur compatible OpenAI (/v1/audio/transcriptions), ex. faster-whisper-server.</summary>
public sealed class SubtitleSettings
{
    public string Url { get; set; } = "http://localhost:8000/v1/audio/transcriptions";
    public string Model { get; set; } = "Systran/faster-whisper-small";
    public string Language { get; set; } = "fr";
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>Envoi automatique du guide JSON vers un site (ex. un script PHP sur ntvbydidikaria.fr).</summary>
public sealed class PublishSettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public int IntervalMinutes { get; set; } = 30;
    public int Hours { get; set; } = 48;
    public DateTimeOffset? LastSent { get; set; }
    public string LastResult { get; set; } = string.Empty;
}

/// <summary>Carton « Ce soir » fabriqué chaque jour à partir du guide.</summary>
/// <summary>
///     Diffusion automatique d'une vidéo fabriquée par les services : aux heures choisies, sur les chaînes choisies,
///     juste avant le programme suivant (elle remplace la fin du programme en cours, souvent le générique) ou à l'heure exacte.
/// </summary>
public sealed class BroadcastSettings
{
    public bool Enabled { get; set; }

    /// <summary>Heures séparées par des virgules, ex. « 07:00, 12:55, 19:55 ».</summary>
    public string Times { get; set; } = "12:55, 19:55";

    public List<int> ChannelIds { get; set; } = [];
    public bool BeforeNext { get; set; } = true;
}

public sealed class TonightSettings
{
    public BroadcastSettings Broadcast { get; set; } = new() { Times = "19:55" };

    public bool Enabled { get; set; }

    /// <summary>Heure de fabrication, « HH:mm ».</summary>
    public string At { get; set; } = "17:00";

    /// <summary>Début de la soirée annoncée, « HH:mm ».</summary>
    public string From { get; set; } = "20:00";

    public string Until { get; set; } = "23:59";
    public List<int> ChannelIds { get; set; } = [];
    public string Title { get; set; } = "CE SOIR";
    public string Music { get; set; } = string.Empty;
    public string ChannelName { get; set; } = string.Empty;
    public double Seconds { get; set; } = 12;
    public DateOnly? LastMade { get; set; }
}

/// <summary>Bande-annonce « Cette semaine » : les films de la semaine, refaite chaque semaine au jour et à l'heure choisis.</summary>
public sealed class WeeklySettings
{
    public BroadcastSettings Broadcast { get; set; } = new() { Times = "13:55, 20:35" };

    public bool Enabled { get; set; }

    /// <summary>Jour de fabrication (lundi par défaut).</summary>
    public DayOfWeek Day { get; set; } = DayOfWeek.Monday;

    public string At { get; set; } = "08:00";
    public List<int> ChannelIds { get; set; } = [];
    public string Label { get; set; } = "CETTE SEMAINE";
    public int MaxFilms { get; set; } = 4;
    public string Music { get; set; } = string.Empty;
    public string ChannelName { get; set; } = string.Empty;
    public DateOnly? LastMade { get; set; }
    public string LastResult { get; set; }
}

/// <summary>Carte météo en quadrillage, refaite chaque jour (Open-Meteo, sans clé).</summary>
public sealed class WeatherSettings
{
    public BroadcastSettings Broadcast { get; set; } = new() { Times = "07:00, 12:55, 19:55" };

    public bool Enabled { get; set; }
    public string At { get; set; } = "06:00";

    /// <summary>Villes, une par ligne (jusqu'à 6).</summary>
    public string Cities { get; set; } = "Saintes\nLa Rochelle\nBordeaux\nParis";

    public string Title { get; set; } = "MÉTÉO";
    public string Music { get; set; } = string.Empty;
    public string ChannelName { get; set; } = string.Empty;
    public double Seconds { get; set; } = 12;
    public DateOnly? LastMade { get; set; }
    public string LastResult { get; set; }
}

/// <summary>« L'essentiel du jour » : les titres de l'info en quadrillage, lus par la voix off, refait aux heures choisies.</summary>
public sealed class EssentialSettings
{
    public BroadcastSettings Broadcast { get; set; } = new() { Times = "12:55, 19:25" };

    public bool Enabled { get; set; }

    /// <summary>Heures de fabrication, ex. « 12:30, 19:00 » (les titres du moment à chaque fois).</summary>
    public string At { get; set; } = "12:30, 19:00";

    public string Title { get; set; } = "L'ESSENTIEL";
    public int Items { get; set; } = 5;

    /// <summary>Durée d'un titre à l'écran quand il n'y a pas de voix off.</summary>
    public double Seconds { get; set; } = 6;

    /// <summary>Voix off qui lit les titres (réglages des serveurs plus bas sur la page).</summary>
    public Atelier.VoiceEngine Voice { get; set; } = Atelier.VoiceEngine.None;

    public string Music { get; set; } = string.Empty;
    public string ChannelName { get; set; } = string.Empty;
    public DateTimeOffset? LastMadeAt { get; set; }
    public string LastResult { get; set; }
}

/// <summary>Rendez-vous d'antenne : à l'heure dite, la régie passe une vidéo (ouverture, fermeture d'antenne…) puis rend la grille.</summary>
public sealed class Appointment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "Ouverture d'antenne";
    public string At { get; set; } = "06:00";

    /// <summary>Jours de la semaine (vide = tous les jours).</summary>
    public List<DayOfWeek> Days { get; set; } = [];

    public int PlayoutId { get; set; }
    public int MediaItemId { get; set; }
    public string MediaTitle { get; set; } = string.Empty;

    /// <summary>Un seul jour (insertion dans le conducteur) ; vide = récurrent.</summary>
    public DateOnly? Date { get; set; }

    /// <summary>Juste avant le programme suivant (finit pile au début du programme qui suit l'heure dite).</summary>
    public bool BeforeNext { get; set; }

    /// <summary>Durée de la vidéo (sert à « juste avant »).</summary>
    public double DurationSeconds { get; set; }

    /// <summary>« À la place » : la vidéo tourne en boucle pendant N minutes (0 = une fois).</summary>
    public int Minutes { get; set; }
    public DateTimeOffset? LastRun { get; set; }
    public string LastResult { get; set; }
}

/// <summary>
///     OBS en direct : OBS envoie son image en SRT (Réglages › Stream › Personnalisé, ou Sortie « Enregistrement personnalisé »)
///     et ErsatzTV la reprend comme un flux en direct.
/// </summary>
public sealed class ObsSettings
{
    /// <summary>Adresse du PC qui fait tourner OBS (127.0.0.1 si c'est le même PC).</summary>
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 9710;

    /// <summary>« srt » (recommandé) ou « udp ».</summary>
    public string Protocol { get; set; } = "srt";

    /// <summary>Latence SRT en millisecondes.</summary>
    public int LatencyMs { get; set; } = 300;

    /// <summary>Télécommande d'OBS (Outils › Paramètres du serveur WebSocket) : port et mot de passe.</summary>
    public int WebSocketPort { get; set; } = 4455;

    public string WebSocketPassword { get; set; } = string.Empty;
}

/// <summary>Chaîne d'info : titres qui défilent, flash, sujet en cours ; lus par l'habillage toutes les quelques secondes.</summary>
public sealed class InfoSettings
{
    public string Headlines { get; set; } = "Bienvenue sur la chaîne d'information\nToute l'actualité en continu";
    public string Flash { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public string RssUrl { get; set; } = string.Empty;
    public string ChannelName { get; set; } = "NOUNOURS.INFOS";
    public List<InfoSource> Sources { get; set; } = [];

    /// <summary>Crédit affiché à l'écran (obligatoire pour certaines sources, ex. « © Union européenne, 2026 — CC BY 4.0 »).</summary>
    public string Credit { get; set; } = string.Empty;

    /// <summary>Adresses lues automatiquement pour les titres : flux RSS / Atom, ou page texte (un titre par ligne). Une par ligne.</summary>
    public string Feeds { get; set; } = string.Empty;

    public int FeedItems { get; set; } = 6;

    /// <summary>Code de la télécommande sur téléphone (/telecommande) ; vide = télécommande fermée.</summary>
    public string RemotePin { get; set; } = string.Empty;

    /// <summary>Programme d'EBS dans le guide : dernière lecture.</summary>
    public bool EbsGuide { get; set; } = true;

    public DateTimeOffset? EbsGuideAt { get; set; }
    public string EbsGuideResult { get; set; }
    public int FeedMinutes { get; set; } = 10;
    public DateTimeOffset? FeedsAt { get; set; }
    public string FeedsResult { get; set; }

    /// <summary>Un nouveau flash déclenche le jingle « Flash spécial » sur ces chaînes.</summary>
    public bool FlashJingle { get; set; }

    public List<int> FlashChannelIds { get; set; } = [];

    /// <summary>Dernier flash mis à l'antenne (pour ne jouer le jingle qu'une fois par flash).</summary>
    public string LastFlash { get; set; } = string.Empty;
}

public sealed class InfoSource
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    /// <summary>Piste audio séparée (ex. la version française d'un flux multilingue) ; vide = celle du flux.</summary>
    public string AudioUrl { get; set; } = string.Empty;

    /// <summary>Crédit à afficher quand cette source passe (licence).</summary>
    public string Credit { get; set; } = string.Empty;
}
