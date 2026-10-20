# ErsatzTV — édition Nounours

Fork d'ErsatzTV Legacy (base : v26.5.1, commit `aa639375`) repensé pour être plus simple à utiliser,
entièrement en français, et facile à étendre.

## Ce qui change

### Une interface repensée
- **Menu réorganisé** par usage : Chaînes · Habillage · Programmation · Médiathèque · Collections · Sources · Modules · Réglages · Diagnostic, avec icônes.
- **Mode simple / mode expert** (interrupteur en bas du menu). Le mode simple masque les options avancées
  (blocs, décos, multi-collections, rediffusions, Trakt, préréglages de remplissage, réglages FFmpeg/HDHomeRun/journalisation).
  Le choix est enregistré en base (`pages.nounours.expert_mode`).
- **Nouvelle palette** sombre/claire, typographie et boutons sans majuscules forcées.
- **Tout en français** : les ~80 pages d'origine, les messages, les dialogues de suppression, les vérifications de santé,
  la pagination, et une langue « Français » ajoutée (par défaut) dans Réglages › Interface.

### Tableau de bord (page d'accueil)
- Chaque chaîne avec son logo, **ce qui passe maintenant** (barre de progression, temps restant) et **le programme suivant**.
- Statistiques de la médiathèque, alerte si une vérification de santé échoue, diffusions en cours.
- Bouton « Regarder » (aperçu vidéo dans le navigateur), raccourcis planning / habillage / réglages.
- Cartes ajoutées par les modules. Rafraîchissement automatique toutes les 30 s.

### Assistant de création de chaîne (`/chaines/nouvelle`)
En 4 étapes : nom → contenu & horaires → diffusion & habillage → récapitulatif.
- Choix du contenu dans **une seule liste** : séries, collections, collections intelligentes, multi-collections, playlists,
  ou préréglages (« tous les films », « tous les épisodes »…) et recherche personnalisée (crée une collection intelligente).
- Créneaux « à la suite » ou « à heure fixe », en continu / 1 programme / N programmes / pendant X minutes, ordre de lecture.
- Modèles prêts à l'emploi (100 % films, séries en continu, « séries la journée, film à 21 h », clips).
- Crée en une fois : collections intelligentes → planning + éléments → chaîne → playout (construit automatiquement) → habillage.
  **En cas d'erreur, tout ce qui a été créé est supprimé** : rien ne reste à moitié configuré.

### Planning visuel (`/planning`)
- **Vue Jour** : grille EPG de toutes les chaînes, heure par heure, ligne « maintenant », zoom, intercalaires affichables.
- **Vue Semaine** : une chaîne, 7 jours.
- Les programmes consécutifs d'une même série sont **regroupés** en un bloc lisible (« Les Nounours ×20 »).
- Clic sur un programme : détail, liste des épisodes du bloc, liens vers le planning / le playout / la chaîne.
- **Ajouter un rendez-vous** (bouton réveil, ou double-clic sur une heure en vue semaine) : ajoute un créneau à heure fixe
  au planning de la chaîne et reconstruit la grille.

