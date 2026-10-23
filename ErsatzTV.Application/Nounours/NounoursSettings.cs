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
public sealed class TonightSettings
{
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
