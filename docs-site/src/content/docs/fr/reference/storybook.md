---
title: Catalogue UI (Storybook)
description: Démarrer et tester le catalogue React sans backend.
---

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
