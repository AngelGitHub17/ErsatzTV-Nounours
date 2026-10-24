using System.Globalization;
using ErsatzTV.Application.Nounours;
using ErsatzTV.Application.ProgramSchedules;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Nounours;

public enum RecurringMode
{
    /// <summary>Un film à chaque fois, dans l'ordre de sortie (la saga continue la fois suivante).</summary>
    One,

    /// <summary>Deux films à la suite.</summary>
    TwoFilms,

    /// <summary>Marathon : les films à la suite jusqu'au rendez-vous suivant de la grille.</summary>
    Marathon
}

/// <summary>Un rendez-vous qui revient : « tous les vendredis à 20h45, la saga Harry Potter ».</summary>
public sealed class RecurringSlot
{
    public int ChannelId { get; set; }
    public List<DayOfWeek> Days { get; set; } = [DayOfWeek.Friday];
    public TimeSpan Start { get; set; } = new(20, 45, 0);
    public int SmartCollectionId { get; set; }
    public string CollectionName { get; set; } = string.Empty;
    public RecurringMode Mode { get; set; } = RecurringMode.One;
    public string Label { get; set; } = string.Empty;

    /// <summary>Seulement pendant cette période (ex. décembre) ; vide = toute l'année.</summary>
    public DateOnly? From { get; set; }

    public DateOnly? Until { get; set; }

    /// <summary>Soirée spéciale : carton d'ouverture avec le film du soir, « la suite… » à la fin.</summary>
    public bool Special { get; set; }
}

