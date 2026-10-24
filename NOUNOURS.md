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
- **Bandeau défilant** (nouveau dans le moteur) : phrases fixes, titres d'un **flux RSS** et **météo** d'une ville (Open-Meteo, gratuit,
  sans clé ; la ville se cherche avec la loupe), étiquette colorée à gauche (« INFO »), vitesse, hauteur, fond. Mis à jour toutes les 15 min.
  Le défilement est calé sur l'heure : il ne saute pas d'un programme à l'autre.
- **Signalétique jeunesse** : rond **-10 / -12 / -16 / -18** choisi automatiquement selon la classification du programme
  (films, épisodes via la série, autres vidéos), rien si le programme n'est pas classé. Par défaut pendant la première minute.
- **Plusieurs habillages par chaîne, avec horaires** : chaque habillage peut avoir une plage « de… à… » (ex. Matinale 06:00 → 09:00,
  Rétro 95 20:00 → 23:59, passage de minuit géré). Appliquer un habillage l'**ajoute** à la chaîne : s'il y en a plusieurs, ils sont
  réunis dans une deco « Habillage combiné · numéro », mise à jour à chaque enregistrement.
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

### Atelier vidéo — jingles, génériques, bandes-annonces (`/atelier`)
Fabrique des vidéos au style « Rétro 95 » — un cadre clair, des **rectangles de couleur qui bougent au rythme du son**
(graves, médiums et aigus les font grandir, chaque temps fort les fait changer de place), des bandeaux noirs en capitales italiques.
- **Jingle avant programme** (étiquette FILM / SÉRIE / CINÉMA… puis le titre), **Générique** (lignes « Rôle : Nom » qui apparaissent
  une à une), **Annonce** (« CE SOIR » + titre + horaire), **Début de pub** / **Fin de pub**.
- On **envoie un son** (mp3, wav, ogg…), on tape le titre : l'aperçu se met à jour, « Créer la vidéo » fabrique le MP4 (1080p, 25 i/s).
  Sans son : une pulsation régulière. Palettes prêtes (Rétro 95, Primaires, Pastel, Néon, Mono), couleur du cadre, énergie du mouvement,
  « Autre chorégraphie ».
- **Bande-annonce** : on ajoute un ou plusieurs programmes (films, épisodes, vidéos) ; chacun donne 3 extraits répartis, réglables
  (début, durée) avec une **vignette** de l'image. Résultat : ouverture « À L'AFFICHE » + titre → extraits dans le cadre clair avec le
  bandeau titre / diffusion → fermeture avec la date. Bouton pour retrouver la **prochaine diffusion** dans le guide.
  Musique au choix (avec ou sans le son des extraits en fond).
- Les vidéos sont rangées toutes seules dans les médias d'habillage (Jingles, Bandes-annonces, Pubs…) : elles sont tout de suite
  utilisables dans les programmes (segments Jingle, Bande-annonce…). Onglet « Mes créations » pour les revoir ou les supprimer.
- Utilise le FFmpeg configuré dans ErsatzTV (encodeur libx264). Seuls les fichiers locaux peuvent servir d'extraits.
- **Quadrillage** (par défaut) : l'écran est découpé en cases (3 × 3 en 4/3, 4 × 3 en 16/9) dans un cadre crème ; à chaque temps fort
  les cases changent toutes d'un coup (damier, bloc d'angle, bande, colonne, cases éparses) en noir, blanc, orange, rouge, gris.
  L'étiquette (SÉRIE, FILM…) s'affiche dans un bloc noir de 2 × 2 cases qui change de coin, puis vient le titre. « Rectangles libres »
  garde l'ancien style. Format **« 4/3 d'époque »** : image 4/3 avec bandes noires sur les côtés.
- **Carton de fin** sur fond noir (ex. « SÉRIE DÉCONSEILLÉE AUX MOINS DE 10 ANS »), durée réglable.
- **Voix off** : texte lu par la voix de Windows (sans rien installer), par un serveur de synthèse compatible OpenAI (Chatterbox…)
  ou un fichier enregistré ; la musique baisse toute seule pendant la voix.
- Onglet **Sous-titres** : envoie l'audio d'un programme à un serveur de transcription (faster-whisper-server…) et range un
  `.fr.srt` à côté de la vidéo (ErsatzTV le détecte au scan suivant).

