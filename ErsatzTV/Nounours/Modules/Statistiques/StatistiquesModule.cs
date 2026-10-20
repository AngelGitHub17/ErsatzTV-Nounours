using ErsatzTV.Plugins;
using MudBlazor;

namespace ErsatzTV.Nounours.Modules.Statistiques;

/// <summary>
///     Module intégré d'exemple : montre comment un module ajoute un lien de menu, une page et une carte
///     sur le tableau de bord. Un module externe s'écrit exactement de la même façon (voir plugins-exemple/).
/// </summary>
public sealed class StatistiquesModule : IErsatzPlugin
{
    public string Id => "nounours.statistiques";
    public string Name => "Statistiques de diffusion";
    public string Description => "Répartition des programmes (films, épisodes, clips…) diffusés sur les prochaines 24 h, par chaîne.";
    public string Version => "1.0.0";
    public string Author => "NounoursCorp";
    public string Icon => Icons.Material.Filled.Insights;

    public IEnumerable<PluginNavItem> NavItems => [new("Statistiques", "modules/statistiques")];

    public IEnumerable<PluginDashboardWidget> DashboardWidgets =>
        [new("Les prochaines 24 h", typeof(StatistiquesWidget), Order: 10, Columns: 6)];
}
