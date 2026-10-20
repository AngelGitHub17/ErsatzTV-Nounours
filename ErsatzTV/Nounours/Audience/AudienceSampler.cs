using ErsatzTV.Application.Nounours;
using ErsatzTV.Core;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Nounours.Audience;

/// <summary>
///     Toutes les minutes : relève le nombre de spectateurs de chaque chaîne et le programme en cours,
///     et cumule le tout dans le fichier du jour (<c>audience/AAAA-MM-JJ.json</c>).
/// </summary>
public sealed class AudienceSampler(
    AudienceTracker tracker,
    SystemStartup systemStartup,
    IServiceScopeFactory scopeFactory,
    ILogger<AudienceSampler> logger) : BackgroundService
{
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static AudienceDay _today;

    /// <summary>Journée en cours (copie à jour, sans attendre l'écriture du fichier).</summary>
    public static AudienceDay Today
    {
        get
        {
            DateOnly date = DateOnly.FromDateTime(DateTime.Now);
            Lock.Wait();
            try
            {
                AudienceDay day = _today;
                return day is not null && day.DateValue == date
                    ? System.Text.Json.JsonSerializer.Deserialize<AudienceDay>(System.Text.Json.JsonSerializer.Serialize(day))
                    : AudienceStore.Load(date);
            }
            finally
            {
                Lock.Release();
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await systemStartup.WaitForDatabase(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                try
                {
                    await Sample(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Relevé d'audience impossible");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // arrêt
        }
    }

    private async Task Sample(CancellationToken cancellationToken)
    {
        Dictionary<string, List<string>> active = tracker.Active();
        DateTime now = DateTime.Now;
        DateOnly date = DateOnly.FromDateTime(now);

        await Lock.WaitAsync(cancellationToken);
        try
        {
            if (_today is null || _today.DateValue != date)
            {
                _today = AudienceStore.Load(date);
            }

            if (active.Count == 0)
            {
                return;
            }

            using IServiceScope scope = scopeFactory.CreateScope();
            IDbContextFactory<TvContext> dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TvContext>>();
            IGuideService guideService = scope.ServiceProvider.GetRequiredService<IGuideService>();

            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var channels = await dbContext.Channels.AsNoTracking()
                .Select(c => new { c.Id, c.Number, c.Name })
                .ToListAsync(cancellationToken);

            Dictionary<int, NowNext> nowNext = await guideService.GetNowNext(cancellationToken);

            foreach ((string number, List<string> viewers) in active)
            {
                var channel = channels.Find(c => c.Number == number);
                if (channel is null)
                {
                    continue;
                }

                if (!_today.Channels.TryGetValue(number, out AudienceChannelDay day))
                {
                    _today.Channels[number] = day = new AudienceChannelDay();
                }

                day.Name = channel.Name;
                day.ViewerMinutes[now.Hour] += viewers.Count;
                day.Peak[now.Hour] = Math.Max(day.Peak[now.Hour], viewers.Count);
                day.Viewers.UnionWith(viewers);

                if (nowNext.TryGetValue(channel.Id, out NowNext info))
                {
                    foreach (GuideEntry entry in info.Now)
                    {
                        string title = entry.IsFiller ? "(intercalaires)" : entry.DisplayTitle ?? "(sans titre)";
                        day.Programmes[title] = day.Programmes.GetValueOrDefault(title) + viewers.Count;
                    }
                }
            }

            AudienceStore.Save(_today);
        }
        finally
        {
            Lock.Release();
        }
    }
}