- Onglet **Habillage complet** : un nom de chaîne + une musique → un jingle par genre (FILM, SÉRIE, CINÉMA, JEUNESSE… au choix),
  les cartons « Publicité » / « Fin de la publicité » et 8 cartons de signalétique (-10 à -18, films et séries), chacun avec sa
  propre chorégraphie. Noms de fichiers fixes : refaire le pack remplace les vidéos (les programmes qui les utilisent suivent).
- **Programme annoncé** : en choisissant un film ou un épisode de la médiathèque, le titre, l'étiquette (FILM / SÉRIE) et le
  **carton de signalétique** sont remplis d'après sa classification (ex. « fr/-12 » → « FILM DÉCONSEILLÉ AUX MOINS DE 12 ANS »).
  Nouveau type **Carton** : un texte seul sur fond noir.
- **Sons par ambiance** (Énergique, Calme, Suspense, Jeunesse, Cinéma, Pub) : filtre au-dessus du choix du son, classement de chaque
  son. Six **boucles intégrées** sont fabriquées par le programme (synthétisées, sans droits) : Pulsation 95, Vitamine, Nuit calme,
  Suspense, Grand écran, Coupure pub.

- **Couleurs selon la saison** (atelier et quadrillage en direct, interrupteur) : Noël (bordeaux, vert), hiver (marine),
  printemps (vert, orange), été (orange, rouge, marine), Halloween (orange, chocolat), toujours avec les couleurs d'époque.
- La page d'exemple **guide.php** reprend les couleurs de l'habillage (cadre vert amande, en-têtes noirs / rouges / marine).
- **Palette Rétro 95 d'époque** (par défaut partout : atelier, rideau, « Tout de suite », cadre du modèle) : cadre vert amande
  `#E1E8CD`, noir `#000000`, blanc `#FFFFFF`, chocolat `#3C3632`, bordeaux `#8C0A11`, rouge vif `#FF3200`, vert émeraude `#008A22`,
  bleu marine `#0B2B70`, orange `#FFA800`. Le rouge vif domine ; bordeaux et chocolat en second ; vert, marine et orange en touches.
  Variantes prêtes : Rouges 95, Orange 95, Vert 95, Marine 95. Les habillages déjà enregistrés gardent leurs couleurs : cliquez
  « Rétro 95 » dans le calque pour les mettre à jour.