### Studio d'habillage façon OBS (`/habillage`)
Composez ce qui s'affiche par-dessus les programmes, en glisser-déposer sur une scène 16:9 :
- **Horloge en direct** (nouveau type d'élément du moteur : se redessine chaque seconde ; format, fuseau, texte autour),
- **Date**, **texte libre**, **titre du programme**, **« À suivre »** (depuis le guide), **logo / image** (import direct, GIF animés),
  **bandeau** (rectangle coloré, coins arrondis) et **bas d'écran** (bandeau + texte),
- style : police (celles du dossier fonts d'ErsatzTV, prévisualisées dans le navigateur), taille, graisse, couleur, contour,
- **apparition** : tout le temps, au début / à la fin de chaque programme, ou toutes les X minutes, avec fondu,
- calques (ordre, visibilité, duplication), magnétisme, flèches du clavier, positions rapides, onglet YAML pour voir le fichier généré.

« Enregistrer » génère les fichiers YAML du moteur graphique d'ErsatzTV et une **déco** « Habillage : nom » (groupe « Habillages ») ;
« Appliquer aux chaînes » en fait la déco par défaut des chaînes choisies. Visible dès le programme suivant
(sauf en mode HLS direct, qui ne transcode pas).

### Programmes composés — conducteurs (`/programmes`)
Composez un vrai programme TV, segment par segment, comme un conducteur de régie :

```
Pubs ×3 → Jingle « Le film du soir » → LE FILM (coupé toutes les 20 min : jingle → 4 pubs → jingle) → Pubs ×3
```

- **Segments** : Pubs, Jingle, Programme principal, Bande-annonce, **Programme lié** (un autre programme inséré tel quel), Autre.
  Chaque segment a une source (série, collection, collection intelligente, playlist, **fichier précis** trouvé par son titre,
  ou recherche), un nombre d'éléments et un ordre de lecture.
- **Coupures pub pendant le programme** : toutes les X minutes ou N coupures réparties, avec « pas avant X min » et
  « pas dans les X dernières minutes ». Fonctionne **sans chapitres** dans les fichiers (nouveau dans le moteur, voir plus bas).
  Une coupure est elle-même une séquence (jingle début → pubs → jingle fin). Les pubs et jingles ne sont jamais coupés.
- **Conducteur visuel** avec la durée estimée de chaque segment et la position des coupures sur le programme.
- **Programmes liés** : « Soirée cinéma » = « Le film du soir » + « Le journal » + … ; si le programme englobant n'a pas
  ses propres coupures, il reprend celles du programme lié.
- **Guide (EPG)** : seuls les segments cochés « Guide » apparaissent (par défaut le programme principal), les pubs et jingles restent invisibles.
- Utilisable partout : comme contenu dans l'**assistant de chaîne**, dans le **planning** (rendez-vous), ou via le bouton
  **« Programmer sur une chaîne »** de l'éditeur. « Une fois » = un passage complet du programme ; « en boucle » = enchaîné jusqu'au créneau suivant.
- Modèles prêts : « Film du soir », « Série », « Bloc jeunesse ».

Sous le capot, un programme est enregistré dans `templates/programmes/<nom>.json` et traduit en éléments ErsatzTV standards :
une playlist « Programme : nom » (groupe « Programmes »), une playlist « Coupure : nom » (groupe « Programmes · coupures »)
et un préréglage de remplissage « milieu de programme » « Coupure : nom ». Modifier le programme met à jour les chaînes qui l'utilisent.

**Nouveau dans le moteur — coupures sans chapitres.** Dans l'expression d'un remplissage « milieu de programme », la directive
`@coupures toutes=15 debut=10 fin=5` (ou `@coupures nombre=2 fin=5`) crée des points de coupure virtuels, en minutes.
Les éléments plus courts que le premier point ne sont pas coupés (et le remplissage n'est pas converti en « après »).
Une expression classique peut suivre après `;`. Utilisable aussi à la main depuis Collections › Préréglages de remplissage.

### Système de modules (`/modules`)
Voir [MODULES.md](MODULES.md). Un module ajoute des pages, des entrées de menu, des cartes du tableau de bord et des services,
sans toucher au cœur. Deux exemples sont fournis : « Statistiques de diffusion » (intégré) et « Bloc-notes de la régie » (externe, `plugins-exemple/`).

### Corrections
- Les rendez-vous ajoutés depuis le planning (et les créneaux de l'assistant) sont remis dans l'ordre chronologique :
  ErsatzTV lit les créneaux dans l'ordre de la liste, un créneau à heure fixe placé après un plus tardif passait au lendemain.
- ErsatzTV plantait après l'ajout d'un élément de planning sans filigrane ni élément graphique (liste non initialisée) : corrigé
  (`ProgramScheduleItemCommandBase.cs`).
- Ta modification `frame-ancestors *` (intégration en iframe) est conservée dans `Startup.cs`.

## Compiler et lancer
Comme avant : ouvrir `ErsatzTV.sln` dans Visual Studio (SDK .NET 10) et lancer le projet `ErsatzTV`, ou

```
dotnet build ErsatzTV.sln -c Release
dotnet run --project ErsatzTV -c Release
```

Aucune migration de base de données n'est nécessaire : l'horloge utilise une nouvelle valeur (5) de `GraphicsElementKind`,
et les préférences utilisent la table de configuration existante. Les nouveaux dossiers sont créés au démarrage :

| Dossier (dans le dossier de config ErsatzTV) | Rôle |
|---|---|
| `templates/graphics-elements/clock/` | éléments « horloge » |
| `templates/habillages/` | scènes du studio (`.json`) |
| `templates/habillages/assets/` | images importées dans le studio |
| `templates/programmes/` | programmes composés (`.json`) |
| `plugins/` | modules externes (un sous-dossier par module) |

## Où sont les changements

| Zone | Fichiers |
|---|---|
| Moteur graphique (horloge) | `ErsatzTV.Core/Graphics/ClockGraphicsElement.cs`, `ErsatzTV.Infrastructure/Streaming/Graphics/Text/ClockElement.cs`, `GraphicsElementLoader.cs`, `GraphicsEngine.cs`, `TextElement.cs`, `GraphicsEngineContext.cs`, `GraphicsElementKind.cs`, `RefreshGraphicsElementsHandler.cs`, `Graphics/Mapper.cs`, `FileSystemLayout.cs` |
| Services de l'édition | `ErsatzTV.Application/Nounours/` (planning, catalogue, habillage) · `ErsatzTV/Nounours/` (assistant, préférences, libellés FR, modules) |
| Pages nouvelles | `ErsatzTV/Pages/Nounours/` (Accueil, Assistant, Programmes, Planning, Habillages, Studio, Modules), `ErsatzTV/Shared/Nounours/` |
| Programmes composés | `ErsatzTV.Application/Nounours/Programmes.cs`, `ErsatzTV.Core/Scheduling/MidRollBreaks.cs` (+ `PlayoutModeSchedulerBase.cs`, `FillerExpression.cs`), tests `ErsatzTV.Core.Tests/Scheduling/MidRollBreaksTests.cs` |
| Modules | `ErsatzTV.Plugins/` (contrat), `ErsatzTV/Nounours/Plugins/` (chargement), `ErsatzTV/Nounours/Modules/` (module intégré), `plugins-exemple/` |
| Interface | `Shared/MainLayout.razor`, `App.razor`, `Pages/_Host.cshtml`, `wwwroot/css/nounours.css`, `wwwroot/js/nounours.js`, `Controllers/NounoursController.cs` |
| Traduction | toutes les pages de `Pages/` et `Shared/`, `Locals/**/*.fr.resx`, `Localization.cs`, vérifications de santé (`ErsatzTV.Infrastructure/Health/Checks/`) |

L'ancienne page « État du système » reste à `/system/health` ; le formulaire complet de création de chaîne reste à `/channels/add`.
