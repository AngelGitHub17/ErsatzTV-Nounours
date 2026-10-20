using System.Globalization;
using System.Text.Json;
using ErsatzTV.Core;

namespace ErsatzTV.Nounours.Audience;

/// <summary>Audience d'une chaîne pour une journée (heure locale du serveur).</summary>
public sealed class AudienceChannelDay
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Minutes-spectateurs par heure (24 cases). 60 = un spectateur pendant toute l'heure.</summary>
    public double[] ViewerMinutes { get; set; } = new double[24];

    /// <summary>Pic de spectateurs simultanés par heure.</summary>
    public int[] Peak { get; set; } = new int[24];

    /// <summary>Minutes-spectateurs par programme (titre affiché dans le guide).</summary>
    public Dictionary<string, double> Programmes { get; set; } = [];

    /// <summary>Empreintes anonymes des spectateurs distincts de la journée.</summary>
    public System.Collections.Generic.HashSet<string> Viewers { get; set; } = [];

    public double TotalMinutes => ViewerMinutes.Sum();
    public int MaxPeak => Peak.Max();
}

public sealed class AudienceDay
{
    public string Date { get; set; } = string.Empty;

    /// <summary>Par numéro de chaîne.</summary>
    public Dictionary<string, AudienceChannelDay> Channels { get; set; } = [];

    public DateOnly DateValue => DateOnly.ParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Fichiers d'audience : un JSON par jour dans <c>audience/</c>.</summary>
public static class AudienceStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string PathFor(DateOnly date) =>
        Path.Combine(FileSystemLayout.AudienceFolder, $"{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json");

    public static AudienceDay Load(DateOnly date)
    {
        string path = PathFor(date);
        try
        {
            if (File.Exists(path))
            {
                AudienceDay day = JsonSerializer.Deserialize<AudienceDay>(File.ReadAllText(path), Options);
                if (day is not null)
                {
                    foreach (AudienceChannelDay channel in day.Channels.Values)
                    {
                        // fichiers anciens / abîmés : on remet des tableaux de 24 cases
                        if (channel.ViewerMinutes is not { Length: 24 })
                        {
                            channel.ViewerMinutes = new double[24];
                        }

                        if (channel.Peak is not { Length: 24 })
                        {
                            channel.Peak = new int[24];
                        }
                    }

                    return day;
                }
            }
        }
        catch (Exception)
        {
            // fichier illisible : on repart de zéro pour ce jour
        }

        return new AudienceDay { Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
    }

    public static void Save(AudienceDay day)
    {
        Directory.CreateDirectory(FileSystemLayout.AudienceFolder);
        string path = PathFor(day.DateValue);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(day, Options));
        File.Move(temp, path, true);
    }

    /// <summary>Jours disponibles, du plus récent au plus ancien.</summary>
    public static List<DateOnly> AvailableDays()
    {
        if (!Directory.Exists(FileSystemLayout.AudienceFolder))
        {
            return [];
        }

        return Directory.GetFiles(FileSystemLayout.AudienceFolder, "*.json")
            .Select(f => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d) ? d : (DateOnly?)null)
            .Where(d => d.HasValue)
            .Select(d => d.Value)
            .OrderDescending()
            .ToList();
    }
}
