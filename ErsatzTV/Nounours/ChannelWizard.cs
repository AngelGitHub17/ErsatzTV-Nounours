using MediatR;
using ErsatzTV.Application.Channels;
using ErsatzTV.Application.FFmpegProfiles;
using ErsatzTV.Application.MediaCollections;
using ErsatzTV.Application.Nounours;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Application.ProgramSchedules;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.ViewModels;

namespace ErsatzTV.Nounours;

public enum WizardSlotMode
{
    /// <summary>En continu jusqu'au créneau suivant.</summary>
    Flood,

    /// <summary>Un seul élément.</summary>
    One,

    /// <summary>N éléments.</summary>
    Multiple,

    /// <summary>Autant d'éléments que possible pendant X minutes.</summary>
    Duration
}

public sealed class WizardSlot
{
    public ContentSourceKind Kind { get; set; } = ContentSourceKind.Search;
    public int? ContentId { get; set; }
    public string ContentName { get; set; } = string.Empty;
    public string Query { get; set; } = "type:movie";

    public bool FixedStart { get; set; }
    public TimeSpan? StartTime { get; set; } = new(20, 0, 0);

    public WizardSlotMode Mode { get; set; } = WizardSlotMode.Flood;
    public int Count { get; set; } = 2;
    public int DurationMinutes { get; set; } = 60;
    public PlaybackOrder Order { get; set; } = PlaybackOrder.Shuffle;
    public string CustomTitle { get; set; } = string.Empty;
}

public sealed class ChannelWizardModel
{
    public string Name { get; set; } = string.Empty;
    public string Number { get; set; } = string.Empty;
    public string Group { get; set; } = "NounoursTV";
    public string Categories { get; set; } = string.Empty;
    public string LogoUrl { get; set; } = string.Empty;
    public int FFmpegProfileId { get; set; }
    public int? WatermarkId { get; set; }
    public StreamingMode StreamingMode { get; set; } = StreamingMode.TransportStreamHybrid;
    public bool ShowInEpg { get; set; } = true;
    public List<WizardSlot> Slots { get; set; } = [new()];
    public string HabillageSlug { get; set; } = string.Empty;
    public bool KeepMultiPartEpisodesTogether { get; set; } = true;
}

public sealed record ChannelWizardResult(int ChannelId, int ScheduleId, int PlayoutId, List<string> Steps);

