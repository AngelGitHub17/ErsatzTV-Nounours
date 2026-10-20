using ErsatzTV.Application.Nounours;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Shared.Nounours;

/// <summary>Une option du sélecteur de contenu (préréglage, collection, fichier précis, programme…).</summary>
public sealed record ContentChoice(
    ContentSourceKind Kind,
    int? Id,
    string Query,
    string Label,
    string Detail,
    CollectionType ItemType = CollectionType.OtherVideo)
{
    public static readonly ContentChoice[] Presets =
    [
        new(ContentSourceKind.Search, null, "type:movie", "Tous les films", "Préréglage"),
        new(ContentSourceKind.Search, null, "type:episode", "Tous les épisodes de séries", "Préréglage"),
        new(ContentSourceKind.Search, null, "type:music_video", "Tous les clips musicaux", "Préréglage"),
        new(ContentSourceKind.Search, null, "type:other_video", "Toutes les autres vidéos", "Préréglage"),
        new(ContentSourceKind.Search, null, string.Empty, "Recherche personnalisée…", "Collection intelligente créée à partir d'une recherche")
    ];

    public static ContentChoice From(ContentOption o) => new(o.Kind, o.Id, null, o.Name, o.Detail, o.ItemType);
}
