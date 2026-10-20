using ErsatzTV.Application.Playouts;
using ErsatzTV.Application.ProgramSchedules;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Nounours;

public sealed record WeekScheduleOption(int Id, string Name, int Items);

/// <summary>
///     Semaine type d'une chaîne : quel planning est utilisé chaque jour de la semaine.
///     <paramref name="HasAdvancedRules" /> = la chaîne a aussi des règles par date / mois (mode expert) : elles sont conservées
///     et restent prioritaires.
/// </summary>
public sealed record WeekTemplate(
    int PlayoutId,
    int ChannelId,
    string ChannelNumber,
    string ChannelName,
    int DefaultScheduleId,
    Dictionary<DayOfWeek, int> Days,
    bool HasAdvancedRules);

public interface IWeekTemplateService
{
    /// <summary>Chaînes à planning classique (les seules qui acceptent une semaine type).</summary>
    Task<List<WeekTemplate>> GetAll(CancellationToken cancellationToken);

    Task<List<WeekScheduleOption>> GetSchedules(CancellationToken cancellationToken);

    /// <summary>Enregistre la semaine type et reconstruit la grille de la chaîne.</summary>
    Task<Either<BaseError, Unit>> Save(int playoutId, Dictionary<DayOfWeek, int> days, CancellationToken cancellationToken);

    /// <summary>Copie un planning (ex. pour faire une version « week-end ») et renvoie le nouveau.</summary>
    Task<Either<BaseError, WeekScheduleOption>> Duplicate(int scheduleId, string name, CancellationToken cancellationToken);
}

public sealed class WeekTemplateService(
    IDbContextFactory<TvContext> dbContextFactory,
    IMediator mediator,
    System.Threading.Channels.ChannelWriter<IBackgroundServiceRequest> workerChannel) : IWeekTemplateService
{
    public static readonly DayOfWeek[] Week =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    public async Task<List<WeekTemplate>> GetAll(CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        List<Playout> playouts = await dbContext.Playouts
            .AsNoTracking()
            .Include(p => p.Channel)
            .Include(p => p.ProgramScheduleAlternates)
            .Where(p => p.ScheduleKind == PlayoutScheduleKind.Classic && p.ProgramScheduleId != null)
            .ToListAsync(cancellationToken);

        return playouts
            .Select(ToTemplate)
            .OrderBy(t => decimal.TryParse(t.ChannelNumber, System.Globalization.CultureInfo.InvariantCulture, out decimal n) ? n : decimal.MaxValue)
            .ToList();
    }

    public async Task<List<WeekScheduleOption>> GetSchedules(CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.ProgramSchedules
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new WeekScheduleOption(s.Id, s.Name, s.Items.Count))
            .ToListAsync(cancellationToken);
    }

    public async Task<Either<BaseError, Unit>> Save(
        int playoutId,
        Dictionary<DayOfWeek, int> days,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Playout playout = await dbContext.Playouts
            .AsNoTracking()
            .Include(p => p.ProgramScheduleAlternates)
            .FirstOrDefaultAsync(p => p.Id == playoutId, cancellationToken);

        if (playout?.ProgramScheduleId is null)
        {
            return BaseError.New("Chaîne introuvable ou sans planning classique");
        }

        // planning par défaut = le plus utilisé de la semaine (à égalité : l'actuel)
        int defaultId = days.Values
            .GroupBy(id => id)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key == playout.ProgramScheduleId)
            .First().Key;

        var items = new List<ReplacePlayoutAlternateSchedule>();
        var index = 1;

        // règles avancées (dates, mois) : conservées telles quelles, en tête (prioritaires)
        foreach (ProgramScheduleAlternate advanced in playout.ProgramScheduleAlternates
                     .Where(IsAdvanced)
                     .OrderBy(a => a.Index))
        {
            items.Add(
                new ReplacePlayoutAlternateSchedule(
                    advanced.Id,
                    index++,
                    advanced.ProgramScheduleId,
                    advanced.DaysOfWeek.ToList(),
                    advanced.DaysOfMonth.ToList(),
                    advanced.MonthsOfYear.ToList(),
                    advanced.LimitToDateRange,
                    advanced.StartMonth,
                    advanced.StartDay,
                    advanced.StartYear,
                    advanced.EndMonth,
                    advanced.EndDay,
                    advanced.EndYear));
        }

        foreach (IGrouping<int, KeyValuePair<DayOfWeek, int>> group in days
                     .Where(d => d.Value != defaultId)
                     .GroupBy(d => d.Value))
        {
            items.Add(Rule(index++, group.Key, group.Select(d => d.Key).ToList()));
        }

        // dernier élément = planning par défaut (tous les jours)
        items.Add(Rule(index, defaultId, AlternateScheduleSelector.AllDaysOfWeek()));

        Either<BaseError, Unit> result = await mediator.Send(
            new ReplacePlayoutAlternateScheduleItems(playoutId, items),
            cancellationToken);

        if (result.IsRight)
        {
            await workerChannel.WriteAsync(new BuildPlayout(playoutId, PlayoutBuildMode.Reset), cancellationToken);
        }

        return result;
    }

    public async Task<Either<BaseError, WeekScheduleOption>> Duplicate(
        int scheduleId,
        string name,
        CancellationToken cancellationToken)
    {
        Either<BaseError, ProgramScheduleViewModel> copy = await mediator.Send(
            new CopyProgramSchedule(scheduleId, name),
            cancellationToken);

        return copy.Map(vm => new WeekScheduleOption(vm.Id, vm.Name, 0));
    }

    private static ReplacePlayoutAlternateSchedule Rule(int index, int scheduleId, List<DayOfWeek> days) =>
        new(
            0,
            index,
            scheduleId,
            days,
            AlternateScheduleSelector.AllDaysOfMonth(),
            AlternateScheduleSelector.AllMonthsOfYear(),
            false,
            1,
            1,
            null,
            12,
            31,
            null);

    private static bool IsAdvanced(ProgramScheduleAlternate alternate) =>
        alternate.LimitToDateRange ||
        alternate.DaysOfMonth.Count is > 0 and < 31 ||
        alternate.MonthsOfYear.Count is > 0 and < 12;

    private static WeekTemplate ToTemplate(Playout playout)
    {
        var simple = playout.ProgramScheduleAlternates.Where(a => !IsAdvanced(a)).ToList();
        var days = new Dictionary<DayOfWeek, int>();
        DateTimeOffset today = DateTimeOffset.Now.Date;
        foreach (DayOfWeek day in Week)
        {
            // prochaine date qui tombe ce jour-là
            DateTimeOffset date = today.AddDays(((int)day - (int)today.DayOfWeek + 7) % 7).AddHours(12);
            days[day] = AlternateScheduleSelector.GetScheduleForDate(simple, date)
                .Match(a => a.ProgramScheduleId, playout.ProgramScheduleId ?? 0);
        }

        return new WeekTemplate(
            playout.Id,
            playout.ChannelId,
            playout.Channel.Number,
            playout.Channel.Name,
            playout.ProgramScheduleId ?? 0,
            days,
            playout.ProgramScheduleAlternates.Any(IsAdvanced));
    }
}
