using System.Text.Json;
using static ErsatzTV.Application.Nounours.Atelier.AtelierSynth;

namespace ErsatzTV.Application.Nounours.Atelier;

/// <summary>
///     Les sons de l'atelier rangés par ambiance, plus des sons « maison » fabriqués par le programme
///     (synthétisés ici, mélodies originales : aucun droit d'auteur) dans l'esprit des habillages télé des années 90 :
///     indicatifs courts qui finissent sur un coup (pour les jingles) et boucles (interlude, météo, cartons).
/// </summary>
public static class AtelierSounds
{
    public static readonly string[] Ambiances = ["Indicatif", "Énergique", "Calme", "Suspense", "Jeunesse", "Cinéma", "Pub"];

    public const string BuiltinPrefix = "integre-";

    /// <summary>Version des sons intégrés : en la changeant, les anciens fichiers sont remplacés.</summary>
    private const string Version = "v2-";

    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly Queue<string> Recent = new();

    private static string TagsFile => Path.Combine(AtelierService.SourcesFolder, "ambiances.json");

    // basses (16 doubles croches) : -99 = silence
    private const int X = -99;
    private static readonly int[] BassHouse = [0, X, 0, X, X, X, 0, X, 0, X, X, 7, X, 0, X, X];
    private static readonly int[] BassFunk = [0, X, X, 7, X, X, 0, X, X, 0, 4, X, 7, X, 5, X];
    private static readonly int[] BassRoot = [0, X, X, X, X, X, X, X, 0, X, X, X, X, X, X, X];
    private static readonly int[] BassPulse = [0, X, 0, X, 0, X, 0, X, 0, X, 0, X, 0, X, 0, X];
    private static readonly int[] BassNone = [X];

    /// <summary>Sons intégrés : fichier, nom affiché, ambiance, recette.</summary>
    private static readonly (string File, string Name, string Ambiance, Recipe Recipe)[] Builtins =
    [
        // ---------------- indicatifs (3 à 4 mesures + coup final), pour les jingles
        (BuiltinPrefix + Version + "carre-rouge.wav", "Carré rouge", "Indicatif",
            new Recipe(120, 57, false, Drums.House, Lead.Brass, [0, 3, 4], BassHouse,
                [(0, 4, 2), (3, 4, 1), (6, 5, 2), (10, 4, 2), (16, 7, 3), (22, 6, 2), (26, 4, 4)], Hit.Orchestra, 3, Riser: true, Seed: 11)),
        (BuiltinPrefix + Version + "grand-soir.wav", "Grand soir", "Indicatif",
            new Recipe(104, 53, false, Drums.NewJack, Lead.EPiano, [0, 5, 3, 4], BassFunk,
                [(0, 2, 3), (4, 4, 2), (8, 6, 4), (16, 7, 2), (20, 6, 2), (24, 4, 6)], Hit.Brass, 4, Swing: 0.32, Seed: 12)),
        (BuiltinPrefix + Version + "eclair.wav", "Éclair", "Indicatif",
            new Recipe(128, 52, true, Drums.Break, Lead.Bell, [0, 5, 6], BassFunk,
                [(0, 0, 1), (2, 2, 1), (4, 4, 1), (6, 7, 2), (12, 6, 2), (16, 4, 1), (18, 2, 1), (20, 4, 2), (24, 9, 4)], Hit.Orchestra, 3, Riser: true, Seed: 13)),
        (BuiltinPrefix + Version + "rideau.wav", "Rideau", "Indicatif",
            new Recipe(96, 50, true, Drums.Break, Lead.Brass, [0, 6, 5, 4], BassRoot,
                [(0, 4, 4), (6, 5, 2), (8, 4, 4), (16, 2, 4), (22, 3, 2), (24, 4, 6)], Hit.Timpani, 3, Riser: true, Seed: 14)),
        (BuiltinPrefix + Version + "neon.wav", "Néon", "Indicatif",
            new Recipe(112, 55, true, Drums.NewJack, Lead.Square, [0, 3, 5, 4], BassFunk,
                [(0, 7, 2), (3, 6, 1), (4, 4, 2), (8, 2, 2), (11, 4, 3), (16, 7, 2), (19, 9, 1), (20, 7, 4)], Hit.Brass, 3, Swing: 0.3, Seed: 15)),
        (BuiltinPrefix + Version + "petit-ecran.wav", "Petit écran", "Jeunesse",
            new Recipe(132, 60, false, Drums.House, Lead.Bell, [0, 4, 5, 3], BassPulse,
                [(0, 0, 1), (2, 4, 1), (4, 7, 1), (6, 4, 1), (8, 9, 2), (12, 7, 2), (16, 5, 1), (18, 7, 1), (20, 9, 1), (22, 11, 3)], Hit.Bell, 3, Seed: 16)),
        (BuiltinPrefix + Version + "flash-pub.wav", "Flash pub", "Pub",
            new Recipe(140, 62, false, Drums.House, Lead.Square, [0, 4, 5, 4], BassPulse,
                [(0, 4, 1), (1, 4, 1), (2, 5, 2), (4, 7, 2), (8, 9, 1), (10, 7, 2), (16, 11, 2), (20, 9, 2), (24, 14, 4)], Hit.Brass, 2, Seed: 17)),
        (BuiltinPrefix + Version + "tension.wav", "Tension", "Suspense",
            new Recipe(92, 45, true, Drums.Heart, Lead.Pad, [0, 0, 5, 4], BassRoot,
                [(0, 7, 4), (8, 8, 4), (16, 7, 4), (24, 6, 6)], Hit.Timpani, 3, Riser: true, Seed: 18)),

        // ---------------- boucles (mesures entières, se raccordent), pour l'interlude, la météo, les cartons
        (BuiltinPrefix + Version + "boucle-95.wav", "Boucle 95", "Énergique",
            new Recipe(122, 57, false, Drums.House, Lead.Brass, [0, 3, 4, 3], BassHouse,
                [(0, 4, 2), (6, 5, 2), (10, 4, 2), (16, 2, 2), (22, 4, 4)], Hit.None, 8, Loop: true, Seed: 21)),
        (BuiltinPrefix + Version + "salon-jazz.wav", "Salon jazz-funk", "Calme",
            new Recipe(94, 53, false, Drums.Break, Lead.EPiano, [0, 5, 1, 4], BassFunk,
                [(0, 4, 3), (6, 6, 2), (10, 4, 3), (16, 2, 3), (22, 3, 2), (26, 1, 4)], Hit.None, 8, Swing: 0.25, Loop: true, Seed: 22)),
        (BuiltinPrefix + Version + "meteo-douce.wav", "Météo douce", "Calme",
            new Recipe(86, 58, false, Drums.None, Lead.Pad, [0, 4, 5, 3], BassRoot,
                [(0, 7, 4), (8, 9, 4), (16, 11, 4), (24, 9, 8)], Hit.None, 8, Loop: true, Seed: 23)),
        (BuiltinPrefix + Version + "ecran-large.wav", "Écran large", "Cinéma",
            new Recipe(84, 50, true, Drums.Break, Lead.Brass, [0, 5, 6, 4], BassRoot,
                [(0, 4, 6), (8, 5, 4), (16, 7, 6), (24, 6, 8)], Hit.None, 8, Loop: true, Seed: 24))
    ];

