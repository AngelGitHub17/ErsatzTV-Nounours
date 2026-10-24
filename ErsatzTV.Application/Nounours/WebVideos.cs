using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Nounours;

/// <summary>Une vidéo trouvée sur YouTube (ou un autre site que yt-dlp sait lire).</summary>
public sealed record WebVideo(string Id, string Url, string Title, string Channel, double? Seconds, string Thumbnail);

/// <summary>Une vidéo déjà récupérée (registre gardé à côté des fichiers).</summary>
public sealed record WebVideoRecord(DateTimeOffset When, string Title, string Url, string File, string Destination);

public enum WebJobState
{
    Running,
    Done,
    Failed
}

public sealed class WebDownloadJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public int Total { get; set; }
    public int Done { get; set; }
    public string Current { get; set; } = string.Empty;
    public double Progress { get; set; }
    public WebJobState State { get; set; } = WebJobState.Running;
    public string Error { get; set; }
    public List<string> Failed { get; } = [];
    public List<string> Files { get; } = [];
}

/// <summary>
///     Vidéos du web (YouTube…) : chercher, choisir, garder les adresses, et télécharger la sélection dans la médiathèque
///     (pubs, jingles, clips…) avec <c>yt-dlp</c>, que la page sait installer. Un registre (adresse, titre, fichier) est
///     gardé dans chaque dossier : on sait toujours d'où vient une vidéo.
/// </summary>
public interface IWebVideoService
{
    /// <summary>Chemin et version de yt-dlp (null si absent).</summary>
    Task<(string Path, string Version)?> Tool(CancellationToken cancellationToken);

    Task<string> InstallTool(CancellationToken cancellationToken);

    /// <summary>Adresse d'une vidéo, d'une playlist ou d'une chaîne — ou des mots à chercher sur YouTube.</summary>
    Task<List<WebVideo>> Search(string input, int max, CancellationToken cancellationToken);

    WebDownloadJob Download(IReadOnlyList<WebVideo> videos, ImportRole role, int maxHeight);

    WebDownloadJob GetJob(string id);

    List<WebVideoRecord> History();

    string AddressList(IEnumerable<WebVideo> videos);
}