### Quadrillage en direct (studio d'habillage)
- **Cartouches à la taille du texte** : un calque texte peut avoir un fond qui s'adapte à son texte (largeur alignée sur des
  seizièmes d'écran) et qui ne s'affiche pas quand le texte est vide. Le modèle « Rétro 95 » s'en sert pour le nom de la chaîne,
  le titre et le sous-titre (titre de l'épisode, dans un cartouche rouge vif).
Deux nouveaux calques, aussi présents dans le modèle « Rétro 95 » :
- **Rideau quadrillé** : à la fin de chaque programme, des cases de couleur couvrent l'image une à une ; au début du suivant, elles
  la découvrent. Durée, ouverture / fermeture, couleurs et liserés réglables ; ignoré pour les programmes courts (jingles, pubs).
- **Tout de suite** : dans les dernières secondes, un bloc de cases « TOUT DE SUITE » + titre et heure du programme suivant (guide).
- Sous le capot : nouveau type d'élément graphique `quadrillage` (Kind 7, dossier `graphics-elements/quadrillage`).

- **Sons intégrés v2** : un petit studio « années 90 » synthétise 12 sons originaux (aucun emprunt) : 8 **indicatifs** courts
  qui finissent sur un coup (orchestre, cuivres, cloches, timbales) — boîte à rythmes façon 909, caisse claire « gated », basse synthé,
  cuivres, piano électrique FM, montée de souffle, réverbération — et 4 **boucles** (interlude, météo, cartons). Nouvelle ambiance
  « Indicatif ». Les sons sont tirés au hasard en évitant les derniers utilisés ; l'habillage complet donne un indicatif différent
  à chaque jingle, joué jusqu'à son coup final.
- **Niveaux** : toutes les musiques sont normalisées (−16 LUFS, crêtes −1,5 dB). Dans les bandes-annonces, la musique est pleine à
  l'ouverture et à la fermeture et **s'efface sous les extraits** (−16 dB si l'on garde leur son, sinon légèrement baissée).
- **Titres calés sur la grille** : le titre se place dans un bloc noir de la largeur qu'il lui faut (en cases entières, sur deux
  lignes au besoin), le sous-titre dans un bloc de couleur dessous, les blocs apparaissent case par case ; cartouche de la chaîne en
  bas à droite, bandeaux des bandes-annonces en demi-cases. Fini les rectangles vides de largeur fixe.

### Conducteur en direct (`/conducteur`)
Les 6 prochaines heures d'une chaîne. Choisissez une vidéo ou un flux, puis : **Passer maintenant**, **Juste avant** un programme
(elle finit pile quand il commence, à la place de la fin du précédent) ou **À la place** d'un programme (pendant toute sa durée).
Les insertions s'affichent dans la liste et s'annulent d'un clic. La grille n'est pas reconstruite : tout reprend à l'heure.

### Programmation automatique (`/programmation-auto`)
- ErsatzTV regarde la médiathèque (genres et classifications : Animation, Famille, Documentaire…) et propose une **vraie grille de
  télé** : matinale jeunesse, séries, journal, film famille l'après-midi, access, **film du soir à 20h45**, deuxième partie de soirée,
  nuit en rediffusion — et un **week-end différent**.
- 6 styles : généraliste, jeunesse, cinéma, séries, info, musique. Chaque famille affiche son nombre de programmes ; s'il n'y a rien
  (ex. pas de dessins animés), un autre type de programme prend le relais (icône ⇄).
- Grille **modifiable** avant d'appliquer : heure, famille, nom du rendez-vous (pour le guide), « un seul programme » ou « en continu »,
  ajout / suppression, frise de la journée de 5h à 5h.
- Options : jingle avant chaque programme (vos « Jingles »), pubs entre les programmes (vos « Pubs »), **débuts au quart d'heure**
  (les pubs comblent), week-end différent (semaine type).
- Appliquer : crée une nouvelle chaîne ou remplace la programmation d'une chaîne existante. Les familles deviennent des collections
  intelligentes « Auto · … » qui suivent la médiathèque toutes seules. Le direct (EBS…) se cale pile dans ses créneaux, sans jingle
  ni pubs autour (les fiches des flux d'info durent désormais 30 min).
- Deux corrections d'ErsatzTV trouvées en testant (`PlayoutBuilder.cs`) : le jour des plannings alternés (week-end) se lit à
  l'heure locale (avant, lundi 0h30 en France comptait encore pour dimanche, et **tout le lundi** passait la grille du week-end) ;
  et le jour du changement d'heure, « minuit » n'est plus calculé à 23h (la journée suivante démarrait la veille).

### Rotation et trous (programmation automatique)
- **Rotation** : pour chaque famille de la grille, tous les combien un même programme revient (d'après la grille et la durée
  moyenne des programmes) — orange s'il revient dans la semaine, rouge s'il revient presque tous les jours.
- **Trous dans les grilles (48 h)** : les moments où une chaîne n'a rien de prévu, et un bouton « Combler » qui fait passer une
  collection au choix pendant ces moments-là (remplissage de secours).

### Sagas et collections de films (`/collections-films`)
- **Par époque** : 4 collections intelligentes d'après la date de sortie — nouveautés (moins de 5 ans), récents (moins de 10 ans),
  un peu anciens (10 à 20 ans), très anciens (plus de 20 ans). Elles avancent avec le temps et servent aussi dans la
  programmation automatique (style Cinéma : grands classiques le matin, film récent à 20h45, classique du week-end).
- **Sagas** trouvées toutes seules : collections Jellyfin / Plex / Emby, `<set>` des fichiers .nfo (Kodi, Jellyfin — désormais lu
  par le scanner), ou titres qui se ressemblent (« Scream », « Scream 2 », « Scream VI » ; « Harry Potter à l'école… »,
  « Harry Potter et la Chambre… » ; « Les Bronzés », « Les Bronzés font du ski »). Films dans l'ordre de sortie ; nom et recherche
  modifiables, sagas à ajouter à la main (« mots du titre »). Chaque saga devient la collection intelligente « Saga · … ».
- **Programmer un rendez-vous** : saga ou collection, chaîne, jours (« Tous les soirs », « Week-end », ou ven. seul…), heure, « un
  film dans l'ordre » (la saga reprend où elle s'était arrêtée la fois suivante), « deux films » ou « marathon jusqu'au rendez-vous
  suivant », et en option **une période** (« les vendredis de décembre »). Le rendez-vous remplace ce qui commençait à cette heure
  ces jours-là (avec les mêmes jingles et pubs) ; les autres jours ne changent pas (planning copié « … · le vendredi » et semaine
  type mise à jour, ou règle de période prioritaire).
- **Où en est la saga** : sur chaque saga, le prochain passage (« vendredi 20h45 · Harry Potter et la Chambre des secrets
  (2/7) · Auto Test ») et le dernier (journal de diffusion).
- **Soirée spéciale** (case à cocher en programmant) : un carton « SOIRÉE SPÉCIALE · HARRY POTTER · CE SOIR 20H45 · le film ·
  2/7 » passe juste avant le film, et un carton « LA SUITE · le film suivant · VENDREDI PROCHAIN 20H45 » juste après. Refaits
  chaque matin (même fichier), branchés tout seuls sur les rendez-vous de la saga.
- Corrigé au passage : la mise à jour d'un film perdait le type de ses étiquettes (pays, saga) ; l'heure des rendez-vous de la
  programmation auto ne se modifiait pas (champ masqué).

### Journal de diffusion (`/journal`)
Chaque minute, tout ce qui a commencé à l'antenne (programmes, intercalaires, régie, directs) est noté dans `journal/AAAA-MM-JJ.jsonl`.
Page par jour, filtre par chaîne, intercalaires masquables, export **CSV** (ouvre dans Excel).
Le journal note désormais le vrai titre (« Scream 2 ») et plus le nom du rendez-vous (« Le film du soir »).
**Statistiques de la semaine** : temps d'antenne par genre (films, épisodes, direct, intercalaires…), par chaîne, les programmes les
plus diffusés (épisodes regroupés par série) et ce qui repasse le plus souvent.

### Même volume partout (Services automatiques)
Un interrupteur active la normalisation du son (EBU R128, −16 LUFS) dans tous les profils FFmpeg : même niveau perçu entre films,
séries, pubs et chaînes.

### Info en continu (`/info`)
- **Bandeaux en direct** : sujet « EN DIRECT » (texte fixe, le fond s'arrête au texte), **flash** (bandeau bordeaux, caché s'il est
  vide), **titres** qui défilent (+ RSS). « Mettre à l'antenne » : visible en 5 à 30 s environ (tampon du lecteur), sans couper l'image.
- **Habillage info** créé et appliqué en un clic (nom de la chaîne, heure, les trois bandeaux), modifiable ensuite dans le studio.
- **Prendre l'antenne** avec un flux en direct ou une vidéo : les bandeaux restent par-dessus.
- **Sources** : **OBS** (caméra, invités…) en **SRT** — dans OBS : Paramètres › Stream › Personnalisé, serveur
  `srt://0.0.0.0:9710?mode=listener&latency=300000` (adresse et port réglables, UDP possible sur le même réseau) — et des **flux de
  chaînes d'info** (lien direct .m3u8 / .ts que la chaîne autorise à reprendre). Ils deviennent des « flux en direct » (bibliothèque
  « Flux en direct ») utilisables aussi dans la Régie et le Conducteur.
