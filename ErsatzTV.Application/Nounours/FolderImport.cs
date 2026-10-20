using ErsatzTV.Application.Libraries;
using ErsatzTV.Application.MediaCollections;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Nounours;

public enum ImportRole
{
    Pubs,
    Jingles,
    BandesAnnonces,
    Habillage
}

public sealed record ImportGroup(
    ImportRole Role,
    string Label,
    string Description,
    int? LibraryId,
    List<string> Folders,
    int Items,
    int? SmartCollectionId,
    string SmartCollectionName);

public sealed record ImportResult(ImportRole Role, int LibraryId, int SmartCollectionId, string SmartCollectionName, int FilesFound);

public sealed record FolderEntry(string Name, string Path, int Videos);

/// <summary>
///     Import en un clic d'un dossier de pubs, de jingles ou de bandes-annonces :
///     le dossier est ajouté à une bibliothèque locale dédiée (« Habillage · Pubs »…), analysé,
///     et une collection intelligente du même nom (« Pubs »…) est tenue à jour — prête pour les programmes.
/// </summary>
public interface IFolderImportService
{
    Task<List<ImportGroup>> GetGroups(CancellationToken cancellationToken);
    Task<Either<BaseError, ImportResult>> Import(string folder, ImportRole role, CancellationToken cancellationToken);
    Task<Either<BaseError, Unit>> RemoveFolder(ImportRole role, string folder, CancellationToken cancellationToken);
    Task Rescan(ImportRole role, CancellationToken cancellationToken);

    /// <summary>Dossier où sont rangés les fichiers envoyés depuis le navigateur.</summary>
    string UploadFolder(ImportRole role);

    /// <summary>Sous-dossiers d'un dossier du serveur (ou les lecteurs / la racine si vide).</summary>
    List<FolderEntry> Browse(string folder);
}

