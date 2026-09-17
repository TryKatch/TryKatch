---
title: Catalogue UI (Storybook)
description: Démarrer et tester le catalogue React sans backend.
---

Commencez par **Welcome → Catalogue guide**. **Forms → Validation** présente les champs requis, la correction, l’enregistrement et les erreurs serveur avec conservation des saisies. Les vrais formulaires de création et d’envoi se trouvent dans **Module UI → Projects / Documents**.

**Forms → Floating fields** présente les champs et zones de texte à libellé flottant : vides, remplis, invalides, désactivés, en lecture seule, nombres et dates. Les formulaires de rôles, projets et métadonnées de documents les réutilisent. Les libellés restent accessibles ; les fichiers et listes natives gardent des libellés visibles.

Le libellé flotte sur la bordure supérieure au focus, y reste après la saisie et revient à l’intérieur après effacement et perte du focus. Un fieldset/legend décoratif ouvre une vraie encoche autour du libellé transparent, sans fond peint ni second contour au focus. L’encoche se ferme lorsque le champ vide perd le focus. Les recherches des tableaux, collections, audits et permissions suivent ce comportement avec leur icône. L’éditeur de rôles fait défiler son contenu séparément des boutons fixes ; voir **Forms → Validation → Constrained height / Narrow saving**.

Les champs flottants gardent les hauteurs compactes Trykatch : 38 px par défaut (seulement 2 px de plus que la base de 36 px), toujours 38 px pour les rôles et 34 px pour les recherches. Les zones de texte respectent leur nombre de lignes natif et restent redimensionnables ; la taille des boutons ne change pas.

**Forms → Floating fields → Search / Search filters** présente `SearchField`, avec une icône à gauche et un bouton de filtrage facultatif à droite. La liste propose Échap, fermeture extérieure et retour du focus. Les champs utilisent une seule bordure avec des couleurs de focus, sans anneau extérieur supplémentaire. La fonctionnalité fournit ses contrôles via `filters`, ses traductions et des compteurs/requêtes exacts. Les contrôles du portail ne doivent pas dépendre de la soumission d’un formulaire parent. `DataTable.searchFilters` offre la même interaction sans déplacer Columns ; voir **DataTables → Tables → Search filter dropdown**.

Les formulaires de rôles d’organisation, projets et métadonnées de documents utilisent Zod 4.5.4. FluentValidation 12.1.1 valide indépendamment les commandes correspondantes côté serveur. Les autres formulaires peuvent encore utiliser une validation native/personnalisée. Les skills livrées demandent de consulter Storybook avant les tâches frontend et de maintenir les stories pertinentes.

Depuis la racine de l’application générée :

```bash
cd web
corepack pnpm install --frozen-lockfile
corepack pnpm storybook
```

Ouvrez `http://localhost:6006`. Ni l’API, ni Docker, ni Aspire ne sont nécessaires. Les applications uniquement backend ne disposent pas de Storybook. La barre d’outils propose les thèmes clair/sombre, l’anglais/français et les tailles de fenêtre.

Depuis `web`, construisez et testez les interactions et l’accessibilité :

```bash
corepack pnpm --filter @trykatch/web exec playwright install chromium
corepack pnpm storybook:build
corepack pnpm storybook:test
```

Dans une application renommée, remplacez `@trykatch/web` par le nom du package dans `web/apps/web/package.json`.

Les fichiers `*.stories.tsx` sont découverts automatiquement dans les sources des packages partagés, de l’hôte et dans `Web/src` des modules. Utilisez les vrais composants, des fixtures API MSW explicites et des tests `play`. Les requêtes API non prévues échouent sans contacter un serveur. N’importez pas les fixtures dans les points d’entrée de production et gardez le service worker dans le catalogue uniquement.

Les formulaires documentent les contrôles HTML existants. Les tableaux montrent la pagination, le tri, la visibilité et la densité supportés, pas le redimensionnement ou le réordonnancement. Storybook ne remplace pas les tests d’autorisation backend, d’isolation PostgreSQL ou de bout en bout.