/// <summary>
///     Crée en une fois : collections intelligentes (si besoin) → planning + éléments → chaîne → playout → habillage.
///     En cas d'erreur, ce qui a déjà été créé est supprimé.
/// </summary>
public sealed class ChannelWizardService(
    IMediator mediator,
    IHabillageService habillageService,
    IProgrammeService programmeService,
    Microsoft.EntityFrameworkCore.IDbContextFactory<Infrastructure.Data.TvContext> dbContextFactory,
    ILogger<ChannelWizardService> logger)
{
    public async Task<ChannelWizardModel> CreateDefaults(CancellationToken cancellationToken)
    {
        FFmpegSettingsViewModel ffmpegSettings =
            await mediator.Send(new GetFFmpegSettings(), cancellationToken);

        List<ChannelViewModel> channels = await mediator.Send(new GetAllChannels(), cancellationToken);
        int maxNumber = channels
            .Select(c => int.TryParse(c.Number.Split('.')[0], out int n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

        return new ChannelWizardModel
        {
            Number = (maxNumber + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            FFmpegProfileId = ffmpegSettings.DefaultFFmpegProfileId
        };
    }

    public async Task<Either<BaseError, ChannelWizardResult>> Create(
        ChannelWizardModel model,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var steps = new List<string>();
        int? scheduleId = null;
        int? channelId = null;
        var createdSmartCollections = new List<int>();

        void Report(string message)
        {
            steps.Add(message);
            progress?.Report(message);
        }

        try
        {
            // 1. contenus « recherche » → collections intelligentes
            foreach (WizardSlot slot in model.Slots.Where(s => s.Kind == ContentSourceKind.Search))
            {
                string name = string.IsNullOrWhiteSpace(slot.ContentName)
                    ? $"{model.Name} · {slot.Query}"
                    : slot.ContentName;
                if (name.Length > 40)
                {
                    name = name[..39].TrimEnd() + "…";
                }

                Either<BaseError, SmartCollectionViewModel> result =
                    await mediator.Send(new CreateSmartCollection(slot.Query, name), cancellationToken);

                foreach (BaseError error in result.LeftToSeq())
                {
                    // nom déjà pris : on suffixe
                    result = await mediator.Send(
                        new CreateSmartCollection(slot.Query, $"{name} ({DateTime.Now:HHmmss})"),
                        cancellationToken);
                    foreach (BaseError error2 in result.LeftToSeq())
                    {
                        return BaseError.New($"Collection intelligente « {name} » : {error2.Value} ({error.Value})");
                    }
                }

                foreach (SmartCollectionViewModel smart in result.RightToSeq())
                {
                    slot.ContentId = smart.Id;
                    createdSmartCollections.Add(smart.Id);
                    Report($"Collection intelligente « {smart.Name} » créée");
                }
            }

            // 2. planning
            Either<BaseError, CreateProgramScheduleResult> schedule = await mediator.Send(
                new CreateProgramSchedule(
                    $"{model.Name} · planning",
                    model.KeepMultiPartEpisodesTogether,
                    TreatCollectionsAsShows: false,
                    ShuffleScheduleItems: false,
                    RandomStartPoint: false,
                    FixedStartTimeBehavior.Flexible),
                cancellationToken);

            foreach (BaseError error in schedule.LeftToSeq())
            {
                return await Rollback(error.Value);
            }

            scheduleId = schedule.RightToSeq().Head().ProgramScheduleId;
            Report("Planning créé");

            // 3. créneaux
            IEnumerable<WizardSlot> orderedSlots = model.Slots;

            var index = 1;
            foreach (WizardSlot slot in orderedSlots)
            {
                Either<BaseError, ProgramScheduleItemViewModel> item =
                    await mediator.Send(ToScheduleItem(scheduleId.Value, slot, await ProgrammeFor(slot, cancellationToken)), cancellationToken);
                foreach (BaseError error in item.LeftToSeq())
                {
                    return await Rollback($"Créneau {index} : {error.Value}");
                }

                index++;
            }

            await SortFixedItems(scheduleId.Value, cancellationToken);
            Report($"{model.Slots.Count} créneau(x) ajouté(s)");

            // 4. chaîne
            var channel = new ChannelEditViewModel
            {
                Name = model.Name,
                Number = model.Number,
                Group = model.Group,
                Categories = model.Categories,
                FFmpegProfileId = model.FFmpegProfileId,
                ExternalLogoUrl = model.LogoUrl,
                StreamingEngine = StreamingEngine.Legacy,
                NextEngineTextSubtitleMode = NextEngineTextSubtitleMode.Burn,
                StreamingMode = model.StreamingMode,
                WatermarkId = model.WatermarkId,
                IsEnabled = true,
                ShowInEpg = model.ShowInEpg
            };

            Either<BaseError, CreateChannelResult> createdChannel =
                await mediator.Send(channel.ToCreate(), cancellationToken);
            foreach (BaseError error in createdChannel.LeftToSeq())
            {
                return await Rollback($"Chaîne : {error.Value}");
            }

            channelId = createdChannel.RightToSeq().Head().ChannelId;
            Report($"Chaîne {model.Number} « {model.Name} » créée");

            // 5. playout (construit automatiquement en arrière-plan)
            Either<BaseError, CreatePlayoutResponse> playout = await mediator.Send(
                new CreateClassicPlayout(channelId.Value, scheduleId.Value),
                cancellationToken);
            foreach (BaseError error in playout.LeftToSeq())
            {
                return await Rollback($"Playout : {error.Value}");
            }

            int playoutId = playout.RightToSeq().Head().PlayoutId;
            Report("Playout créé, construction de la grille lancée");

            // 6. habillage
            if (!string.IsNullOrWhiteSpace(model.HabillageSlug))
            {
                Option<HabillageScene> maybeScene = await habillageService.GetScene(model.HabillageSlug, cancellationToken);
                foreach (HabillageScene scene in maybeScene)
                {
                    List<HabillageChannelTarget> targets = await habillageService.GetTargets(cancellationToken);
                    var channelIds = targets
                        .Where(t => t.Scenes.Contains(scene.Slug))
                        .Select(t => t.ChannelId)
                        .Append(channelId.Value)
                        .ToList();
                    await habillageService.Apply(scene.Slug, channelIds, cancellationToken);
                    Report($"Habillage « {scene.Name} » appliqué");
                }
            }

            return new ChannelWizardResult(channelId.Value, scheduleId.Value, playoutId, steps);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Assistant de chaîne : échec, annulation des éléments créés");
            return await Rollback(ex.Message);
        }

        async Task<Either<BaseError, ChannelWizardResult>> Rollback(string message)
        {
            if (channelId.HasValue)
            {
                await mediator.Send(new DeleteChannel(channelId.Value), CancellationToken.None);
            }

            if (scheduleId.HasValue)
            {
                await mediator.Send(new DeleteProgramSchedule(scheduleId.Value), CancellationToken.None);
            }

            foreach (int id in createdSmartCollections)
            {
                await mediator.Send(new DeleteSmartCollection(id), CancellationToken.None);
            }

            return BaseError.New(message);
        }
    }

    /// <summary>Ajoute un créneau à heure fixe au planning classique d'une chaîne puis reconstruit sa grille.</summary>
    public async Task<Option<BaseError>> AddSlotToChannel(
        int playoutId,
        int programScheduleId,
        WizardSlot slot,
        System.Threading.Channels.ChannelWriter<Application.IBackgroundServiceRequest> worker,
        CancellationToken cancellationToken)
    {
        if (slot.Kind == ContentSourceKind.Search)
        {
            Either<BaseError, SmartCollectionViewModel> smart = await mediator.Send(
                new CreateSmartCollection(slot.Query, Cut(string.IsNullOrWhiteSpace(slot.ContentName) ? slot.Query : slot.ContentName, 40) + $" #{DateTime.Now:HHmmss}"),
                cancellationToken);
            foreach (BaseError error in smart.LeftToSeq())
            {
                return error;
            }

            slot.ContentId = smart.RightToSeq().Head().Id;
        }

        Either<BaseError, ProgramScheduleItemViewModel> result =
            await mediator.Send(ToScheduleItem(programScheduleId, slot, await ProgrammeFor(slot, cancellationToken)), cancellationToken);
        foreach (BaseError error in result.LeftToSeq())
        {
            return error;
        }

        await SortFixedItems(programScheduleId, cancellationToken);
        await worker.WriteAsync(new BuildPlayout(playoutId, PlayoutBuildMode.Reset), cancellationToken);
        return Option<BaseError>.None;
    }

    /// <summary>
    ///     ErsatzTV lit les créneaux dans l'ordre de la liste : un créneau à heure fixe placé après un créneau plus tardif
    ///     est reporté au lendemain. On remet donc les créneaux fixes dans l'ordre chronologique ; chaque créneau
    ///     « à la suite » reste attaché au créneau fixe qui le précède.
    /// </summary>
    public async Task SortFixedItems(int programScheduleId, CancellationToken cancellationToken)
    {
        await using Infrastructure.Data.TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<ProgramScheduleItem> items = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            dbContext.ProgramScheduleItems.Where(i => i.ProgramScheduleId == programScheduleId),
            cancellationToken);

        var ordered = items.OrderBy(i => i.Index).ToList();
        var leading = ordered.TakeWhile(i => i.StartType != StartType.Fixed).ToList();
        var groups = new List<List<ProgramScheduleItem>>();
        foreach (ProgramScheduleItem item in ordered.Skip(leading.Count))
        {
            if (item.StartType == StartType.Fixed || groups.Count == 0)
            {
                groups.Add([item]);
            }
            else
            {
                groups[^1].Add(item);
            }
        }

        var result = leading
            .Concat(groups.OrderBy(g => g[0].StartTime ?? TimeSpan.Zero).SelectMany(g => g))
            .ToList();
        for (var i = 0; i < result.Count; i++)
        {
            result[i].Index = i + 1;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Cut(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..(max - 1)].TrimEnd() + "…";

    private async Task<Option<ProgrammeScheduling>> ProgrammeFor(WizardSlot slot, CancellationToken cancellationToken) =>
        slot.Kind == ContentSourceKind.Programme && slot.ContentId is { } playlistId
            ? await programmeService.GetSchedulingByPlaylist(playlistId, cancellationToken)
            : Option<ProgrammeScheduling>.None;

    internal static AddProgramScheduleItem ToScheduleItem(int scheduleId, WizardSlot slot, Option<ProgrammeScheduling> programme)
    {
        CollectionType collectionType = ContentCatalog.ToCollectionType(slot.Kind);
        PlayoutMode mode = slot.Mode switch
        {
            WizardSlotMode.One => PlayoutMode.One,
            WizardSlotMode.Multiple => PlayoutMode.Multiple,
            WizardSlotMode.Duration => PlayoutMode.Duration,
            _ => PlayoutMode.Flood
        };

        // un programme composé = une playlist : « un programme » = un passage complet de la playlist
        int? midRollFillerId = null;
        string multipleCount = mode == PlayoutMode.Multiple
            ? Math.Max(1, slot.Count).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        foreach (ProgrammeScheduling p in programme)
        {
            midRollFillerId = p.BreakFillerId;
            if (mode is PlayoutMode.One or PlayoutMode.Multiple)
            {
                int passes = mode == PlayoutMode.One ? 1 : Math.Max(1, slot.Count);
                mode = PlayoutMode.Multiple;
                multipleCount = (p.ItemCount * passes).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return new AddProgramScheduleItem(
            scheduleId,
            slot.FixedStart ? StartType.Fixed : StartType.Dynamic,
            slot.FixedStart ? slot.StartTime ?? TimeSpan.Zero : null,
            FixedStartTimeBehavior: null,
            mode,
            collectionType,
            CollectionId: collectionType == CollectionType.Collection ? slot.ContentId : null,
            MultiCollectionId: collectionType == CollectionType.MultiCollection ? slot.ContentId : null,
            SmartCollectionId: collectionType == CollectionType.SmartCollection ? slot.ContentId : null,
            RerunCollectionId: null,
            MediaItemId: collectionType == CollectionType.TelevisionShow ? slot.ContentId : null,
            PlaylistId: collectionType == CollectionType.Playlist ? slot.ContentId : null,
            SearchTitle: null,
            SearchQuery: null,
            collectionType == CollectionType.Playlist ? PlaybackOrder.None : slot.Order,
            MarathonGroupBy.None,
            MarathonShuffleGroups: false,
            MarathonShuffleItems: false,
            MarathonBatchSize: null,
            FillWithGroupMode.None,
            MultipleMode.Count,
            multipleCount,
            mode == PlayoutMode.Duration ? TimeSpan.FromMinutes(Math.Max(1, slot.DurationMinutes)) : null,
            TailMode.None,
            DiscardToFillAttempts: null,
            string.IsNullOrWhiteSpace(slot.CustomTitle) ? null : slot.CustomTitle,
            GuideMode.Normal,
            PreRollFillerId: null,
            MidRollFillerId: midRollFillerId,
            PostRollFillerId: null,
            TailFillerId: null,
            FallbackFillerId: null,
            WatermarkIds: [],
            GraphicsElementIds: [],
            PreferredAudioLanguageCode: null,
            PreferredAudioTitle: null,
            PreferredSubtitleLanguageCode: null,
            SubtitleMode: null);
    }
}
