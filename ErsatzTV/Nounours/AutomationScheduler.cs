using System.Globalization;
using ErsatzTV.Application.Nounours;
using ErsatzTV.Core;

namespace ErsatzTV.Nounours;

/// <summary>Toutes les minutes : chaînes éphémères, rendez-vous d'antenne, carton « Ce soir », bande-annonce de la semaine, météo, publication du guide.</summary>
public sealed class AutomationScheduler(
    SystemStartup systemStartup,
    IServiceScopeFactory scopeFactory,
    ILogger<AutomationScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await systemStartup.WaitForDatabase(stoppingToken);
            await systemStartup.WaitForSearchIndex(stoppingToken);
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
                    await Tick(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Services automatiques : erreur");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // arrêt
        }
    }

    private DateTimeOffset _journalUntil = DateTimeOffset.Now.AddMinutes(-1);

    private async Task Tick(CancellationToken cancellationToken)
    {
        NounoursSettings settings = NounoursSettings.Load();
        using IServiceScope scope = scopeFactory.CreateScope();
        IAutomationService automation = scope.ServiceProvider.GetRequiredService<IAutomationService>();

        // journal de diffusion : ce qui a commencé depuis la dernière minute
        DateTimeOffset now = DateTimeOffset.Now;
        try
        {
            await scope.ServiceProvider.GetRequiredService<IJournalService>().Record(_journalUntil, now, cancellationToken);
            _journalUntil = now;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Journal de diffusion : erreur");
        }

        // titres de l'info lus sur vos sites / flux RSS
        if (!string.IsNullOrWhiteSpace(settings.Info.Feeds) &&
            (settings.Info.FeedsAt is null || now - settings.Info.FeedsAt >= TimeSpan.FromMinutes(Math.Max(2, settings.Info.FeedMinutes))))
        {
            await scope.ServiceProvider.GetRequiredService<IInfoService>().RefreshFeeds(cancellationToken);
        }

        // programme d'EBS dans le guide, toutes les heures (et après chaque reconstruction de grille)
        if (settings.Info.EbsGuide && settings.Info.Sources.Any(InfoService.IsEbs) &&
            (settings.Info.EbsGuideAt is null || now - settings.Info.EbsGuideAt >= TimeSpan.FromMinutes(60)))
        {
            await scope.ServiceProvider.GetRequiredService<IInfoService>().RefreshEbsGuide(cancellationToken);
        }

        if (settings.EphemeralChannels.Count > 0)
        {
            await automation.DeleteExpiredChannels(cancellationToken);
        }

        TonightSettings tonight = settings.Tonight;
        if (tonight.Enabled &&
            TimeSpan.TryParse(tonight.At, CultureInfo.InvariantCulture, out TimeSpan at) &&
            DateTime.Now.TimeOfDay >= at &&
            tonight.LastMade != DateOnly.FromDateTime(DateTime.Now))
        {
            await automation.MakeTonight(cancellationToken);
        }

        await automation.RunAppointments(cancellationToken);

        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        WeeklySettings weekly = settings.Weekly;
        if (weekly.Enabled && DateTime.Now.DayOfWeek == weekly.Day &&
            TimeSpan.TryParse(weekly.At, CultureInfo.InvariantCulture, out TimeSpan weeklyAt) &&
            DateTime.Now.TimeOfDay >= weeklyAt && weekly.LastMade != today)
        {
            await automation.MakeWeekly(cancellationToken);
        }

        WeatherSettings weather = settings.Weather;
        if (weather.Enabled &&
            TimeSpan.TryParse(weather.At, CultureInfo.InvariantCulture, out TimeSpan weatherAt) &&
            DateTime.Now.TimeOfDay >= weatherAt && weather.LastMade != today)
        {
            try
            {
                await automation.MakeWeather(cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning("{Message}", ex.Message);
            }
        }

        // l'essentiel du jour : aux heures choisies, avec les titres du moment
        EssentialSettings essential = settings.Essential;
        if (essential.Enabled)
        {
            DateTimeOffset due = AutomationService.ParseTimes(essential.At)
                .Select(t => new DateTimeOffset(DateTime.Today + t, DateTimeOffset.Now.Offset))
                .Where(t => t <= DateTimeOffset.Now)
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Max();
            if (due > DateTimeOffset.MinValue && (essential.LastMadeAt is null || essential.LastMadeAt < due))
            {
                await automation.MakeEssential(cancellationToken);
            }
        }

        // sagas : cartons des soirées spéciales, bande-annonce, anniversaire de sortie
        try
        {
            await scope.ServiceProvider.GetRequiredService<SagaEventService>().Tick(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sagas : erreur");
        }

        PublishSettings publish = settings.Publish;
        if (publish.Enabled && !string.IsNullOrWhiteSpace(publish.Url) &&
            (publish.LastSent is null || DateTimeOffset.Now - publish.LastSent >= TimeSpan.FromMinutes(Math.Max(5, publish.IntervalMinutes))))
        {
            await automation.PublishGuide(cancellationToken);
        }
    }
}
