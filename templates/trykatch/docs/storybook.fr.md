# Catalogue UI

**Application UI → Organization settings** présente les onglets Administration : activation IA propre à chaque organisation, lecture seule, fournisseur indisponible, chargement, erreurs d’enregistrement et rechargement explicite après conflit. Les identifiants du fournisseur ne sont jamais modifiables ni exposés.

**Application UI → Member invitations → Default role forbidden** présente une demande d’invitation sans accès en lecture aux rôles, refusée par le serveur tout en conservant l’adresse du destinataire. Le rôle Membre prédéfini accorde `roles.read` ; ce gestionnaire ne peut donc pas l’attribuer. Aucun catalogue restreint n’est demandé et aucune invitation réussie n’est supposée pour ces permissions.

Depuis la racine de l’application générée, contenant sa solution et `trykatch.modules.json` :

```bash
cd web
corepack pnpm install --frozen-lockfile
corepack pnpm storybook
```

Ouvrez <http://localhost:6006>. Storybook utilise des données de démonstration : **ne démarrez ni l’API, ni Docker, ni Aspire**. Il présente l’interface, pas une application métier active. Les projets uniquement backend ne l’incluent pas.

La barre d’outils permet de choisir le thème clair/sombre, l’anglais/français et les formats téléphone/tablette/ordinateur. Les styles et variables CSS sont ceux de l’application. Les champs natifs sont des exemples réutilisables, pas une nouvelle bibliothèque de formulaires.

## Formulaires et validation

**Application UI → Organization settings** présente les onglets d’administration, le fournisseur/modèle/destination propres à l’organisation, la clé en écriture seule, son remplacement et sa suppression explicite, l’activation, les tests de connexion et les conflits. Les clés sont fictives et les fournisseurs simulés ; ces stories ne prouvent pas une recette auprès d’un fournisseur payant. Une clé enregistrée n’est jamais affichée.

Commencez par **Welcome → Catalogue guide**, puis **Forms → Native controls** et **Forms → Validation**. La validation présente le vrai éditeur de rôles : champs requis, correction, enregistrement, erreurs serveur avec conservation des saisies, français et thème sombre. **Module UI → Projects** contient le formulaire de création et ses erreurs. **Module UI → Documents** présente le formulaire d’envoi, la validation des fichiers et le chargement.

**Forms → Floating fields** documente `FloatingInput` et `FloatingTextarea` : vide, rempli, focus/saisie, invalide, désactivé, lecture seule, nombre/date et langues/thèmes. Les rôles, projets et métadonnées de documents réutilisent ces contrôles. Les libellés sont associés aux champs ; ceux des dates/heures flottent toujours. Les fichiers et listes natives gardent leurs libellés visibles. Fournissez des valeurs traduites pour `label`, `description` et `error`, sans recréer les styles dans chaque page.

Les formulaires de rôles d’organisation, projets et métadonnées de documents utilisent Zod 4.5.4 avant l’envoi. FluentValidation 12.1.1 valide indépendamment les commandes correspondantes côté serveur. Sécurité des fichiers, autorisations, unicité et invariants métier restent côté serveur. Les autres formulaires peuvent encore utiliser une validation native/personnalisée ; Zod n’est pas encore universel.

Les skills livrées et `AGENTS.md` demandent de consulter Storybook avant chaque tâche frontend et de maintenir les stories du comportement modifié.

Le libellé reste dans le champ vide non actif, puis flotte sur la bordure supérieure au focus. Un fieldset/legend décoratif ouvre une vraie encoche autour du libellé transparent, sans fond peint ni second contour au focus. Il reste dans l’encoche après la saisie et la perte du focus ; vider puis quitter le champ le ramène à l’intérieur et ferme l’encoche. Les recherches des tableaux, collections, audits et permissions suivent le même comportement en conservant leur icône. **Forms → Validation → Constrained height / Narrow saving** vérifie le défilement de l’éditeur de rôles sans chevaucher les boutons fixes.

Les champs flottants gardent l’échelle compacte Trykatch : 38 px par défaut (seulement 2 px de plus que la base de 36 px), toujours 38 px pour les rôles et 34 px pour les recherches. Les zones de texte suivent leur nombre de lignes natif et restent redimensionnables, sans imposer une grande hauteur minimale. Les libellés flottants ne changent pas la taille des boutons.

**Forms → Floating dropdown** présente `FloatingSelect` : une liste fournisseur de 38 px, avec sélection au clavier, fermeture par Échap, restitution du focus, désactivation et erreurs. Les paramètres IA utilisent cette liste et les champs flottants partagés, avec une zone distincte pour l’activation et la confidentialité. **Application UI → Organization settings → Compact floating controls** protège la hauteur, la transparence des libellés et l’unique bordure de focus face aux styles du formulaire. Réutilisez les contrôles partagés pour tout nouveau champ texte ; ne recréez pas leur géométrie dans les modules.

Les styles génériques des formulaires doivent exclure les libellés appartenant à `.floating-control` : ils ne doivent ni les transformer en grille ni placer l’astérisque requis sur une seconde ligne. **Forms → Floating fields → Host form contexts** vérifie les champs texte, multiligne et mot de passe dans les conteneurs d’authentification, de dialogue, de profil et de rôle. **Application UI → Authentication → Sign in / French sign in / Dark sign in** teste la vraie page de connexion au focus, à la saisie, à la perte du focus et après effacement. Conservez ces tests d’intégration lors des changements CSS. Ces styles sont livrés dans le template : les nouvelles applications React générées utilisent les mêmes contrôles et règles.

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

**Application UI → Member invitations → Select workspace role / Roles loading / Roles unavailable** présente le choix des rôles autorisés et distingue le chargement d’une erreur. Une requête échouée propose de réessayer au lieu d’annoncer qu’aucun rôle n’existe. Le serveur garde l’autorité sur les rôles attribuables ; un catalogue vide ne permet pas de contourner les contrôles de délégation.

Les champs texte d’invitation, de profil, d’organisation, de fédération, de MFA et de l’assistant réutilisent les contrôles flottants partagés, ainsi que les champs CRUD, les saisies de workflow et les recherches des nouveaux modules générés. Cases à cocher, boutons radio, fichiers, couleurs, listes et champs cachés d’autocomplétion restent natifs. Mettre à jour le template installé ne réécrit pas les applications déjà générées.

Placez `*.stories.tsx` près du code des packages partagés, de l’hôte ou dans `Web/src` d’un module. Les stories à la racine de `Web` sont également découvertes. Importez `Meta` et `StoryObj` depuis `@storybook/react-vite` et utilisez le vrai composant.

Chaque package concerné déclare `@storybook/react-vite`, `storybook` et éventuellement `msw` en dépendances de développement. N’importez jamais les fixtures dans le code de production. Le service worker reste dans `web/apps/storybook/public`.

Déclarez les handlers MSW dans `parameters.msw.handlers.api`. Les valeurs par défaut de session, de protection antiforgery et d’autorisations vides sont centrales ; remplacez leur groupe nommé si nécessaire. Les requêtes `/api` ou `/connect` non prévues échouent sans contacter le backend. Chaque story possède un cache de requêtes, un routeur mémoire et un état de récupération de sécurité isolés. Ces autorisations fictives ne modifient pas celles de production.

Testez les interactions avec `play` et `storybook/test`. Présentez les états de chargement, vide, validation, accès limité et conflit lorsque l’interface les supporte. Ne prétendez pas supporter le redimensionnement ou le réordonnancement des colonnes. Storybook complète les tests backend et de bout en bout.