public sealed class FolderImportService(IDbContextFactory<TvContext> dbContextFactory, IMediator mediator)
    : IFolderImportService
{
    public static readonly string[] VideoExtensions =
        [".mp4", ".mkv", ".mov", ".avi", ".ts", ".m2ts", ".webm", ".m4v", ".mpg", ".mpeg", ".wmv", ".flv", ".ogv"];

    public static string Label(ImportRole role) =>
        role switch
        {
            ImportRole.Pubs => "Pubs",
            ImportRole.Jingles => "Jingles",
            ImportRole.BandesAnnonces => "Bandes-annonces",
            _ => "Habillage vidéo"
        };

    public static string Describe(ImportRole role) =>
        role switch
        {
            ImportRole.Pubs => "Spots publicitaires, autopromos : pour les coupures et les écrans pub.",
            ImportRole.Jingles => "Jingles d'ouverture / de fin, virgules, génériques courts.",
            ImportRole.BandesAnnonces => "Bandes-annonces de films et de programmes à venir.",
            _ => "Mires, boucles d'attente, écrans de secours, tout le reste."
        };

    private static string LibraryName(ImportRole role) => $"Habillage · {Label(role)}";

    public async Task<List<ImportGroup>> GetGroups(CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var result = new List<ImportGroup>();
        foreach (ImportRole role in Enum.GetValues<ImportRole>())
        {
            string name = LibraryName(role);
            LocalLibrary library = await dbContext.LocalLibraries
                .AsNoTracking()
                .Include(l => l.Paths)
                .FirstOrDefaultAsync(l => l.Name == name, cancellationToken);

            int items = 0;
            if (library is not null)
            {
                var pathIds = library.Paths.Select(p => p.Id).ToList();
                items = await dbContext.MediaItems.CountAsync(m => pathIds.Contains(m.LibraryPathId), cancellationToken);
            }

            string query = library is null ? null : $"library_id:{library.Id}";
            SmartCollection collection = query is null
                ? null
                : await dbContext.SmartCollections
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Query == query, cancellationToken);

            result.Add(
                new ImportGroup(
                    role,
                    Label(role),
                    Describe(role),
                    library?.Id,
                    library?.Paths.Select(p => p.Path).OrderBy(p => p).ToList() ?? [],
                    items,
                    collection?.Id,
                    collection?.Name));
        }

        return result;
    }

    public async Task<Either<BaseError, ImportResult>> Import(string folder, ImportRole role, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return BaseError.New("Indiquez un dossier");
        }

        string path;
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BaseError.New($"Chemin invalide : {ex.Message}");
        }

        if (!Directory.Exists(path))
        {
            return BaseError.New($"Le dossier « {path} » n'existe pas sur le serveur ErsatzTV");
        }

        int files = CountVideos(path, true);
        string libraryName = LibraryName(role);

        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        LocalLibrary existing = await dbContext.LocalLibraries
            .AsNoTracking()
            .Include(l => l.Paths)
            .FirstOrDefaultAsync(l => l.Name == libraryName, cancellationToken);

        int libraryId;
        if (existing is null)
        {
            Either<BaseError, LocalLibraryViewModel> created = await mediator.Send(
                new CreateLocalLibrary(libraryName, LibraryMediaKind.OtherVideos, [path]),
                cancellationToken);

            if (created.IsLeft)
            {
                return created.LeftToSeq().Head();
            }

            libraryId = created.RightToSeq().Head().Id;
        }
        else
        {
            libraryId = existing.Id;
            if (existing.Paths.All(p => !string.Equals(Path.TrimEndingDirectorySeparator(p.Path), path, StringComparison.OrdinalIgnoreCase)))
            {
                var paths = existing.Paths.Select(p => new UpdateLocalLibraryPath(p.Id, p.Path)).ToList();
                paths.Add(new UpdateLocalLibraryPath(0, path));
                Either<BaseError, LocalLibraryViewModel> updated = await mediator.Send(
                    new UpdateLocalLibrary(existing.Id, existing.Name, paths),
                    cancellationToken);

                if (updated.IsLeft)
                {
                    return updated.LeftToSeq().Head();
                }
            }

            await mediator.Send(new QueueLibraryScanByLibraryId(libraryId), cancellationToken);
        }

        Either<BaseError, (int Id, string Name)> collection = await EnsureCollection(dbContext, role, libraryId, cancellationToken);
        return collection.Map(c => new ImportResult(role, libraryId, c.Id, c.Name, files));
    }

    public async Task<Either<BaseError, Unit>> RemoveFolder(ImportRole role, string folder, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        string libraryName = LibraryName(role);
        LocalLibrary existing = await dbContext.LocalLibraries
            .AsNoTracking()
            .Include(l => l.Paths)
            .FirstOrDefaultAsync(l => l.Name == libraryName, cancellationToken);

        if (existing is null)
        {
            return Unit.Default;
        }

        var paths = existing.Paths
            .Where(p => p.Path != folder)
            .Select(p => new UpdateLocalLibraryPath(p.Id, p.Path))
            .ToList();

        Either<BaseError, LocalLibraryViewModel> updated = await mediator.Send(
            new UpdateLocalLibrary(existing.Id, existing.Name, paths),
            cancellationToken);

        return updated.Map(_ => Unit.Default);
    }

    public async Task Rescan(ImportRole role, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        string libraryName = LibraryName(role);
        int? id = await dbContext.LocalLibraries
            .Where(l => l.Name == libraryName)
            .Select(l => (int?)l.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (id.HasValue)
        {
            await mediator.Send(new QueueLibraryScanByLibraryId(id.Value), cancellationToken);
        }
    }

    public string UploadFolder(ImportRole role)
    {
        string folder = Path.Combine(FileSystemLayout.ImportsFolder, Label(role));
        Directory.CreateDirectory(folder);
        return folder;
    }

    public List<FolderEntry> Browse(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                if (OperatingSystem.IsWindows())
                {
                    return DriveInfo.GetDrives()
                        .Where(d => d.IsReady)
                        .Select(d => new FolderEntry(d.Name, d.RootDirectory.FullName, 0))
                        .ToList();
                }

                folder = "/";
            }

            return new DirectoryInfo(folder)
                .EnumerateDirectories()
                .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden) && !d.Name.StartsWith('.'))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Take(500)
                .Select(d => new FolderEntry(d.Name, d.FullName, CountVideos(d.FullName, false)))
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private async Task<Either<BaseError, (int Id, string Name)>> EnsureCollection(
        TvContext dbContext,
        ImportRole role,
        int libraryId,
        CancellationToken cancellationToken)
    {
        string name = Label(role);
        string query = $"library_id:{libraryId}";
        SmartCollection existing = await dbContext.SmartCollections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Name == name, cancellationToken);

        // une collection « Pubs » créée à la main n'est jamais écrasée : on utilise alors « Pubs · import »
        if (existing is not null && !existing.Query.StartsWith("library_id:", StringComparison.Ordinal))
        {
            name = $"{name} · import";
            existing = await dbContext.SmartCollections
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Name == name, cancellationToken);
        }

        if (existing is not null)
        {
            if (existing.Query != query)
            {
                Either<BaseError, UpdateSmartCollectionResult> updated =
                    await mediator.Send(new UpdateSmartCollection(existing.Id, name, query), cancellationToken);
                return updated.Map(_ => (existing.Id, name));
            }

            return (existing.Id, name);
        }

        Either<BaseError, SmartCollectionViewModel> created =
            await mediator.Send(new CreateSmartCollection(query, name), cancellationToken);
        return created.Map(c => (c.Id, name));
    }

    private static int CountVideos(string folder, bool recursive)
    {
        try
        {
            return Directory
                .EnumerateFiles(folder, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = recursive,
                    IgnoreInaccessible = true,
                    MaxRecursionDepth = 6
                })
                .Take(10_000)
                .Count(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return 0;
        }
    }
}