public sealed class WebVideoService(
    IFolderImportService import,
    IDbContextFactory<TvContext> dbContextFactory,
    ILogger<WebVideoService> logger) : IWebVideoService
{
    private const string RegisterFile = "adresses-videos-web.tsv";
    private static readonly ConcurrentDictionary<string, WebDownloadJob> Jobs = new();
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private static readonly Regex Percent = new(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);

    public static readonly ImportRole[] Destinations =
        [ImportRole.Pubs, ImportRole.Jingles, ImportRole.BandesAnnonces, ImportRole.Habillage, ImportRole.Clips, ImportRole.Videos, ImportRole.Films];

    private static string ToolsFolder => Path.Combine(FileSystemLayout.AppDataFolder, "outils");

    private static string LocalTool => Path.Combine(ToolsFolder, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp");

    public static string Folder(ImportRole role) => Path.Combine(FileSystemLayout.ImportsFolder, FolderImportService.Label(role));

    // ------------------------------------------------------------------ l'outil

    public async Task<(string Path, string Version)?> Tool(CancellationToken cancellationToken)
    {
        foreach (string candidate in new[] { LocalTool, "yt-dlp" })
        {
            if (candidate != "yt-dlp" && !File.Exists(candidate))
            {
                continue;
            }

            try
            {
                (int code, string output, _) = await Run(candidate, ["--version"], null, TimeSpan.FromSeconds(20), cancellationToken);
                if (code == 0)
                {
                    return (candidate, output.Trim());
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                // pas installé à cet endroit
            }
        }

        return null;
    }

    /// <summary>Télécharge la dernière version officielle de yt-dlp (GitHub) dans le dossier de configuration.</summary>
    public async Task<string> InstallTool(CancellationToken cancellationToken)
    {
        string asset = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "yt-dlp_macos" : "yt-dlp";
        Directory.CreateDirectory(ToolsFolder);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ErsatzTV-Nounours/1.0");
        byte[] bytes = await http.GetByteArrayAsync($"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{asset}", cancellationToken);
        string temp = LocalTool + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }

        File.Move(temp, LocalTool, true);
        (string Path, string Version)? tool = await Tool(cancellationToken);
        return tool is null ? "yt-dlp téléchargé, mais il ne démarre pas (sous Linux il faut Python 3)." : $"yt-dlp {tool.Value.Version} installé.";
    }

    // ------------------------------------------------------------------ chercher

    public async Task<List<WebVideo>> Search(string input, int max, CancellationToken cancellationToken)
    {
        (string Path, string Version)? tool = await Tool(cancellationToken) ?? throw new InvalidOperationException("yt-dlp n'est pas installé : bouton « Installer yt-dlp ».");
        string text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return [];
        }

        max = Math.Clamp(max, 1, 200);
        bool isUrl = Uri.TryCreate(text, UriKind.Absolute, out Uri uri) && uri.Scheme is "http" or "https";
        string target = isUrl ? text : $"ytsearch{max}:{text}";
        (int code, string output, string errors) = await Run(
            tool.Value.Path,
            [.. Cookies(), "--flat-playlist", "-J", "--no-warnings", "--playlist-end", max.ToString(CultureInfo.InvariantCulture), target],
            null,
            TimeSpan.FromMinutes(2),
            cancellationToken);
        if (code != 0)
        {
            throw new InvalidOperationException($"yt-dlp : {LastLine(errors)}");
        }

        return Parse(output).Take(max).ToList();
    }

    /// <summary>La réponse de « yt-dlp -J » : une playlist (entries) ou une seule vidéo.</summary>
    public static List<WebVideo> Parse(string json)
    {
        JsonNode root = JsonNode.Parse(json);
        if (root is null)
        {
            return [];
        }

        IEnumerable<JsonNode> entries = root["entries"] is JsonArray array
            ? array.SelectMany(e => e?["entries"] is JsonArray inner ? inner.Where(i => i is not null) : [e]) // chaîne → onglets → vidéos
            : [root];
        var result = new List<WebVideo>();
        foreach (JsonNode e in entries.Where(e => e is not null))
        {
            string id = e["id"]?.ToString() ?? string.Empty;
            string url = e["webpage_url"]?.ToString() ?? e["url"]?.ToString() ?? string.Empty;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && id.Length > 0)
            {
                url = $"https://www.youtube.com/watch?v={id}";
            }

            if (url.Length == 0)
            {
                continue;
            }

            string thumbnail = e["thumbnail"]?.ToString()
                               ?? (e["thumbnails"] as JsonArray)?.LastOrDefault(t => t?["url"] is not null)?["url"]?.ToString()
                               ?? string.Empty;
            double? seconds = e["duration"] is JsonValue d && d.TryGetValue(out double v) ? v : null;
            result.Add(new WebVideo(id, url, e["title"]?.ToString() ?? url, e["channel"]?.ToString() ?? e["uploader"]?.ToString() ?? string.Empty, seconds, thumbnail));
        }

        return result.DistinctBy(v => v.Url).ToList();
    }

    // ------------------------------------------------------------------ garder les adresses

    public string AddressList(IEnumerable<WebVideo> videos) =>
        string.Join(Environment.NewLine, videos.Select(v => $"{v.Url}\t{v.Title}"));

    public List<WebVideoRecord> History()
    {
        var result = new List<WebVideoRecord>();
        foreach (ImportRole role in Destinations)
        {
            string file = Path.Combine(Folder(role), RegisterFile);
            if (!File.Exists(file))
            {
                continue;
            }

            foreach (string line in File.ReadAllLines(file).Skip(1))
            {
                string[] parts = line.Split('\t');
                if (parts.Length >= 4 && DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset when))
                {
                    result.Add(new WebVideoRecord(when, parts[1], parts[2], parts[3], FolderImportService.Label(role)));
                }
            }
        }

        return result.OrderByDescending(r => r.When).ToList();
    }

    // ------------------------------------------------------------------ télécharger

    public WebDownloadJob GetJob(string id) => Jobs.GetValueOrDefault(id);

    public WebDownloadJob Download(IReadOnlyList<WebVideo> videos, ImportRole role, int maxHeight)
    {
        var job = new WebDownloadJob { Total = videos.Count };
        Jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            await OneAtATime.WaitAsync();
            try
            {
                await RunDownload(job, videos.ToList(), role, maxHeight);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Vidéos du web : échec");
                job.State = WebJobState.Failed;
                job.Error = ex.Message;
            }
            finally
            {
                OneAtATime.Release();
            }
        });
        return job;
    }

    private async Task RunDownload(WebDownloadJob job, List<WebVideo> videos, ImportRole role, int maxHeight)
    {
        (string Path, string Version) tool = await Tool(CancellationToken.None) ?? throw new InvalidOperationException("yt-dlp n'est pas installé.");
        string ffmpeg = await FFmpegPath();
        string folder = Folder(role);
        Directory.CreateDirectory(folder);
        string register = Path.Combine(folder, RegisterFile);
        if (!File.Exists(register))
        {
            await File.WriteAllTextAsync(register, "date\ttitre\tadresse\tfichier\n", new UTF8Encoding(false));
        }

        int height = maxHeight <= 0 ? 4320 : maxHeight;
        foreach (WebVideo video in videos)
        {
            job.Current = video.Title;
            job.Progress = 0;
            var args = new List<string>(Cookies())
            {
                "-f", $"bv*[height<={height}]+ba/b[height<={height}]/b", "--merge-output-format", "mp4",
                "--no-playlist", "--newline", "--no-part", "--no-warnings", "--windows-filenames",
                "-o", Path.Combine(folder, "%(title).120B [%(id)s].%(ext)s"),
                // --print rend yt-dlp muet et « pour de faux » : on lui redemande de télécharger et d'afficher la progression
                "--print", "after_move:filepath", "--no-simulate", "--progress"
            };
            if (!string.IsNullOrWhiteSpace(ffmpeg) && File.Exists(ffmpeg))
            {
                args.AddRange(["--ffmpeg-location", Path.GetDirectoryName(ffmpeg)!]);
            }

            args.Add(video.Url);
            (int code, string output, string errors) = await Run(
                tool.Path, args,
                line =>
                {
                    Match m = Percent.Match(line);
                    if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                    {
                        job.Progress = p / 100;
                    }
                },
                TimeSpan.FromHours(3),
                CancellationToken.None);
            string file = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(l => File.Exists(l));
            if (code != 0 || file is null)
            {
                string error = LastLine(errors);
                job.Failed.Add($"{video.Title} : {(error.Contains("not a bot", StringComparison.OrdinalIgnoreCase) ? "YouTube demande une connexion : choisissez votre navigateur dans « Connexion YouTube » (Firefox conseillé)" : error)}");
            }
            else
            {
                job.Files.Add(file);
                await File.AppendAllTextAsync(
                    register,
                    $"{DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture)}\t{Clean(video.Title)}\t{video.Url}\t{Path.GetFileName(file)}\n",
                    new UTF8Encoding(false));
            }

            job.Done++;
        }

        job.Current = "Ajout à la médiathèque…";
        if (job.Files.Count > 0)
        {
            await import.Import(folder, role, CancellationToken.None);
        }

        job.Progress = 1;
        job.Current = string.Empty;
        job.State = job.Files.Count == 0 && job.Failed.Count > 0 ? WebJobState.Failed : WebJobState.Done;
        job.Error = job.Failed.Count == 0 ? null : string.Join(" · ", job.Failed);
    }

    /// <summary>« Confirmez que vous n'êtes pas un robot » : yt-dlp reprend la connexion YouTube du navigateur choisi.</summary>
    private static List<string> Cookies()
    {
        string browser = NounoursSettings.Load().WebCookiesBrowser?.Trim().ToLowerInvariant() ?? string.Empty;
        return browser.Length == 0 ? [] : ["--cookies-from-browser", browser];
    }

    private static string Clean(string text) => (text ?? string.Empty).Replace('\t', ' ').Replace('\n', ' ');

    private static string LastLine(string text) =>
        (text ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "erreur inconnue";

    private async Task<string> FFmpegPath()
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync();
        string key = ConfigElementKey.FFmpegPath.Key;
        return await db.ConfigElements.Where(c => c.Key == key).Select(c => c.Value).FirstOrDefaultAsync();
    }

    private static async Task<(int Code, string Output, string Errors)> Run(
        string file,
        IEnumerable<string> args,
        Action<string> onLine,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // yt-dlp en UTF-8 même sous Windows (titres accentués)
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{file} ne démarre pas");
        var output = new StringBuilder();
        var errors = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            output.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                errors.AppendLine(e.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timer.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw new InvalidOperationException("yt-dlp a mis trop de temps à répondre.");
        }

        return (process.ExitCode, output.ToString(), errors.ToString());
    }
}
