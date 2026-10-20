using ErsatzTV.Application.Nounours;
using ErsatzTV.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace ErsatzTV.Controllers;

/// <summary>Fichiers utilisés par le studio d'habillage (aperçu dans le navigateur).</summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public class NounoursController(IHabillageService habillageService) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    // image d'un calque : uniquement depuis les dossiers d'habillage (pas de lecture arbitraire du disque)
    [HttpGet("nounours/habillage/asset")]
    public IActionResult GetAsset([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return NotFound();
        }

        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(FileSystemLayout.HabillageAssetsFolder);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(full))
        {
            return NotFound();
        }

        if (!ContentTypes.TryGetContentType(full, out string contentType))
        {
            contentType = "application/octet-stream";
        }

        return PhysicalFile(full, contentType);
    }

    [HttpGet("nounours/habillage/font/{fileName}")]
    public IActionResult GetFont(string fileName)
    {
        string full = Path.GetFullPath(Path.Combine(FileSystemLayout.FontsCacheFolder, Path.GetFileName(fileName)));
        if (!System.IO.File.Exists(full))
        {
            return NotFound();
        }

        string contentType = full.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ? "font/otf" : "font/ttf";
        return PhysicalFile(full, contentType);
    }

    [HttpPost("nounours/habillage/upload")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest();
        }

        string ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp"))
        {
            return BadRequest("Formats acceptés : png, jpg, gif, webp");
        }

        await using Stream stream = file.OpenReadStream();
        string saved = await habillageService.SaveAsset(file.FileName, stream, cancellationToken);
        return Ok(new { path = saved });
    }
}
