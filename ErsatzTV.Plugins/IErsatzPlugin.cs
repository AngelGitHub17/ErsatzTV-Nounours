using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ErsatzTV.Plugins;

/// <summary>
///     Point d'entrée d'un module. Une classe publique, non abstraite, avec un constructeur sans paramètre.
///     ErsatzTV la découvre au démarrage dans :
///     - l'assembly principale (modules intégrés),
///     - chaque sous-dossier de &lt;dossier de config&gt;/plugins/ (un dossier par module).
///     Toutes les pages Razor (@page) de l'assembly du module sont routées automatiquement.
/// </summary>
public interface IErsatzPlugin
{
    /// <summary>Identifiant unique et stable, ex. "nounours.stats".</summary>
    string Id { get; }

    /// <summary>Nom affiché dans l'interface.</summary>
    string Name { get; }

    /// <summary>Courte description affichée dans la page Modules.</summary>
    string Description { get; }

    /// <summary>Version du module, ex. "1.0.0".</summary>
    string Version { get; }

    /// <summary>Auteur (facultatif).</summary>
    string Author => string.Empty;

    /// <summary>Icône MudBlazor (chaîne SVG, ex. Icons.Material.Filled.Extension). Facultatif.</summary>
    string? Icon => null;

    /// <summary>Enregistre les services du module dans l'injection de dépendances.</summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    /// <summary>Liens ajoutés dans le menu, section « Modules ».</summary>
    IEnumerable<PluginNavItem> NavItems => [];

    /// <summary>Cartes ajoutées sur le tableau de bord.</summary>
    IEnumerable<PluginDashboardWidget> DashboardWidgets => [];

    /// <summary>Appelé une fois que l'application a démarré (base de données prête).</summary>
    Task OnStartedAsync(IServiceProvider services, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Un lien de menu. Href relatif, ex. "modules/stats".</summary>
public sealed record PluginNavItem(string Title, string Href, string? Icon = null);

/// <summary>
///     Une carte du tableau de bord. ComponentType doit être un composant Razor (ComponentBase).
///     Columns : largeur sur 12 (6 = moitié, 12 = pleine largeur).
/// </summary>
public sealed record PluginDashboardWidget(string Title, Type ComponentType, int Order = 100, int Columns = 6);
