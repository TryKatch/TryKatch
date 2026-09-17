# Catalogue UI

Depuis la racine de l’application générée, contenant sa solution et `trykatch.modules.json` :

```bash
cd web
corepack pnpm install --frozen-lockfile
corepack pnpm storybook
```

Ouvrez <http://localhost:6006>. Storybook utilise des données de démonstration : **ne démarrez ni l’API, ni Docker, ni Aspire**. Il présente l’interface, pas une application métier active. Les projets uniquement backend ne l’incluent pas.

La barre d’outils permet de choisir le thème clair/sombre, l’anglais/français et les formats téléphone/tablette/ordinateur. Les styles et variables CSS sont ceux de l’application. Les champs natifs sont des exemples réutilisables, pas une nouvelle bibliothèque de formulaires.

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