- **Créer la chaîne d'info en un clic** (« Nounours.Infos ») : bouton « Ajouter EBS » — la chaîne d'information des institutions
  européennes, diffusée en clair et réutilisable sous licence **CC BY 4.0** en citant la source (le crédit « © Union européenne »
  s'affiche à l'écran) ; son en français assemblé automatiquement (image 720p + piste française) — puis « Créer la chaîne » : chaîne
  en continu sur les flux d'info, habillage info appliqué, flash relié. France 24, DW… : il faut leur accord pour rediffuser.
- **Titres automatiques** : adresses lues toutes les N minutes (RSS, Atom, ou une page de vos sites qui renvoie un titre par ligne
  ou un tableau JSON), ajoutées aux titres du bandeau. Exemple PHP : `<?php header('Content-Type: text/plain; charset=utf-8');
  echo "Titre 1\nTitre 2";`.
- **Nom du site devant ses titres** : une ligne « `NSN | https://…` » affiche « NSN : … » devant chaque titre de ce site.
- **Programme d'EBS dans le guide** : toutes les heures, le programme officiel d'EBS (le service JSON qu'utilise la page « EBS
  schedule » de la Commission) renomme les blocs EBS du guide sujet par sujet (« EBS · en direct · Point de presse de midi… »).
  Le week-end, EBS publie souvent un programme vide : le guide affiche « Images de l'actualité européenne ».
