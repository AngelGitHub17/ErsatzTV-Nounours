using System.Globalization;
using System.Text;
using System.Text.Json;
using ErsatzTV.Core;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Nounours;

/// <summary>Une ligne du journal : ce qui a commencé à l'antenne, sur quelle chaîne, à quelle heure.</summary>
public sealed record JournalLine(DateTimeOffset Start, DateTimeOffset Finish, int ChannelId, string Channel, string Title, string Kind);

/// <summary>Un total des statistiques : nom, temps d'antenne, nombre de passages.</summary>
public sealed record JournalTotal(string Name, TimeSpan Time, int Count);

/// <summary>Statistiques d'une période du journal.</summary>
public sealed record JournalStats(
    int Days,
    TimeSpan Total,
    List<JournalTotal> Kinds,
    List<(string Channel, TimeSpan Total, List<JournalTotal> Kinds)> Channels,
    List<JournalTotal> Programmes,
    List<JournalTotal> Repeats)
{
    /// <summary>
    ///     Regroupe les épisodes par série (« Série - s01e03 - … » → « Série »), compte le temps d'antenne
    ///     par genre, par chaîne et par programme, et repère ce qui repasse le plus (hors intercalaires).
    /// </summary>
    public static JournalStats From(IReadOnlyCollection<JournalLine> lines, int days)
    {
        static TimeSpan Length(JournalLine l) => l.Finish > l.Start ? l.Finish - l.Start : TimeSpan.Zero;
        static TimeSpan Sum(IEnumerable<JournalLine> ls) => ls.Aggregate(TimeSpan.Zero, (t, l) => t + Length(l));
        static List<JournalTotal> Totals(IEnumerable<IGrouping<string, JournalLine>> groups) =>
            groups.Select(g => new JournalTotal(g.Key, Sum(g), g.Count())).OrderByDescending(t => t.Time).ToList();

        var programmes = lines.Where(l => l.Kind is not "Intercalaire").ToList();
        return new JournalStats(
            days,
            Sum(lines),
            Totals(lines.GroupBy(l => l.Kind)),
            lines.GroupBy(l => l.Channel)
                .Select(g => (g.Key, Sum(g), Totals(g.GroupBy(l => l.Kind))))
                .OrderByDescending(c => c.Item2)
                .ToList(),
            Totals(programmes.GroupBy(l => Programme(l.Title))).Take(15).ToList(),
            programmes.GroupBy(l => l.Title)
                .Select(g => new JournalTotal(g.Key, Sum(g), g.Count()))
                .Where(t => t.Count >= Math.Max(3, days * 2))
                .OrderByDescending(t => t.Count)
                .Take(10)
                .ToList());
    }

    public static string Programme(string title)
    {
        System.Text.RegularExpressions.Match episode = System.Text.RegularExpressions.Regex.Match(
            title ?? string.Empty, @"^(.*?)\s+-\s+s\d+e\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return episode.Success ? episode.Groups[1].Value.Trim() : (title ?? string.Empty).Trim();
    }
}

/// <summary>
///     Journal de diffusion : chaque minute, tout ce qui a commencé à l'antenne depuis la minute précédente
///     (programmes, intercalaires, prises d'antenne de la régie) est noté dans <c>journal/AAAA-MM-JJ.jsonl</c>.
/// </summary>
public interface IJournalService
{
    Task Record(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken);
    List<JournalLine> Read(DateOnly day);
    List<DateOnly> Days();
    string Csv(IEnumerable<JournalLine> lines);
}

public sealed class JournalService(IGuideService guideService, IDbContextFactory<TvContext> dbContextFactory) : IJournalService
{
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Json = new();

    public static string Folder => Path.Combine(FileSystemLayout.AppDataFolder, "journal");

    private static string FileFor(DateOnly day) => Path.Combine(Folder, $"{day:yyyy-MM-dd}.jsonl");

    public async Task Record(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        DateTimeOffset from = since;
        DateTimeOffset to = until;
        if (to <= from)
        {
            return;
        }

        List<GuideEntry> entries = await guideService.GetEntries(from.AddHours(-6), to, [], includeFiller: true, cancellationToken);
        var started = entries.Where(e => e.Kind != GuideEntryKind.Gap && e.Start > from && e.Start <= to).OrderBy(e => e.Start).ToList();
        if (started.Count == 0)
        {
            return;
        }

        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Dictionary<int, string> names = await db.Channels.AsNoTracking().ToDictionaryAsync(c => c.Id, c => $"{c.Number} · {c.Name}", cancellationToken);
        Directory.CreateDirectory(Folder);
        lock (Lock)
        {
            foreach (IGrouping<DateOnly, GuideEntry> day in started.GroupBy(e => DateOnly.FromDateTime(e.Start.ToLocalTime().DateTime)))
            {
                var text = new StringBuilder();
                foreach (GuideEntry e in day)
                {
                    string kind = e.IsFiller ? "Intercalaire" : e.Kind switch
                    {
                        GuideEntryKind.Movie => "Film",
                        GuideEntryKind.Episode => "Épisode",
                        GuideEntryKind.RemoteStream => "Direct",
                        GuideEntryKind.MusicVideo => "Clip",
                        _ => "Vidéo"
                    };
                    var line = new JournalLine(e.Start.ToLocalTime(), e.Finish.ToLocalTime(), e.ChannelId, names.GetValueOrDefault(e.ChannelId) ?? $"Chaîne {e.ChannelId}",
                        // le vrai titre (pas le nom du rendez-vous « Le film du soir ») : statistiques et sagas s'y retrouvent
                        string.IsNullOrWhiteSpace(e.Title) ? e.DisplayTitle : e.Title, kind);
                    text.AppendLine(JsonSerializer.Serialize(line, Json));
                }

                File.AppendAllText(FileFor(day.Key), text.ToString(), new UTF8Encoding(false));
            }
        }
    }

    public List<JournalLine> Read(DateOnly day)
    {
        string file = FileFor(day);
        if (!File.Exists(file))
        {
            return [];
        }

        lock (Lock)
        {
            return File.ReadAllLines(file)
                .Where(l => l.Length > 2)
                .Select(l =>
                {
                    try
                    {
                        return JsonSerializer.Deserialize<JournalLine>(l, Json);
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                })
                .Where(l => l is not null)
                .DistinctBy(l => (l.ChannelId, l.Start))
                .OrderBy(l => l.Start)
                .ToList();
        }
    }

    public List<DateOnly> Days() =>
        Directory.Exists(Folder)
            ? Directory.GetFiles(Folder, "*.jsonl")
                .Select(f => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d) ? d : (DateOnly?)null)
                .Where(d => d is not null)
                .Select(d => d!.Value)
                .OrderDescending()
                .ToList()
            : [];

    public string Csv(IEnumerable<JournalLine> lines)
    {
        var sb = new StringBuilder("Date;Début;Fin;Durée;Chaîne;Type;Titre\r\n");
        foreach (JournalLine l in lines)
        {
            TimeSpan d = l.Finish - l.Start;
            sb.Append(CultureInfo.InvariantCulture, $"{l.Start:dd/MM/yyyy};{l.Start:HH:mm:ss};{l.Finish:HH:mm:ss};{(int)d.TotalMinutes}:{d.Seconds:00};{Cell(l.Channel)};{l.Kind};{Cell(l.Title)}\r\n");
        }

        return sb.ToString();
    }

    private static string Cell(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