    /// <summary>Nom lisible d'un son (« Carré rouge · intégré » pour les sons maison).</summary>
    public static string DisplayName(string file)
    {
        foreach ((string f, string name, _, _) in Builtins)
        {
            if (string.Equals(f, file, StringComparison.OrdinalIgnoreCase))
            {
                return $"{name} · intégré";
            }
        }

        return file;
    }

    public static bool IsBuiltin(string file) => Builtins.Any(b => string.Equals(b.File, file, StringComparison.OrdinalIgnoreCase));

    /// <summary>Fabrique les sons intégrés manquants (une seule fois) et retire ceux d'une ancienne version.</summary>
    public static void EnsureBuiltins()
    {
        lock (Lock)
        {
            Directory.CreateDirectory(AtelierService.SourcesFolder);
            Dictionary<string, string> tags = LoadTags();
            var changed = false;

            // anciens sons intégrés (version précédente) : remplacés
            foreach (string old in Directory.GetFiles(AtelierService.SourcesFolder, BuiltinPrefix + "*.wav"))
            {
                string name = Path.GetFileName(old);
                if (!IsBuiltin(name))
                {
                    File.Delete(old);
                    changed |= tags.Remove(name);
                }
            }

            foreach ((string file, _, string ambiance, Recipe recipe) in Builtins)
            {
                string path = Path.Combine(AtelierService.SourcesFolder, file);
                if (!File.Exists(path))
                {
                    (float[] left, float[] right) = Render(recipe);
                    WriteWav(path, left, right);
                }

                if (!tags.ContainsKey(file))
                {
                    tags[file] = ambiance;
                    changed = true;
                }
            }

            if (changed)
            {
                SaveTags(tags);
            }
        }
    }

    public static string AmbianceOf(string file)
    {
        lock (Lock)
        {
            return file is not null && LoadTags().TryGetValue(file, out string a) ? a : string.Empty;
        }
    }

    public static Dictionary<string, string> AllTags()
    {
        lock (Lock)
        {
            return LoadTags();
        }
    }

