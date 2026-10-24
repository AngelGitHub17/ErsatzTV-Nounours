namespace ErsatzTV.Core.Domain;

public class Tag
{
    public static readonly string PlexNetworkTypeId = "319";
    public static readonly string NfoCountryTypeId = "nfo/country";

    /// <summary>Nounours : la saga d'un film (&lt;set&gt; des fichiers .nfo de Kodi / Jellyfin), ex. « Harry Potter ».</summary>
    public static readonly string NfoSetTypeId = "nfo/set";

    public int Id { get; set; }
    public string Name { get; set; }
    public string ExternalCollectionId { get; set; }
    public string ExternalTypeId { get; set; }
}
