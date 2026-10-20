using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using ErsatzTV.Plugins;

namespace ErsatzTV.Nounours.Plugins;

/// <summary>
///     Découvre et charge les modules :
///     - intégrés : types IErsatzPlugin de l'assembly principale ;
///     - externes : &lt;plugins&gt;/&lt;NomDuModule&gt;/*.dll (une DLL contenant un IErsatzPlugin par dossier).
///     L'état activé/désactivé est enregistré dans &lt;plugins&gt;/plugins.json.
/// </summary>
public sealed class PluginRegistry : IPluginRegistry
{
    private const string StateFileName = "plugins.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly List<LoadedPlugin> _plugins = [];
    private readonly List<PluginLoadError> _errors = [];
    private readonly System.Collections.Generic.HashSet<string> _disabled;

    private PluginRegistry(string pluginsFolder)
    {
        PluginsFolder = pluginsFolder;
        _disabled = LoadState(pluginsFolder);
    }

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    public IReadOnlyList<PluginLoadError> Errors => _errors;

    public IEnumerable<Assembly> RoutableAssemblies =>
        _plugins.Where(p => p.IsEnabled && !p.IsBuiltIn).Select(p => p.Assembly).Distinct();

    public string PluginsFolder { get; }

    public async Task SetEnabledAsync(string pluginId, bool enabled)
    {
        if (enabled)
        {
            _disabled.Remove(pluginId);
        }
        else
        {
            _disabled.Add(pluginId);
        }

        Directory.CreateDirectory(PluginsFolder);
        var state = new PluginState { Disabled = _disabled.Order().ToList() };
        await File.WriteAllTextAsync(
            Path.Combine(PluginsFolder, StateFileName),
            JsonSerializer.Serialize(state, JsonOptions));
    }

    public bool IsDisabledInState(string pluginId) => _disabled.Contains(pluginId);

    public static PluginRegistry Load(string pluginsFolder, Assembly hostAssembly)
    {
        var registry = new PluginRegistry(pluginsFolder);

        registry.LoadFromAssembly(hostAssembly, "intégré", isBuiltIn: true);

        if (Directory.Exists(pluginsFolder))
        {
            foreach (string folder in Directory.GetDirectories(pluginsFolder))
            {
                foreach (string dll in Directory.GetFiles(folder, "*.dll"))
                {
                    if (!LooksLikePlugin(dll))
                    {
                        continue;
                    }

                    try
                    {
                        Assembly assembly = LoadAssembly(dll);
                        registry.LoadFromAssembly(assembly, dll, isBuiltIn: false);
                    }
                    catch (Exception ex)
                    {
                        registry._errors.Add(new PluginLoadError(dll, ex.Message));
                    }
                }
            }
        }

        return registry;
    }

    private void LoadFromAssembly(Assembly assembly, string source, bool isBuiltIn)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            _errors.Add(
                new PluginLoadError(
                    source,
                    string.Join("; ", ex.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message).Distinct())));
            types = ex.Types.Where(t => t is not null).ToArray()!;
        }

        foreach (Type type in types.Where(t =>
                     t is { IsClass: true, IsAbstract: false, IsPublic: true } &&
                     typeof(IErsatzPlugin).IsAssignableFrom(t)))
        {
            try
            {
                if (Activator.CreateInstance(type) is not IErsatzPlugin plugin)
                {
                    continue;
                }

                if (_plugins.Any(p => p.Plugin.Id == plugin.Id))
                {
                    _errors.Add(new PluginLoadError(source, $"Identifiant de module en double : {plugin.Id}"));
                    continue;
                }

                _plugins.Add(new LoadedPlugin(plugin, assembly, source, isBuiltIn, !_disabled.Contains(plugin.Id)));
            }
            catch (Exception ex)
            {
                _errors.Add(new PluginLoadError(source, $"{type.FullName} : {ex.Message}"));
            }
        }
    }

    private static Assembly LoadAssembly(string path)
    {
        // déjà chargée (même nom) ? on réutilise, pour éviter les doublons de types
        AssemblyName name = AssemblyName.GetAssemblyName(path);
        Assembly existing = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a =>
            string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        string folder = Path.GetDirectoryName(path)!;
        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            string candidate = Path.Combine(folder, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };

        return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }

    // ignore les dépendances copiées à côté du module et déjà fournies par ErsatzTV (MudBlazor, ErsatzTV.Plugins, ...)
    private static bool LooksLikePlugin(string dll)
    {
        string fileName = Path.GetFileName(dll);
        return !File.Exists(Path.Combine(AppContext.BaseDirectory, fileName));
    }

    private static System.Collections.Generic.HashSet<string> LoadState(string pluginsFolder)
    {
        try
        {
            string file = Path.Combine(pluginsFolder, StateFileName);
            if (File.Exists(file))
            {
                PluginState state = JsonSerializer.Deserialize<PluginState>(File.ReadAllText(file));
                return new System.Collections.Generic.HashSet<string>(state?.Disabled ?? []);
            }
        }
        catch
        {
            // fichier illisible : tout est activé
        }

        return [];
    }

    private sealed class PluginState
    {
        public List<string> Disabled { get; set; } = [];
    }
}
