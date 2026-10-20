using ErsatzTV.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ErsatzTV.Plugin.Exemple;

/// <summary>Point d'entrée du module : ErsatzTV le trouve tout seul au démarrage.</summary>
public sealed class ExemplePlugin : IErsatzPlugin
{
    public string Id => "exemple.bloc-notes";
    public string Name => "Bloc-notes de la régie";
    public string Description => "Module d'exemple : une page de notes partagées et une carte sur le tableau de bord.";
    public string Version => "1.0.0";
    public string Author => "Vous !";
    public string? Icon => MudBlazor.Icons.Material.Filled.StickyNote2;

    // 1. vos services
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<NotesStore>();

    // 2. vos entrées de menu (section « Modules »)
    public IEnumerable<PluginNavItem> NavItems => [new("Bloc-notes", "modules/bloc-notes")];

    // 3. vos cartes du tableau de bord
    public IEnumerable<PluginDashboardWidget> DashboardWidgets => [new("Note du jour", typeof(NoteWidget), Order: 50, Columns: 6)];
}
