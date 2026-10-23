using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ErsatzTV.Application.Nounours;

/// <summary>Guide JSON enrichi (site web, publication automatique), construit à partir du XMLTV d'ErsatzTV.</summary>
public static partial class EpgJson
{
    public static EpgDocument Build(string xml, string requestBase, string accessToken, double heures, string chaines, bool passes)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        DateTimeOffset until = now.AddHours(Math.Clamp(heures, 1, 24 * 14));
        System.Collections.Generic.HashSet<string> wanted = string.IsNullOrWhiteSpace(chaines)
            ? null
            : chaines.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

        string token = string.IsNullOrWhiteSpace(accessToken) ? string.Empty : $"?access_token={Uri.EscapeDataString(accessToken)}";

        // les fragments mis en cache commencent par un BOM : on les retire avant de lire le XML
        XDocument doc = XDocument.Parse(xml.Replace("\uFEFF", string.Empty, StringComparison.Ordinal));
        var channels = new List<EpgChannel>();
        var byId = new Dictionary<string, EpgChannel>();

        foreach (XElement element in doc.Root?.Elements("channel") ?? [])
        {
            var names = element.Elements("display-name").Select(e => e.Value).ToList();
            string number = names.Find(n => NumberRegex().IsMatch(n)) ?? names.FirstOrDefault() ?? "?";
            string name = names.LastOrDefault(n => n != number && !n.StartsWith(number + " ", StringComparison.Ordinal)) ?? names.LastOrDefault() ?? number;

            var channel = new EpgChannel
            {
                Id = (string)element.Attribute("id"),
                Numero = number,
                Nom = name,
                Logo = (string)element.Element("icon")?.Attribute("src"),
                Flux = new EpgStreams
                {
                    Hls = $"{requestBase}/iptv/channel/{Uri.EscapeDataString(number)}.m3u8{token}",
                    Ts = $"{requestBase}/iptv/channel/{Uri.EscapeDataString(number)}.ts{token}"
                }
            };

            if (wanted is null || wanted.Contains(number))
            {
                channels.Add(channel);
                byId[channel.Id] = channel;
            }
        }

        foreach (XElement element in doc.Root?.Elements("programme") ?? [])
        {
            if (!byId.TryGetValue((string)element.Attribute("channel") ?? string.Empty, out EpgChannel channel))
            {
                continue;
            }

            if (!TryParseXmltvTime((string)element.Attribute("start"), out DateTimeOffset start) ||
                !TryParseXmltvTime((string)element.Attribute("stop"), out DateTimeOffset stop))
            {
                continue;
            }

            if (stop <= now && !passes || start >= until)
            {
                continue;
            }

            string onscreen = element.Elements("episode-num")
                .FirstOrDefault(e => (string)e.Attribute("system") == "onscreen")?.Value;
            Match episode = EpisodeRegex().Match(onscreen ?? string.Empty);

            var programme = new EpgProgramme
            {
                Debut = start,
                Fin = stop,
                DureeMinutes = (int)Math.Round((stop - start).TotalMinutes),
                Titre = element.Element("title")?.Value,
                SousTitre = element.Element("sub-title")?.Value,
                Description = element.Element("desc")?.Value,
                Categories = element.Elements("category").Select(c => c.Value).Distinct().ToList(),
                Image = (string)element.Element("icon")?.Attribute("src"),
                Annee = element.Element("date")?.Value,
                Classification = element.Element("rating")?.Element("value")?.Value,
                Saison = episode.Success ? int.Parse(episode.Groups["s"].Value, CultureInfo.InvariantCulture) : null,
                Episode = episode.Success ? int.Parse(episode.Groups["e"].Value, CultureInfo.InvariantCulture) : null,
                Rediffusion = element.Element("previously-shown") is not null,
                EnCours = start <= now && stop > now
            };

            channel.Programmes.Add(programme);
        }

        foreach (EpgChannel channel in channels)
        {
            channel.Programmes = channel.Programmes.OrderBy(p => p.Debut).ToList();
            channel.EnCours = channel.Programmes.Find(p => p.EnCours);
            channel.ASuivre = channel.Programmes.Find(p => p.Debut >= now);
        }

        return new EpgDocument
        {
            GenereLe = now,
            Jusqua = until,
            Chaines = channels.OrderBy(c => decimal.TryParse(c.Numero, CultureInfo.InvariantCulture, out decimal n) ? n : decimal.MaxValue).ToList()
        };
    }

    // XMLTV : « 20261020180000 +0200 »
    private static bool TryParseXmltvTime(string value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length < 14)
        {
            return false;
        }

        if (!DateTime.TryParseExact(value[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
        {
            return false;
        }

        TimeSpan offset = TimeSpan.Zero;
        string zone = value[14..].Trim();
        if (zone.Length == 5 && (zone[0] == '+' || zone[0] == '-') &&
            int.TryParse(zone.AsSpan(1, 2), CultureInfo.InvariantCulture, out int h) &&
            int.TryParse(zone.AsSpan(3, 2), CultureInfo.InvariantCulture, out int m))
        {
            offset = new TimeSpan(h, m, 0);
            if (zone[0] == '-')
            {
                offset = -offset;
            }
        }

        result = new DateTimeOffset(local, offset);
        return true;
    }

    [GeneratedRegex(@"^\d+(\.\d+)?$")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"S(?<s>\d+)E(?<e>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeRegex();

    public sealed class EpgDocument
    {
        public DateTimeOffset GenereLe { get; set; }
        public DateTimeOffset Jusqua { get; set; }
        public List<EpgChannel> Chaines { get; set; } = [];
    }

    public sealed class EpgChannel
    {
        public string Id { get; set; }
        public string Numero { get; set; }
        public string Nom { get; set; }
        public string Logo { get; set; }
        public EpgStreams Flux { get; set; }
        public EpgProgramme EnCours { get; set; }
        public EpgProgramme ASuivre { get; set; }
        public List<EpgProgramme> Programmes { get; set; } = [];
    }

    public sealed class EpgStreams
    {
        public string Hls { get; set; }
        public string Ts { get; set; }
    }

    public sealed class EpgProgramme
    {
        public DateTimeOffset Debut { get; set; }
        public DateTimeOffset Fin { get; set; }
        public int DureeMinutes { get; set; }
        public string Titre { get; set; }
        public string SousTitre { get; set; }
        public string Description { get; set; }
        public List<string> Categories { get; set; } = [];
        public string Image { get; set; }
        public string Annee { get; set; }
        public string Classification { get; set; }
        public int? Saison { get; set; }
        public int? Episode { get; set; }
        public bool Rediffusion { get; set; }
        public bool EnCours { get; set; }
    }
}
