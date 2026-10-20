using System.Reflection;

namespace ErsatzTV.Plugins;

/// <summary>Informations sur un module chargé.</summary>
public sealed record LoadedPlugin(
    IErsatzPlugin Plugin,
    Assembly Assembly,
    string Source,
    bool IsBuiltIn,
    bool IsEnabled);

/// <summary>Échec de chargement d'un module (affiché dans la page Modules).</summary>
public sealed record PluginLoadError(string Source, string Message);

/// <summary>Accès en lecture aux modules chargés. Injectable dans n'importe quel composant.</summary>
public interface IPluginRegistry
{
    IReadOnlyList<LoadedPlugin> Plugins { get; }

    IReadOnlyList<PluginLoadError> Errors { get; }

    IEnumerable<LoadedPlugin> Enabled => Plugins.Where(p => p.IsEnabled);

    /// <summary>Assemblies à router (modules externes activés).</summary>
    IEnumerable<Assembly> RoutableAssemblies { get; }

    /// <summary>Active ou désactive un module (pris en compte au prochain redémarrage).</summary>
    Task SetEnabledAsync(string pluginId, bool enabled);

    /// <summary>Dossier où déposer les modules externes.</summary>
    string PluginsFolder { get; }
}
