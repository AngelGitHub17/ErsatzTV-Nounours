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

### Menu simple et guide « Bien démarrer »
- Par défaut, le menu ne montre que l'essentiel, rangé par usage : **Ma télé** (chaînes, créer une chaîne, programmes,
  planning, régie) · **À l'écran** (habillage) · **Mes médias** (ajouter des médias, films, séries, vidéos) · Modules · Réglages.
  « À vérifier » n'apparaît qu'en cas de problème. L'interrupteur **« Toutes les options (expert) »** affiche le menu complet d'ErsatzTV.
- Sur l'accueil, le guide **« Bien démarrer »** coche tout seul les 5 étapes : FFmpeg → ajouter des médias → créer une chaîne →
  habiller la chaîne → regarder (avec les adresses M3U et XMLTV à copier). Il disparaît quand tout est fait
  (ou via « Masquer ce guide » ; réactivable dans Réglages › Interface).
- **Ajouter des médias** (`/medias`) : films, séries, clips et vidéos s'ajoutent comme les pubs — on choisit le type, on indique le dossier
  (ou on envoie les fichiers), c'est analysé. Le dossier rejoint votre bibliothèque existante du même type s'il y en a une.

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

- **Compte à rebours** (« Début dans 1:30 » avant le programme suivant, affichable seulement les X dernières minutes),
  **temps restant** du programme en cours (`{restant}`), et **« Prochainement »** : les 3 prochains programmes du guide avec leur heure.
  Dans un texte d'horloge : `{clock}` = l'heure, `{countdown}` = temps avant le programme suivant, `{restant}` = temps restant.

- **Horloge à aiguilles** (nouveau dans le moteur) : cadran rond avec repères (points, traits ou aucun), contour, fond, aiguille des secondes
  en option, ombre pour rester lisible sur fond clair. Taille à la poignée, comme une image.
- **Cadre (passe-partout)** : une bordure pleine autour de l'image avec une fenêtre au centre (épaisseurs, couleur, arrondi, fine ombre),
  pour l'effet « carton » des années 90. Il passe sous les autres calques ; l'image n'est pas réduite, ses bords sont recouverts.
- **Modèles** en un clic sur la page Habillage :
  - **Classique** — horloge, titre au début du programme, « à suivre » à la fin ;
  - **Rétro 95** — cadre clair, cartouche « MA CHAÎNE », bandeaux noirs en capitales italiques (titre au début, « À SUIVRE · 20H40 · TITRE » à la fin),
    bloc de couleur ; remplacez le cartouche par votre logo ;
  - **Matinale** — horloge à aiguilles et titre empilé en haut à gauche, date et heure en bas.
- Mettre un texte en capitales : `{{ MediaItem_Title | string.upcase }}`. Les graisses Medium, Bold, Black et les italiques de Roboto sont fournies.

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

### Semaine type (`/semaine-type`)
Un planning différent selon le jour de la semaine : mercredi jeunesse, samedi cinéma, week-end plus calme…
- Pour chaque jour, choisissez le planning à utiliser ; raccourcis « Week-end différent », « Mercredi différent »,
  « Même planning toute la semaine ».
- « Version propre à ce jour » copie le planning sous un nouveau nom pour le modifier sans toucher aux autres jours.
- Sous le capot : les plannings alternatifs d'ErsatzTV (par jour de semaine). Les règles par date ou par mois réglées en mode expert sont conservées et restent prioritaires.

### Pubs, jingles & bandes-annonces en un clic (`/importer`)
- Choisissez le type (Pubs, Jingles, Bandes-annonces, Habillage vidéo), indiquez un dossier du serveur (avec un explorateur de dossiers)
  **ou envoyez directement des vidéos** depuis le navigateur.
- Le dossier est ajouté à une bibliothèque dédiée (« Habillage · Pubs »…) et analysé ; une **collection intelligente** du même nom
  (« Pubs »…) est créée et tenue à jour : elle apparaît tout de suite dans les programmes et l'assistant.
- Une collection « Pubs » créée à la main n'est jamais écrasée (l'import utilise alors « Pubs · import »).

### Régie — prendre l'antenne (`/regie`)
- Choisissez une chaîne, une vidéo ou un flux distant (recherche par titre, ou les **écrans de secours** importés comme « Habillage vidéo »),
  puis **« Prendre l'antenne maintenant »** : une fois, en boucle pendant X minutes, ou jusqu'à « Rendre l'antenne » (6 h max).
