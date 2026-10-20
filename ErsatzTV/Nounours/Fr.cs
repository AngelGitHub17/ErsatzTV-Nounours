namespace ErsatzTV.Nounours;

/// <summary>Libellés français des valeurs techniques (énumérations) affichées dans les tableaux.</summary>
public static class Fr
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        // modes de diffusion
        ["Flood"] = "En continu", ["One"] = "Un seul", ["Multiple"] = "Plusieurs", ["Duration"] = "Durée",
        // début
        ["Dynamic"] = "Dynamique", ["Fixed"] = "Fixe",
        // ordres de lecture
        ["Chronological"] = "Chronologique", ["Random"] = "Aléatoire", ["Shuffle"] = "Mélangé", ["ShuffleInOrder"] = "Mélangé dans l'ordre",
        ["MultiEpisodeShuffle"] = "Mélange multi-épisodes", ["SeasonEpisode"] = "Saison, épisode", ["RandomRotation"] = "Rotation aléatoire",
        ["Marathon"] = "Marathon", ["None"] = "Aucun",
        // types de playout
        ["Classic"] = "Classique", ["Block"] = "Blocs", ["Sequential"] = "Séquentiel", ["Scripted"] = "Scripté", ["ExternalJson"] = "JSON externe",
        // bibliothèques / médias
        ["Local"] = "Local", ["Movies"] = "Films", ["Shows"] = "Séries", ["MusicVideos"] = "Clips", ["OtherVideos"] = "Autres vidéos",
        ["Songs"] = "Morceaux", ["Images"] = "Images", ["RemoteStreams"] = "Flux distants",
        // filigranes
        ["Permanent"] = "Permanent", ["Intermittent"] = "Intermittent", ["OpacityExpression"] = "Expression d'opacité",
        // éléments
        ["Collection"] = "Collection", ["TelevisionShow"] = "Série", ["TelevisionSeason"] = "Saison", ["Artist"] = "Artiste",
        ["MultiCollection"] = "Multi-collection", ["SmartCollection"] = "Collection intelligente", ["Playlist"] = "Playlist",
        ["Movie"] = "Film", ["Episode"] = "Épisode", ["MusicVideo"] = "Clip", ["OtherVideo"] = "Autre vidéo", ["Song"] = "Morceau",
        ["Image"] = "Image", ["RemoteStream"] = "Flux distant", ["SearchQuery"] = "Recherche"
    };

    public static string Label(object value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        string key = value.ToString() ?? string.Empty;
        return Labels.TryGetValue(key, out string label) ? label : key;
    }
}
