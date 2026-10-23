using ErsatzTV.Application.Nounours;
using ErsatzTV.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace ErsatzTV.Controllers;

/// <summary>Fichiers utilisés par le studio d'habillage (aperçu dans le navigateur).</summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public class NounoursController(
    IHabillageService habillageService,
    ErsatzTV.Application.Nounours.Atelier.AtelierService atelier) : ControllerBase
{
    // vidéo créée par l'atelier (ou importée) : lecture dans le navigateur, uniquement depuis le dossier des imports
    [HttpGet("nounours/atelier/video")]
    public IActionResult GetAtelierVideo([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return NotFound();
        }

        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(FileSystemLayout.ImportsFolder);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(full))
        {
            return NotFound();
        }

        return PhysicalFile(full, "video/mp4", enableRangeProcessing: true);
    }

    // son envoyé dans l'atelier (écoute dans la page)
    [HttpGet("nounours/atelier/son")]
    public IActionResult GetAtelierSound([FromQuery] string name)
    {
        string path = ErsatzTV.Application.Nounours.Atelier.AtelierService.SourcePath(name);
        if (path is null || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        string type = ContentTypes.TryGetContentType(path, out string contentType) ? contentType : "application/octet-stream";
        return PhysicalFile(path, type, enableRangeProcessing: true);
    }

    // vignette d'un programme à l'instant t, pour choisir les extraits d'une bande-annonce
    [HttpGet("nounours/atelier/vignette")]
    public async Task<IActionResult> GetThumbnail([FromQuery] int item, [FromQuery] double t, CancellationToken cancellationToken)
    {
        string path = await atelier.PathOf(item, cancellationToken);
        byte[] jpeg = await atelier.Thumbnail(path, Math.Max(0, t), cancellationToken);
        return jpeg.Length == 0 ? NotFound() : File(jpeg, "image/jpeg");
    }

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
