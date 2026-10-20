using ErsatzTV.Core;
using ErsatzTV.Plugins;

namespace ErsatzTV.Nounours.Plugins;

/// <summary>Appelle OnStartedAsync sur chaque module activé une fois la base de données prête.</summary>
public sealed class PluginStartupService(
    IPluginRegistry registry,
    SystemStartup systemStartup,
    IServiceScopeFactory scopeFactory,
    ILogger<PluginStartupService> logger) : BackgroundService
{
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

        foreach (LoadedPlugin loaded in registry.Enabled)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                await loaded.Plugin.OnStartedAsync(scope.ServiceProvider, stoppingToken);
                logger.LogInformation("Module {Plugin} {Version} démarré", loaded.Plugin.Name, loaded.Plugin.Version);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Le module {Plugin} a échoué au démarrage", loaded.Plugin.Name);
            }
        }
    }
}