/// <summary>
///     Ajoute un rendez-vous à certains jours de la semaine d'une chaîne, sans toucher aux autres jours : le planning
///     de ces jours est modifié s'il ne sert qu'à eux, sinon il est copié (« … · vendredi ») et la semaine type mise à
///     jour. Le rendez-vous remplace ce qui commençait à la même heure (ex. le film du soir de 20h45).
/// </summary>
public sealed class RecurringSlotService(
    IMediator mediator,
    IDbContextFactory<TvContext> dbContextFactory,
    IWeekTemplateService weekTemplates,
    ChannelWizardService wizard)
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static string DaysLabel(IReadOnlyCollection<DayOfWeek> days)
    {
        var set = days.ToHashSet();
        if (set.Count is 0 or 7)
        {
            return "tous les jours";
        }

        if (set.SetEquals([DayOfWeek.Saturday, DayOfWeek.Sunday]))
        {
            return "le week-end";
        }

        if (set.SetEquals([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]))
        {
            return "en semaine";
        }

        IEnumerable<string> names = WeekTemplateService.Week.Where(set.Contains).Select(d => Fr.DateTimeFormat.GetDayName(d));
        return set.Count == 1 ? $"le {names.First()}" : string.Join(", ", names);
    }

    public async Task<Either<BaseError, List<string>>> Add(RecurringSlot slot, CancellationToken cancellationToken)
    {
        if (slot.SmartCollectionId <= 0)
        {
            return BaseError.New("Choisissez la collection (saga ou époque) à programmer.");
        }

        List<DayOfWeek> days = slot.Days.Count == 0 ? [.. WeekTemplateService.Week] : slot.Days.Distinct().ToList();
        WeekTemplate template = (await weekTemplates.GetAll(cancellationToken)).FirstOrDefault(t => t.ChannelId == slot.ChannelId);
        if (template is null)
        {
            return BaseError.New("Cette chaîne n'a pas de planning classique : la programmation par jour n'est pas possible (mode expert).");
        }

        var steps = new List<string>();
        string label = string.IsNullOrWhiteSpace(slot.Label) ? slot.CollectionName : slot.Label.Trim();
        string when = $"{DaysLabel(days)} à {slot.Start:hh\\:mm}";
        Dictionary<int, string> names = (await weekTemplates.GetSchedules(cancellationToken)).ToDictionary(s => s.Id, s => s.Name);

        // pendant une période : toujours une copie, réservée à ces jours et à ces dates
        if (slot.From is { } from)
        {
            DateOnly until = slot.Until ?? from;
            foreach (IGrouping<int, DayOfWeek> group in days.GroupBy(d => template.Days[d]))
            {
                Either<BaseError, int> copy = await Copy(group.Key, $"{names.GetValueOrDefault(group.Key, "Planning")} · {label} ({from:dd/MM}–{until:dd/MM})", cancellationToken);
                foreach (BaseError error in copy.LeftToSeq())
                {
                    return error;
                }

                int target = copy.RightToSeq().Head();
                await PutSlot(target, slot, label, cancellationToken);
                Either<BaseError, Unit> saved = await weekTemplates.AddPeriod(template.PlayoutId, target, group.ToList(), from, until, cancellationToken);
                foreach (BaseError error in saved.LeftToSeq())
                {
                    return error;
                }
            }

            steps.Add($"« {label} » {when}, du {from.ToString("d MMMM", Fr)} au {until.ToString("d MMMM yyyy", Fr)}");
            steps.Add("Hors de cette période, la grille habituelle ne change pas");
            return steps;
        }

        var newDays = new Dictionary<DayOfWeek, int>(template.Days);
        foreach (IGrouping<int, DayOfWeek> group in days.GroupBy(d => template.Days[d]))
        {
            int source = group.Key;
            bool onlyThese = template.Days.Where(kv => kv.Value == source).All(kv => days.Contains(kv.Key));
            int target = source;
            if (!onlyThese)
            {
                // le planning sert aussi à d'autres jours : copie pour ces jours-là
                Either<BaseError, int> copy = await Copy(source, $"{names.GetValueOrDefault(source, "Planning")} · {DaysLabel(group.ToList())}", cancellationToken);
                foreach (BaseError error in copy.LeftToSeq())
                {
                    return error;
                }

                target = copy.RightToSeq().Head();
                foreach (DayOfWeek day in group)
                {
                    newDays[day] = target;
                }

                steps.Add($"Planning « {names.GetValueOrDefault(source, "?")} » copié pour {DaysLabel(group.ToList())}");
            }

            string replaced = await PutSlot(target, slot, label, cancellationToken);
            if (!string.IsNullOrEmpty(replaced))
            {
                steps.Add($"Remplace « {replaced} » de {slot.Start:hh\\:mm} {DaysLabel(group.ToList())}");
            }
        }

        Either<BaseError, Unit> result = await weekTemplates.Save(template.PlayoutId, newDays, cancellationToken);
        foreach (BaseError error in result.LeftToSeq())
        {
            return error;
        }

        steps.Insert(0, $"« {label} » {when} sur {template.ChannelName}");
        if (slot.Special)
        {
            NounoursSettings.Update(s =>
            {
                s.Sagas.Evenings.RemoveAll(e => e.SmartCollectionId == slot.SmartCollectionId && e.ChannelId == slot.ChannelId);
                s.Sagas.Evenings.Add(new SagaEvening
                {
                    SmartCollectionId = slot.SmartCollectionId, Name = slot.CollectionName.Replace(MovieCollectionService.SagaPrefix, string.Empty, StringComparison.Ordinal),
                    ChannelId = slot.ChannelId, Days = days, At = slot.Start.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
                });
                s.Sagas.LastMade = null; // cartons fabriqués tout de suite
            });
            steps.Add("Soirée spéciale : les cartons « Soirée spéciale » et « La suite » se fabriquent maintenant, puis chaque matin");
        }

        steps.Add(slot.Mode switch
        {
            RecurringMode.One => "Un film à chaque fois, dans l'ordre de sortie : la saga continue la fois suivante",
            RecurringMode.TwoFilms => "Deux films à chaque fois, dans l'ordre de sortie",
            _ => "Marathon : les films s'enchaînent jusqu'au rendez-vous suivant"
        });
        return steps;
    }

    private async Task<Either<BaseError, int>> Copy(int scheduleId, string name, CancellationToken cancellationToken)
    {
        await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // une ancienne copie du même nom n'est plus à jour : on la retire si plus rien ne s'en sert, sinon nouveau nom
        string unique = name;
        for (var n = 2; ; n++)
        {
            string candidate = unique;
            ProgramSchedule old = await db.ProgramSchedules.FirstOrDefaultAsync(s => s.Name == candidate, cancellationToken);
            if (old is null)
            {
                break;
            }

            bool used = await db.Playouts.AnyAsync(p => p.ProgramScheduleId == old.Id, cancellationToken) ||
                        await db.ProgramScheduleAlternates.AnyAsync(a => a.ProgramScheduleId == old.Id, cancellationToken);
            if (!used)
            {
                List<ProgramScheduleItem> oldItems = await db.ProgramScheduleItems.Where(i => i.ProgramScheduleId == old.Id).ToListAsync(cancellationToken);
                db.ProgramScheduleItems.RemoveRange(oldItems);
                db.ProgramSchedules.Remove(old);
                await db.SaveChangesAsync(cancellationToken);
                break;
            }

            unique = $"{name} ({n})";
        }

        name = unique;

        Either<BaseError, WeekScheduleOption> copy = await weekTemplates.Duplicate(scheduleId, name, cancellationToken);
        return copy.Map(c => c.Id);
    }

    /// <summary>Met le rendez-vous dans un planning ; renvoie le nom de ce qu'il remplace (même heure de début).</summary>
    private async Task<string> PutSlot(int scheduleId, RecurringSlot slot, string label, CancellationToken cancellationToken)
    {
        string replaced = null;
        int? pre = null, post = null, tail = null;
        await using (TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            List<ProgramScheduleItem> items = await db.ProgramScheduleItems
                .Include(i => i.SmartCollection)
                .Include(i => i.Collection)
                .Where(i => i.ProgramScheduleId == scheduleId)
                .ToListAsync(cancellationToken);
            List<ProgramScheduleItem> same = items.Where(i => i.StartType == StartType.Fixed && i.StartTime == slot.Start).ToList();
            ProgramScheduleItem model = same.FirstOrDefault()
                                        ?? items.Where(i => i.StartType == StartType.Fixed && i.StartTime < slot.Start).MaxBy(i => i.StartTime)
                                        ?? items.MinBy(i => i.Index);
            if (model is not null)
            {
                // mêmes jingles et pubs que le rendez-vous remplacé (ou que celui d'avant)
                (pre, post, tail) = (model.PreRollFillerId, model.PostRollFillerId, model.TailFillerId);
            }

            if (same.Count > 0)
            {
                ProgramScheduleItem first = same[0];
                replaced = !string.IsNullOrWhiteSpace(first.CustomTitle) ? first.CustomTitle : first.SmartCollection?.Name ?? first.Collection?.Name ?? "programme";
                db.ProgramScheduleItems.RemoveRange(same);
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        var wizardSlot = new WizardSlot
        {
            Kind = ContentSourceKind.SmartCollection,
            ContentId = slot.SmartCollectionId,
            ContentName = slot.CollectionName,
            FixedStart = true,
            StartTime = slot.Start,
            Mode = slot.Mode switch
            {
                RecurringMode.One => WizardSlotMode.One,
                RecurringMode.TwoFilms => WizardSlotMode.Multiple,
                _ => WizardSlotMode.Flood
            },
            Count = 2,
            Order = PlaybackOrder.Chronological,
            CustomTitle = label
        };
        Either<BaseError, ProgramScheduleItemViewModel> added = await mediator.Send(ChannelWizardService.ToScheduleItem(scheduleId, wizardSlot, Option<ProgrammeScheduling>.None), cancellationToken);
        foreach (ProgramScheduleItemViewModel vm in added.RightToSeq())
        {
            await using TvContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            ProgramScheduleItem item = await db.ProgramScheduleItems.FirstOrDefaultAsync(i => i.Id == vm.Id, cancellationToken);
            if (item is not null)
            {
                (item.PreRollFillerId, item.PostRollFillerId, item.TailFillerId) = (pre, post, tail);
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        await wizard.SortFixedItems(scheduleId, cancellationToken);
        return replaced;
    }
}
