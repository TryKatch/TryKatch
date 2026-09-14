---
title: Créer un module
description: Ajoutez une capacité sans affaiblir le noyau de sécurité ni créer de couplage d’exécution caché.
---

Un module Trykatch est un ensemble fonctionnel full-stack doté d’un contrat explicite. Projects illustre une fonctionnalité intégrée, Federation un adaptateur de plateforme optionnel, et Documents démontre un module de données d’organisation distribué séparément couvrant .NET, React, les migrations, les permissions, l’audit et le cycle de vie.

## Ce qu’un module peut fournir

- des services API et des contrôleurs ;
- la configuration du modèle EF Core et les migrations ;
- des permissions stables et des rôles par défaut sûrs ;
- des routes React, la navigation et des extensions d’interface nommées ;
- des événements d’outbox, des abonnés et des workers ;
- des outils d’assistance explicitement autorisés.

## Ce qui reste centralisé

L’identité, la résolution de l’organisation, la RLS, la protection antiforgery, l’application des permissions, l’intégrité de l’audit et la validation des modules ne sont pas des points d’extension.

## Générer un module backend

Exécutez le générateur depuis la racine d’une application déjà créée avec Trykatch — le dossier qui contient la solution, `src/`, `tests/` et `trykatch.modules.json` :

```bash
cd /chemin/vers/Horizon
trykatch module create Invoicing \
  --entity Invoice \
  --resource invoices \
  --ownership organization
```

`Invoicing` et `Invoice` doivent être des identifiants .NET en PascalCase. `invoices` doit être un identifiant PostgreSQL explicite en snake_case minuscule. La version 1 exige volontairement `--ownership organization` et ne devine jamais la frontière de sécurité.

## Générer un module full-stack

Ajoutez `--with-web` pour inclure une interface React Query de consultation, création et modification, la navigation, l’intégration aux archives et des messages anglais/français appartenant au module :

```bash
trykatch module create Invoicing \
  --entity Invoice \
  --resource invoices \
  --ownership organization \
  --description "Gestion des factures de l’organisation." \
  --with-web
```

Utilisez `trykatch module create --help` pour afficher le contrat complet de la commande.

## Fichiers générés

```text
src/Modules/Invoicing/
├── Horizon.Modules.Invoicing.Domain/
├── Horizon.Modules.Invoicing.Application/
├── Horizon.Modules.Invoicing.IntegrationEvents/
├── Horizon.Modules.Invoicing.Presentation/
├── Horizon.Modules.Invoicing.Infrastructure/
├── Web/                         # uniquement avec --with-web
├── trykatch.module.json
└── README.md
tests/Modules/Invoicing/
├── Horizon.Modules.Invoicing.UnitTests/
└── Horizon.Modules.Invoicing.ArchitectureTests/
```

La commande ajoute aussi les projets à la solution, enregistre l’Infrastructure auprès de l’API et du migrateur, ajoute et active l’entrée du catalogue, régénère les registres, restaure les dépendances, compile le backend, exécute les tests générés et le diagnostic des modules. Avec `--with-web`, elle génère également le client OpenAPI puis exécute le typage, les tests et le build de production du frontend.

L’opération est atomique. Le rendu se fait dans un répertoire privé de préparation. Si l’enregistrement, la restauration, la compilation, les tests, la génération du client ou la validation échoue, Trykatch restaure le catalogue, la solution, les projets, les registres, les sorties OpenAPI/client et les lockfiles, puis supprime le nouveau module. Une commande identique répétée signale que le module existe déjà sans rien modifier ; la version 1 ne propose aucun écrasement.

## Contrat de sécurité généré

L’entité générée implémente `IOrganizationOwned`. L’hôte applique le filtre d’organisation nommé et valide `OrganizationId` ainsi que l’index commençant par l’organisation. La migration PostgreSQL crée `app.invoices`, active et force la RLS, puis définit `invoices_organization_isolation` avec `USING` et `WITH CHECK`. L’API expose :

```text
GET    /api/v1/invoices/
GET    /api/v1/invoices/{id}
POST   /api/v1/invoices/
PUT    /api/v1/invoices/{id}
POST   /api/v1/invoices/{id}/archive
POST   /api/v1/invoices/{id}/restore
DELETE /api/v1/invoices/{id}
```

Les lectures exigent `invoicing.read` et les mutations `invoicing.manage`. Les cas d’utilisation répètent l’autorisation, les mutations exigent la protection antiforgery et les écritures créent les preuves d’audit et d’outbox dans la transaction de l’hôte.

## Démarrer et vérifier le résultat

Depuis la racine de l’application :

```bash
trykatch start
```

Ouvrez le tableau de bord Aspire, sélectionnez la ressource **api**, puis utilisez son URL HTTPS. `/docs` ouvre Scalar et `/openapi/v1.json` expose le contrat généré en environnement Development. Avec `--with-web`, ouvrez `/invoices` sur la ressource React. Pour relancer les contrôles déterministes :

```bash
dotnet build Horizon.slnx
dotnet test tests/Modules/Invoicing/Horizon.Modules.Invoicing.UnitTests
dotnet test tests/Modules/Invoicing/Horizon.Modules.Invoicing.ArchitectureTests
corepack pnpm --dir web typecheck
corepack pnpm --dir web test
corepack pnpm --dir web build
trykatch module doctor
```

## Étendre l’entité générée sans risque

Ajoutez le comportement métier dans l’entité plutôt que des setters publics. Ajoutez des champs explicites aux requêtes/DTO et leur validation dans Application, mappez la persistance dans le contributeur de modèle du module et créez une nouvelle migration immuable et uniquement progressive. Conservez tout accès d’organisation derrière `IOrganizationModuleData` ; n’injectez jamais un DbContext de l’hôte et n’acceptez jamais un identifiant d’organisation venant d’une requête. Préservez les noms d’opérations, permissions, événements, table et politique RLS stables sauf si vous versionnez volontairement ce contrat public.

Les modules persistants doivent également respecter le [contrat d’isolation des données des modules](/fr/architecture/module-data-isolation/). Aucun module ne peut désactiver le cloisonnement par organisation ni accéder directement aux contextes de base de données de l’hôte.

```bash
trykatch module list --root ./Horizon
trykatch module doctor --root ./Horizon
```

:::caution
Le générateur crée des modules source dans une application existante. Il ne transforme pas des packages non fiables en plugins d’exécution arbitraires ; les packages distribués séparément passent toujours par le cycle de vie signé et la même validation à la compilation.
:::