- Les spectateurs basculent en quelques secondes (la session HLS est relancée, le lecteur se reconnecte). Le guide affiche le titre choisi (« Flash spécial »).
- **« Rendre l'antenne »** remet la grille exactement là où elle en serait : rien n'est reconstruit, les programmes recouverts
  sont mis de côté dans `regie/<playout>.json` et replacés (le programme en cours reprend au bon endroit).
- Bouton « Contrôle » : aperçu de la chaîne dans le navigateur.
- Limite : en mode « MPEG-TS » ou « HLS direct », le changement n'est visible qu'au programme suivant ou à la reconnexion du lecteur.

### Audience (module intégré, `/modules/audience`)
- Spectateurs **en direct** par chaîne avec le programme en cours, carte sur le tableau de bord.
- Par jour : temps regardé, spectateurs distincts, pic simultané, durée moyenne, courbe heure par heure (filtrable par chaîne),
  part de chaque chaîne, **programmes les plus regardés**, et l'historique des 14 derniers jours.
- Comptage : chaque requête de flux IPTV (M3U/HLS, HDHomeRun, `.ts`, aperçu) compte ; un spectateur = empreinte anonyme
  de l'adresse + du lecteur (aucune adresse IP n'est enregistrée), actif tant qu'il lit ou pendant 40 s après sa dernière requête.
  Relevé chaque minute dans `audience/AAAA-MM-JJ.json`.

### Guide JSON pour le site NounoursTV (`/iptv/nounours/epg.json`)
Le même guide que `xmltv.xml`, en JSON prêt à afficher sur un site :

```
GET /iptv/nounours/epg.json?heures=24&chaines=1,2&passes=false
```

```json
{
  "genereLe": "2026-10-20T19:42:39+02:00",
  "jusqua": "2026-10-21T19:42:39+02:00",
  "chaines": [{
    "id": "C2.146.ersatztv.org", "numero": "2", "nom": "Nounours Ciné", "logo": "http://…/iptv/logos/…",
    "flux": { "hls": "http://…/iptv/channel/2.m3u8", "ts": "http://…/iptv/channel/2.ts" },
    "enCours": { … }, "aSuivre": { … },
    "programmes": [{
      "debut": "2026-10-20T20:00:00+02:00", "fin": "2026-10-20T20:03:00+02:00", "dureeMinutes": 3,
      "titre": "Les Nounours", "sousTitre": "…", "description": "…", "categories": ["Series"],
      "image": "http://…/iptv/artwork/posters/…", "annee": "2024", "classification": "-12",
      "saison": 1, "episode": 1, "rediffusion": true, "enCours": false
    }]
  }]
}
```
- `heures` : durée couverte à partir de maintenant (1 à 336, défaut 24) · `chaines` : numéros séparés par des virgules · `passes=true` : inclut les programmes déjà terminés de la fenêtre.
- Les champs vides sont omis. Les chaînes masquées du guide le sont aussi ici. CORS ouvert (appel direct depuis le navigateur possible).
- Si l'accès IPTV est protégé (JWT), ajoutez `?access_token=…` comme pour le M3U ; il est repris dans les liens de flux.

### Système de modules (`/modules`)
Voir [MODULES.md](MODULES.md). Un module ajoute des pages, des entrées de menu, des cartes du tableau de bord et des services,
sans toucher au cœur. Exemples fournis : « Audience » et « Statistiques de diffusion » (intégrés) et « Bloc-notes de la régie » (externe, `plugins-exemple/`).

