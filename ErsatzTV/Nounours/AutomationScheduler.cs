using System.Globalization;
using ErsatzTV.Application.Nounours;
using ErsatzTV.Core;

namespace ErsatzTV.Nounours;

/// <summary>Toutes les minutes : chaînes éphémères arrivées à échéance, carton « Ce soir » à l'heure dite, publication du guide.</summary>
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

    private async Task Tick(CancellationToken cancellationToken)
    {
        NounoursSettings settings = NounoursSettings.Load();
        using IServiceScope scope = scopeFactory.CreateScope();
        IAutomationService automation = scope.ServiceProvider.GetRequiredService<IAutomationService>();

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

        PublishSettings publish = settings.Publish;
        if (publish.Enabled && !string.IsNullOrWhiteSpace(publish.Url) &&
            (publish.LastSent is null || DateTimeOffset.Now - publish.LastSent >= TimeSpan.FromMinutes(Math.Max(5, publish.IntervalMinutes))))
        {
            await automation.PublishGuide(cancellationToken);
        }
    }
}
