# Créer un module pour ErsatzTV (édition Nounours)

Un module est une bibliothèque Razor (.NET 10) qui contient une classe implémentant `ErsatzTV.Plugins.IErsatzPlugin`.
Il peut ajouter :

- des **pages** (tout composant avec `@page "/modules/…"` est routé automatiquement),
- des **entrées de menu** (section « Modules »),
- des **cartes** sur le tableau de bord,
- des **services** dans l'injection de dépendances,
- du **code au démarrage** (base de données prête).

## En 5 minutes

1. Copiez `plugins-exemple/ErsatzTV.Plugin.Exemple` et renommez le projet.
2. Modifiez la classe principale :

```csharp
public sealed class MonModule : IErsatzPlugin
{
    public string Id => "moi.mon-module";          // unique et stable
    public string Name => "Mon module";
    public string Description => "Ce que fait mon module.";
    public string Version => "1.0.0";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<MonService>();

    public IEnumerable<PluginNavItem> NavItems => [new("Mon module", "modules/mon-module")];

    public IEnumerable<PluginDashboardWidget> DashboardWidgets =>
        [new("Ma carte", typeof(MaCarte), Order: 50, Columns: 6)];   // Columns sur 12
}
```

3. Ajoutez vos pages :

```razor
@page "/modules/mon-module"
@inject ErsatzTV.Application.Nounours.IGuideService Guide

<div class="n-page">
    <div class="n-page-header"><div><h1>Mon module</h1></div></div>
    <div class="n-card">…</div>
</div>
```

4. Compilez : `dotnet build -c Release`
5. Copiez la DLL du module dans `<dossier de config ErsatzTV>/plugins/MonModule/` et redémarrez ErsatzTV.
   La page **Modules** liste les modules chargés, permet de les désactiver et affiche les erreurs de chargement.

## Accéder aux données d'ErsatzTV

Dans vos pages et services, vous pouvez injecter tout ce qu'ErsatzTV enregistre :

| Service | Usage |
|---|---|
| `IMediator` | toutes les requêtes et commandes d'ErsatzTV (`GetAllChannels`, `CreateChannel`, `BuildPlayout`…) |
| `ErsatzTV.Application.Nounours.IGuideService` | programmes entre deux dates, « en cours / à suivre », statistiques |
| `ErsatzTV.Application.Nounours.IContentCatalog` | tout le contenu programmable (séries, collections, playlists…) |
| `ErsatzTV.Application.Nounours.IHabillageService` | lire / créer / appliquer des habillages |
| `IDbContextFactory<TvContext>` | accès direct à la base (avancé) |
| `IPluginRegistry` | liste des modules chargés |

Pour ces services, référencez aussi `ErsatzTV.Application` (avec `Private="false"`, comme `ErsatzTV.Plugins` dans l'exemple)
afin de ne pas copier ses DLL à côté du module : elles sont fournies par ErsatzTV.

## Styles disponibles

Les classes de l'interface sont utilisables telles quelles : `n-page`, `n-page-header`, `n-card`, `n-card-title`, `n-muted`,
`n-empty`, `n-stat`, `n-stat-value`, `n-stat-label`, ainsi que tous les composants MudBlazor.

## Bon à savoir

- Les modules s'exécutent dans le même processus qu'ErsatzTV, avec les mêmes droits : n'installez que des modules de confiance.
- Les DLL déjà fournies par ErsatzTV (MudBlazor, ErsatzTV.*…) présentes dans le dossier d'un module sont ignorées.
- Un module intégré (dans le code d'ErsatzTV) s'écrit exactement pareil : voir `ErsatzTV/Nounours/Modules/Statistiques/`.