### Corrections
- Les rendez-vous ajoutés depuis le planning (et les créneaux de l'assistant) sont remis dans l'ordre chronologique :
  ErsatzTV lit les créneaux dans l'ordre de la liste, un créneau à heure fixe placé après un plus tardif passait au lendemain.
- ErsatzTV plantait après l'ajout d'un élément de planning sans filigrane ni élément graphique (liste non initialisée) : corrigé
  (`ProgramScheduleItemCommandBase.cs`).
- Ta modification `frame-ancestors *` (intégration en iframe) est conservée dans `Startup.cs`.

## Installer sur Windows

Double-cliquez sur **`windows\Installer.bat`** (il faut le [SDK .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0)). Le script :
1. compile ErsatzTV et son scanner dans `%LOCALAPPDATA%\Programs\ErsatzTV-Nounours` ;
2. crée le raccourci **« ErsatzTV Nounours »** sur le Bureau et dans le menu Démarrer (démarre ErsatzTV sans fenêtre et ouvre l'interface),
   ainsi que **« Arrêter ErsatzTV Nounours »** dans le menu Démarrer ;
3. active le **démarrage automatique** à l'ouverture de session (en arrière-plan, sans ouvrir le navigateur) ;
4. démarre ErsatzTV et ouvre http://localhost:8409.

- **Mettre à jour** : `git pull`, puis relancez `Installer.bat` (ErsatzTV est arrêté, recompilé et redémarré).
- Sans démarrage automatique : `Installer.bat -SansDemarrageAuto` · Désinstaller : `Installer.bat -Desinstaller`.
- Vos chaînes, plannings et réglages restent dans `%LOCALAPPDATA%\ersatztv` : ils ne sont jamais effacés.
- Si l'ErsatzTV d'origine est installé, ne lancez pas les deux en même temps (même port 8409, même base de données) :
  retirez son éventuel démarrage automatique.
- Au premier lancement, Windows peut demander d'autoriser ErsatzTV dans le pare-feu : acceptez pour les réseaux privés
  si vos téléviseurs / lecteurs IPTV sont sur d'autres appareils.

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
| `audience/` | relevés d'audience (un `.json` par jour) |
| `regie/` | prises d'antenne en cours (programmes mis de côté) |
| `medias-importes/` | vidéos envoyées depuis la page d'import |

## Où sont les changements

| Zone | Fichiers |
|---|---|
| Moteur graphique (horloge) | `ErsatzTV.Core/Graphics/ClockGraphicsElement.cs`, `ErsatzTV.Infrastructure/Streaming/Graphics/Text/ClockElement.cs`, `GraphicsElementLoader.cs`, `GraphicsEngine.cs`, `TextElement.cs`, `GraphicsEngineContext.cs`, `GraphicsElementKind.cs`, `RefreshGraphicsElementsHandler.cs`, `Graphics/Mapper.cs`, `FileSystemLayout.cs` |
| Services de l'édition | `ErsatzTV.Application/Nounours/` (planning, catalogue, habillage) · `ErsatzTV/Nounours/` (assistant, préférences, libellés FR, modules) |
| Pages nouvelles | `ErsatzTV/Pages/Nounours/` (Accueil, Assistant, Programmes, Planning, Habillages, Studio, Modules), `ErsatzTV/Shared/Nounours/` |
| Programmes composés | `ErsatzTV.Application/Nounours/Programmes.cs`, `ErsatzTV.Core/Scheduling/MidRollBreaks.cs` (+ `PlayoutModeSchedulerBase.cs`, `FillerExpression.cs`), tests `ErsatzTV.Core.Tests/Scheduling/MidRollBreaksTests.cs` |
| Modules | `ErsatzTV.Plugins/` (contrat), `ErsatzTV/Nounours/Plugins/` (chargement), `ErsatzTV/Nounours/Modules/` (modules intégrés), `plugins-exemple/` |
| Semaine type · import · régie | `ErsatzTV.Application/Nounours/WeekTemplate.cs`, `FolderImport.cs`, `Regie.cs` · pages `WeekTemplatePage.razor`, `ImportPage.razor`, `RegiePage.razor` |
| Audience | `ErsatzTV/Nounours/Audience/` (comptage, relevés), `ErsatzTV/Nounours/Modules/Audience/` (page, carte) |
| Guide JSON | `ErsatzTV/Controllers/NounoursEpgController.cs` |
| Interface | `Shared/MainLayout.razor`, `App.razor`, `Pages/_Host.cshtml`, `wwwroot/css/nounours.css`, `wwwroot/js/nounours.js`, `Controllers/NounoursController.cs` |
| Traduction | toutes les pages de `Pages/` et `Shared/`, `Locals/**/*.fr.resx`, `Localization.cs`, vérifications de santé (`ErsatzTV.Infrastructure/Health/Checks/`) |

L'ancienne page « État du système » reste à `/system/health` ; le formulaire complet de création de chaîne reste à `/channels/add`.