- **Télécommande sur téléphone** (`/telecommande`) : avec un code (page Info), lancer un flash, changer le sujet « EN DIRECT »,
  ajouter un titre, **prendre l'antenne avec OBS / la rendre et changer de caméra** (scènes OBS), avec ou sans le jingle « Flash spécial »,
  depuis le téléphone. 5 codes faux de suite = 5 minutes de pause.
- **Jingle « Flash spécial »** : fabriqué en un clic, il passe tout seul sur les chaînes choisies dès qu'un nouveau flash est mis
  à l'antenne (une fois par flash).
- **Caméras / scènes OBS** : télécommande d'OBS (obs-websocket v5, Outils › Paramètres du serveur WebSocket, mot de passe) ; les
  scènes s'affichent en boutons dans « Prendre l'antenne » : multi-caméras sans couper l'antenne.
- Un direct tourne désormais en un seul processus continu (au lieu de se reconnecter à la source toutes les 44 s).
- Corrigé : un texte vide (ex. titre d'un flux en direct) faisait disparaître tout l'habillage ; le moteur graphique journalise
  maintenant ses erreurs au lieu de s'arrêter en silence.

### Services automatiques (`/services`)
Ce qu'ErsatzTV fait tout seul, vérifié chaque minute :
- **Publication du guide sur votre site** : envoie le guide JSON (toutes les N minutes) à un petit script PHP à déposer sur votre
  hébergement — `recevoir-guide.php` (protégé par un jeton) et `guide.php`, page « programme TV » d'exemple, téléchargeables depuis la page.
- **Carton « Ce soir »** : fabriqué chaque jour à l'heure choisie avec les vrais programmes de la soirée (quadrillage, horaires, nom
  de la chaîne) ; toujours le même fichier dans « Bandes-annonces », il se met à jour seul.
- **Chaînes éphémères** : une chaîne pour un week-end ou un événement, supprimée automatiquement à la date choisie (aussi réglable
  dans l'assistant de création).
- **Bande-annonce « Cette semaine »** : chaque semaine (jour et heure au choix), les films des 7 prochains jours trouvés dans le
  guide, deux extraits chacun avec le jour et l'heure de diffusion. Toujours le même fichier dans « Bandes-annonces ».
- **Carte météo en quadrillage** : chaque jour, jusqu'à 6 villes (température, temps, mini / maxi) d'Open-Meteo, sans clé.
- **« L'essentiel du jour »** : aux heures choisies (« 12:30, 19:00 »), les titres du moment (les vôtres + vos sites), un par écran
  dans le quadrillage, le nom du site en étiquette, **lus par la voix off** (voix de Windows ou serveur de synthèse) : chaque titre
  reste à l'écran le temps d'être lu. Toujours le même fichier dans « Bandes-annonces », diffusion automatique possible.
- **Bande-annonce du prochain film de saga** : chaque matin, la saga qui passe le plus tôt dans la semaine, trois extraits du
  film avec « CE VENDREDI 20H45 · 2/7 » (fichier « prochaine-saga » dans « Bandes-annonces », diffusion automatique possible).
- **« Il y a 30 ans sortait… »** : le jour anniversaire de la sortie d'un de vos films (30 ans de préférence, puis 20, 25…), un
  carton puis le film à l'heure choisie, à la place de la grille. Il faut la vraie date de sortie (`premiered` des .nfo, Jellyfin,
  Plex).
- **Interlude quadrillé** : une boucle de carrés au rythme d'une musique, rangée dans « Habillage vidéo » ; un clic en fait le
  « remplissage de secours » des chaînes choisies (ce qui passe quand rien n'est prévu, au lieu d'un écran noir).
