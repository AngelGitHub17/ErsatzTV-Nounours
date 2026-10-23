using System.IO.Compression;
using ErsatzTV.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ErsatzTV.Controllers;

/// <summary>
///     Sauvegarde complète en un fichier .zip : base de données (copie cohérente, même ErsatzTV allumé),
///     habillages, programmes, éléments graphiques, réglages de l'édition Nounours, sons de l'atelier.
///     Les vidéos ne sont pas incluses (elles restent dans vos dossiers).
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public class NounoursBackupController(ILogger<NounoursBackupController> logger) : ControllerBase
{
    private static readonly string[] Folders =
    [
        FileSystemLayout.TemplatesFolder,
        FileSystemLayout.AudienceFolder,
        FileSystemLayout.RegieFolder,
        FileSystemLayout.PluginsFolder,
        Path.Combine(FileSystemLayout.AtelierFolder, "sources")
    ];

    [HttpGet("nounours/sauvegarde.zip")]
    public async Task<IActionResult> Download(CancellationToken cancellationToken)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"ersatztv-sauvegarde-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        string zipPath = temp + ".zip";
        try
        {
            using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                if (System.IO.File.Exists(FileSystemLayout.DatabasePath))
                {
                    // VACUUM INTO : copie propre de la base, sans arrêter ErsatzTV
                    string snapshot = Path.Combine(temp, "ersatztv.sqlite3");
                    await using (var connection = new SqliteConnection($"Data Source={FileSystemLayout.DatabasePath}"))
                    {
                        await connection.OpenAsync(cancellationToken);
                        await using SqliteCommand command = connection.CreateCommand();
                        command.CommandText = "VACUUM INTO $path";
                        command.Parameters.AddWithValue("$path", snapshot);
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }

                    SqliteConnection.ClearAllPools();
                    zip.CreateEntryFromFile(snapshot, "ersatztv.sqlite3", CompressionLevel.Optimal);
                }

                foreach (string folder in Folders.Where(Directory.Exists))
                {
                    string relativeRoot = Path.GetRelativePath(FileSystemLayout.AppDataFolder, folder);
                    foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        string name = Path.Combine(relativeRoot, Path.GetRelativePath(folder, file)).Replace('\\', '/');
                        try
                        {
                            zip.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
                        }
                        catch (IOException ex)
                        {
                            logger.LogWarning("Sauvegarde : {File} ignoré ({Error})", file, ex.Message);
                        }
                    }
                }

                ZipArchiveEntry readme = zip.CreateEntry("LISEZ-MOI.txt");
                await using (var writer = new StreamWriter(readme.Open()))
                {
                    await writer.WriteAsync(
                        "Sauvegarde ErsatzTV - édition Nounours\n\n"
                        + "Pour restaurer : arrêtez ErsatzTV, puis décompressez ce fichier dans le dossier de configuration\n"
                        + "(Windows : %LOCALAPPDATA%\\ersatztv ; Linux : ~/.local/share/ersatztv) en remplaçant les fichiers.\n"
                        + "Redémarrez ErsatzTV. Les vidéos ne sont pas dans la sauvegarde : elles doivent être aux mêmes emplacements.\n");
                }
            }

            byte[] bytes = await System.IO.File.ReadAllBytesAsync(zipPath, cancellationToken);
            return File(bytes, "application/zip", $"ersatztv-sauvegarde-{DateTime.Now:yyyy-MM-dd-HHmm}.zip");
        }
        finally
        {
            try
            {
                Directory.Delete(temp, true);
                System.IO.File.Delete(zipPath);
            }
            catch (IOException)
            {
                // nettoyé plus tard par le système
            }
        }
    }
}
