using ErsatzTV.Plugins;
using MudBlazor;

namespace ErsatzTV.Nounours.Modules.Audience;

/// <summary>Module intégré : spectateurs en direct et audience par heure, par chaîne et par programme.</summary>
public sealed class AudienceModule : IErsatzPlugin
{
    public string Id => "nounours.audience";
    public string Name => "Audience";
    public string Description => "Spectateurs en direct, audience heure par heure, programmes les plus regardés (relevé anonyme chaque minute).";
    public string Version => "1.0.0";
    public string Author => "NounoursCorp";
    public string Icon => Icons.Material.Filled.Groups;

    public IEnumerable<PluginNavItem> NavItems => [new("Audience", "modules/audience")];

    public IEnumerable<PluginDashboardWidget> DashboardWidgets =>
        [new("Audience", typeof(AudienceWidget), Order: 5, Columns: 6)];
}