    public static void SetAmbiance(string file, string ambiance)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        lock (Lock)
        {
            Dictionary<string, string> tags = LoadTags();
            if (string.IsNullOrWhiteSpace(ambiance))
            {
                tags.Remove(file);
            }
            else
            {
                tags[file] = ambiance;
            }

            SaveTags(tags);
        }
    }

    /// <summary>Un son de l'ambiance, tiré au hasard en évitant les derniers utilisés (vos sons passent avant les sons intégrés).</summary>
    public static string FirstOf(string ambiance) => Pick(ambiance, 1).FirstOrDefault() ?? string.Empty;

    /// <summary>
    ///     <paramref name="count" /> sons de l'ambiance, tous différents tant qu'il y en a assez,
    ///     en évitant ceux qui viennent de servir.
    /// </summary>
    public static List<string> Pick(string ambiance, int count, params string[] fallbacks)
    {
        EnsureBuiltins();
        Dictionary<string, string> tags = AllTags();
        List<string> names = AtelierService.Sources().Select(s => s.Name).ToList();
        List<string> candidates = names.Where(n => tags.GetValueOrDefault(n) == ambiance).ToList();
        foreach (string fallback in fallbacks)
        {
            if (candidates.Count >= count)
            {
                break;
            }

            candidates.AddRange(names.Where(n => tags.GetValueOrDefault(n) == fallback && !candidates.Contains(n)));
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        lock (Lock)
        {
            // les sons de l'utilisateur d'abord, puis le hasard, les plus récemment utilisés en dernier
            List<string> ordered = candidates
                .OrderBy(n => Recent.Contains(n))
                .ThenBy(n => IsBuiltin(n))
                .ThenBy(_ => Random.Shared.Next())
                .ToList();
            var result = new List<string>();
            for (var i = 0; i < count; i++)
            {
                result.Add(ordered[i % ordered.Count]);
            }

            foreach (string used in result.Distinct())
            {
                Recent.Enqueue(used);
                while (Recent.Count > Math.Max(2, candidates.Count - 1))
                {
                    Recent.Dequeue();
                }
            }

            return result;
        }
    }

    private static Dictionary<string, string> LoadTags()
    {
        try
        {
            if (File.Exists(TagsFile))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TagsFile)) ?? [];
            }
        }
        catch (Exception)
        {
            // fichier abîmé : on repart de zéro
        }

        return [];
    }

    private static void SaveTags(Dictionary<string, string> tags)
    {
        Directory.CreateDirectory(AtelierService.SourcesFolder);
        File.WriteAllText(TagsFile, JsonSerializer.Serialize(tags, Indented));
    }
}

/// <summary>Signalétique jeunesse : niveau (-10, -12, -16, -18) d'après la classification d'un programme, et le carton qui va avec.</summary>
public static class Signaletique
{
    /// <summary>0 = tous publics / non classé ; sinon 10, 12, 16 ou 18. Mêmes règles que le rond de l'habillage.</summary>
    public static int Level(string rating)
    {
        string r = (rating ?? string.Empty).Trim().ToLowerInvariant();
        if (r.Length == 0)
        {
            return 0;
        }

        if (r.Contains("18", StringComparison.Ordinal) || r == "nc-17")
        {
            return 18;
        }

        if (r.Contains("16", StringComparison.Ordinal) || r is "tv-ma" or "r")
        {
            return 16;
        }

        if (r.Contains("12", StringComparison.Ordinal) || r.Contains("13", StringComparison.Ordinal) || r.Contains("14", StringComparison.Ordinal))
        {
            return 12;
        }

        if (r.Contains("10", StringComparison.Ordinal) || r is "tv-pg" or "pg")
        {
            return 10;
        }

        return 0;
    }

    /// <summary>« SÉRIE DÉCONSEILLÉE AUX MOINS DE 12 ANS », « FILM INTERDIT AUX MOINS DE 18 ANS »… (vide si tous publics).</summary>
    public static string Message(int level, string label)
    {
        if (level <= 0)
        {
            return string.Empty;
        }

        string word = string.IsNullOrWhiteSpace(label) ? "PROGRAMME" : label.Trim().ToUpperInvariant();
        bool feminine = word is "SÉRIE" or "ÉMISSION" or "SÉRIE ANIMÉE" or "FICTION" or "TÉLÉRÉALITÉ";
        if (level >= 18)
        {
            return $"{word} INTERDIT{(feminine ? "E" : "")} AUX MOINS DE 18 ANS";
        }

        return $"{word} DÉCONSEILLÉ{(feminine ? "E" : "")} AUX MOINS DE {level} ANS";
    }
}