- **Diffusion automatique** (dans chaque carte : Ce soir, Cette semaine, Météo, L'essentiel, Interlude) : heures (« 07:00, 12:55, 19:55 »),
  chaînes, et « juste avant le programme suivant » (la vidéo remplace la fin du programme en cours, souvent le générique) ou
  « à l'heure exacte ». Passage à la seconde près, via la régie.
- **Rendez-vous d'antenne** : à l'heure dite (certains jours ou tous les jours), la régie passe une vidéo une fois — ouverture,
  fermeture d'antenne… — puis la grille reprend où elle en était. Rattrapage de 10 minutes si ErsatzTV était arrêté.
- Adresses des serveurs de voix off et de sous-titres (facultatifs). Réglages dans `nounours-reglages.json` (dossier de config).

### Mur de chaînes (`/mur`), grille à imprimer (`/grille`), sauvegarde (`/sauvegarde`)
- **Mur de chaînes** : les chaînes en direct en mosaïque (2, 3 ou 4 colonnes), clic pour écouter, plein écran par vignette.
  Chaque vignette lance une diffusion : n'affichez que celles à surveiller.
- **Grille à imprimer** : le programme jour par jour (1, 3 ou 7 jours), épisodes regroupés, choix des chaînes, « Imprimer / PDF »
  (mise en page propre à l'impression) et lien vers le guide JSON.
- **Sauvegarde** : un .zip avec la base (copie cohérente sans arrêter ErsatzTV), les habillages, programmes, éléments graphiques,
  l'audience et les sons de l'atelier (pas les vidéos). La restauration est expliquée sur la page.

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
Visual Studio n'est pas nécessaire : il suffit de **Git** et du **SDK .NET 10** (https://dotnet.microsoft.com/download/dotnet/10.0).

**Le plus simple (Windows)** — dans le dossier du projet, double-clic sur `windows\Installer.bat`, ou dans un terminal :

```
git pull
windows\Installer.bat
```

Il compile, installe dans `%LOCALAPPDATA%\ErsatzTV-Nounours`, crée les raccourcis et le démarrage automatique
(`Installer.bat -SansDemarrageAuto` pour s'en passer, `Installer.bat -Desinstaller` pour tout retirer).

**À la main (PowerShell)**, ce que fait l'installateur :

```
git pull
dotnet publish ErsatzTV.Scanner\ErsatzTV.Scanner.csproj -c Release -r win-x64 --self-contained true -o publish-windows -p:PublishSingleFile=true -p:DebugType=Embedded -p:EnableCompressionInSingleFile=false
dotnet publish ErsatzTV\ErsatzTV.csproj -c Release -r win-x64 --self-contained true -o publish-windows -p:PublishSingleFile=true -p:DebugType=Embedded -p:EnableCompressionInSingleFile=false
.\publish-windows\ErsatzTV.exe
```

puis ouvrir http://localhost:8409. Arrêtez ErsatzTV avant de republier dans le même dossier.

**Pour essayer sans rien installer** (tous systèmes) :

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
| `medias-importes/` | vidéos envoyées depuis la page d'import, et créations de l'atelier |
| `atelier/sources/` | sons envoyés dans l'atelier |
| `templates/graphics-elements/ticker/` | bandeaux défilants |
| `templates/graphics-elements/quadrillage/` | rideau quadrillé et « Tout de suite » |
| `nounours-reglages.json` | réglages des services automatiques |

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
| Atelier vidéo | `ErsatzTV.Application/Nounours/Atelier/` (analyse du son, rendu, FFmpeg), `Pages/Nounours/AtelierPage.razor`, `Shared/Nounours/{SourcePicker,StylePicker,JobCard}.razor` |
| Bandeau défilant | `ErsatzTV.Core/Graphics/TickerGraphicsElement.cs`, `ErsatzTV.Infrastructure/Streaming/Graphics/Text/TickerElement.cs` |
| Mur, grille, sauvegarde | `Pages/Nounours/{WallPage,PrintGuidePage,BackupPage}.razor`, `Controllers/NounoursBackupController.cs` |
| Interface | `Shared/MainLayout.razor`, `App.razor`, `Pages/_Host.cshtml`, `wwwroot/css/nounours.css`, `wwwroot/js/nounours.js`, `Controllers/NounoursController.cs` |
| Traduction | toutes les pages de `Pages/` et `Shared/`, `Locals/**/*.fr.resx`, `Localization.cs`, vérifications de santé (`ErsatzTV.Infrastructure/Health/Checks/`) |

L'ancienne page « État du système » reste à `/system/health` ; le formulaire complet de création de chaîne reste à `/channels/add`.
