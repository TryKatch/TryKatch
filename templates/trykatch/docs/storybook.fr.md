# Catalogue UI

Depuis la racine de l’application générée, contenant sa solution et `trykatch.modules.json` :

```bash
cd web
corepack pnpm install --frozen-lockfile
corepack pnpm storybook
```

Ouvrez <http://localhost:6006>. Storybook utilise des données de démonstration : **ne démarrez ni l’API, ni Docker, ni Aspire**. Il présente l’interface, pas une application métier active. Les projets uniquement backend ne l’incluent pas.

La barre d’outils permet de choisir le thème clair/sombre, l’anglais/français et les formats téléphone/tablette/ordinateur. Les styles et variables CSS sont ceux de l’application. Les champs natifs sont des exemples réutilisables, pas une nouvelle bibliothèque de formulaires.

## Formulaires et validation

Commencez par **Welcome → Catalogue guide**, puis **Forms → Native controls** et **Forms → Validation**. La validation présente le vrai éditeur de rôles : champs requis, correction, enregistrement, erreurs serveur avec conservation des saisies, français et thème sombre. **Module UI → Projects** contient le formulaire de création et ses erreurs. **Module UI → Documents** présente le formulaire d’envoi, la validation des fichiers et le chargement.

**Forms → Floating fields** documente `FloatingInput` et `FloatingTextarea` : vide, rempli, focus/saisie, invalide, désactivé, lecture seule, nombre/date et langues/thèmes. Les rôles, projets et métadonnées de documents réutilisent ces contrôles. Les libellés sont associés aux champs ; ceux des dates/heures flottent toujours. Les fichiers et listes natives gardent leurs libellés visibles. Fournissez des valeurs traduites pour `label`, `description` et `error`, sans recréer les styles dans chaque page.

Les formulaires de rôles d’organisation, projets et métadonnées de documents utilisent Zod 4.5.4 avant l’envoi. FluentValidation 12.1.1 valide indépendamment les commandes correspondantes côté serveur. Sécurité des fichiers, autorisations, unicité et invariants métier restent côté serveur. Les autres formulaires peuvent encore utiliser une validation native/personnalisée ; Zod n’est pas encore universel.

Les skills livrées et `AGENTS.md` demandent de consulter Storybook avant chaque tâche frontend et de maintenir les stories du comportement modifié.

Le libellé reste dans le champ vide non actif, puis flotte sur la bordure supérieure au focus. Un fieldset/legend décoratif ouvre une vraie encoche autour du libellé transparent, sans fond peint ni second contour au focus. Il reste dans l’encoche après la saisie et la perte du focus ; vider puis quitter le champ le ramène à l’intérieur et ferme l’encoche. Les recherches des tableaux, collections, audits et permissions suivent le même comportement en conservant leur icône. **Forms → Validation → Constrained height / Narrow saving** vérifie le défilement de l’éditeur de rôles sans chevaucher les boutons fixes.

Les champs flottants gardent l’échelle compacte Trykatch : 38 px par défaut (seulement 2 px de plus que la base de 36 px), toujours 38 px pour les rôles et 34 px pour les recherches. Les zones de texte suivent leur nombre de lignes natif et restent redimensionnables, sans imposer une grande hauteur minimale. Les libellés flottants ne changent pas la taille des boutons.

**Forms → Floating fields → Search / Search filters** présente `SearchField` : icône de recherche à gauche et bouton de filtrage facultatif à droite, ouvrant une liste aux couleurs Trykatch. Échap et un clic extérieur ferment la liste ; Échap rend le focus au bouton. Les champs indiquent le focus par leur bordure/libellé existants, sans anneau supplémentaire. Les boutons gardent leur indicateur de focus clavier.

Importez `SearchField` depuis le package UI et fournissez des contrôles de filtrage pilotés par leur état dans `filters`. Traduisez `label`, `filterLabel` et `closeFiltersLabel`. La fonctionnalité possède les valeurs, compteurs et requêtes client/serveur ; les compteurs chargés ne représentent pas nécessairement tous les résultats serveur. La liste utilise un portail : employez des valeurs et callbacks contrôlés plutôt que la soumission d’un formulaire parent. `DataTable` propose la même liste via `searchFilters`, sans déplacer Columns. Voir **DataTables → Tables → Search filter dropdown**.

Depuis `web`, construisez et testez le catalogue :

```bash
corepack pnpm storybook:build
corepack pnpm storybook:test
```

Les tests d’interaction et d’accessibilité s’exécutent dans Chromium sans interface. Sur une nouvelle machine, installez d’abord le navigateur :

```bash
corepack pnpm --filter @trykatch/web exec playwright install chromium
```

## Ajouter une story

Placez `*.stories.tsx` près du code des packages partagés, de l’hôte ou dans `Web/src` d’un module. Les stories à la racine de `Web` sont également découvertes. Importez `Meta` et `StoryObj` depuis `@storybook/react-vite` et utilisez le vrai composant.

Chaque package concerné déclare `@storybook/react-vite`, `storybook` et éventuellement `msw` en dépendances de développement. N’importez jamais les fixtures dans le code de production. Le service worker reste dans `web/apps/storybook/public`.

Déclarez les handlers MSW dans `parameters.msw.handlers.api`. Les valeurs par défaut de session, de protection antiforgery et d’autorisations vides sont centrales ; remplacez leur groupe nommé si nécessaire. Les requêtes `/api` ou `/connect` non prévues échouent sans contacter le backend. Chaque story possède un cache de requêtes, un routeur mémoire et un état de récupération de sécurité isolés. Ces autorisations fictives ne modifient pas celles de production.

Testez les interactions avec `play` et `storybook/test`. Présentez les états de chargement, vide, validation, accès limité et conflit lorsque l’interface les supporte. Ne prétendez pas supporter le redimensionnement ou le réordonnancement des colonnes. Storybook complète les tests backend et de bout en bout.
