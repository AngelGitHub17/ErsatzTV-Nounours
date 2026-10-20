using ErsatzTV.Application.Nounours;

namespace ErsatzTV.Nounours.Modules.Statistiques;

public static class StatistiquesCalcul
{
    public static readonly (GuideEntryKind Kind, string Label, string Color)[] Kinds =
    [
        (GuideEntryKind.Movie, "Films", "#4f8cff"),
        (GuideEntryKind.Episode, "Épisodes", "#22c55e"),
        (GuideEntryKind.MusicVideo, "Clips", "#e879f9"),
        (GuideEntryKind.OtherVideo, "Autres vidéos", "#f5b83d"),
        (GuideEntryKind.Song, "Musique", "#38bdf8"),
        (GuideEntryKind.Gap, "Hors antenne", "#6b7280")
    ];

    public static Dictionary<GuideEntryKind, double> HoursByKind(IEnumerable<GuideEntry> entries, DateTimeOffset start, DateTimeOffset finish) =>
        entries
            .GroupBy(e => Kinds.Any(k => k.Kind == e.Kind) ? e.Kind : GuideEntryKind.Other)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(e => (Min(e.Finish, finish) - Max(e.Start, start)).TotalHours));

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
