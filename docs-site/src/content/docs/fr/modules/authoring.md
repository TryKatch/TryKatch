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
  --ownership organization \
  --fields "number:string:required:max(40),total:decimal:required,dueDate:date:required,status:enum(Draft,Sent,Paid):required,notes:string:optional:max(2000)"
```

`Invoicing` et `Invoice` doivent être des identifiants .NET PascalCase portables : Trykatch rejette aussi les noms réservés de l’hôte et de Windows comme `CON`, `AUX`, `COM1` et `LPT1`. `invoices` doit être un identifiant PostgreSQL explicite en snake_case minuscule, ne doit pas être un mot-clé PostgreSQL et ne doit pas dupliquer une relation du schéma `app` déclarée par un autre module enregistré. La version 1 exige volontairement `--ownership organization` et ne devine jamais la frontière de sécurité. Ces contrôles s’exécutent avant toute préparation ou modification du workspace.

## Générer un module full-stack

Ajoutez `--with-web` pour inclure une interface React Query de consultation, création et modification, la navigation, l’intégration aux archives et des messages anglais/français appartenant au module :

```bash
trykatch module create Invoicing \
  --entity Invoice \
  --resource invoices \
  --ownership organization \
  --fields "number:string:required:max(40),total:decimal:required,dueDate:date:required,status:enum(Draft,Sent,Paid):required,notes:string:optional:max(2000)" \
  --description "Gestion des factures de l’organisation." \
  --with-web
```

Utilisez `trykatch module create --help` pour afficher le contrat complet de la commande.

## Décrire les champs métier une seule fois

`--fields` est la forme métier de référence pour le CRUD généré. Le générateur l’applique de façon cohérente à l’entité du domaine, aux contrats de création et de modification, au DTO, à la validation, à la configuration EF Core, à la migration PostgreSQL, au document OpenAPI et—avec `--with-web`—au tableau, au formulaire, à la vue détaillée et aux catalogues de messages anglais/français de React.

Chaque définition utilise `lowerCamelCase:type`, suivie de modificateurs facultatifs. Les champs sont obligatoires par défaut ; utilisez `optional` lorsque `null` est une valeur métier valide. Les chaînes acceptent `max(longueur)` de 1 à 10 000. Le contrat accepte jusqu’à 24 champs.

| Type du contrat | Type .NET généré | Contrôle React généré |
| --- | --- | --- |
| `string` | `string` | champ texte ou zone de texte |
| `decimal` | `decimal` avec stockage `numeric(18,2)` | transport sous forme de chaîne invariante et saisie décimale exacte |
| `int` | `int` | champ numérique entier |
| `long` | `long` | transport sous forme de chaîne invariante et saisie entière 64 bits exacte |
| `bool` | `bool` | case à cocher ou sélecteur facultatif |
| `date` | `DateOnly` | champ de date |
| `datetime` | `DateTimeOffset` normalisé en UTC | saisie locale convertie en instant ISO UTC |
| `guid` | `Guid` | champ d’identifiant |
| `enum(Draft,Sent,Paid)` | `InvoiceStatus` fortement typé | sélecteur traduit |

Si `--fields` est omis, le contrat de démarrage compatible reste `name:string:required:max(200),description:string:optional:max(2000)`. Les champs gérés par la plateforme, notamment `Id`, `OrganizationId`, les dates d’audit et les métadonnées de suppression, ne peuvent pas être déclarés ni exposés en écriture.

Les identifiants de champ sont limités à 63 caractères ASCII afin que PostgreSQL ne puisse pas tronquer silencieusement un nom de colonne généré. Les valeurs décimales acceptent au plus 16 chiffres entiers et 2 décimales, conformément à `numeric(18,2)`. Les nombres décimaux et les entiers 64 bits transitent dans JSON sous forme de chaînes invariantes afin d’éviter tout arrondi JavaScript. Les dates-heures générées sont normalisées en UTC avant la persistance et converties entre l’éditeur local du navigateur et le transport ISO UTC.

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

La commande ajoute aussi les projets à la solution dans un ordre déterministe, enregistre l’Infrastructure auprès de l’API et du migrateur, ajoute et active l’entrée du catalogue, régénère les registres, restaure les dépendances, compile le backend, exécute les tests générés et le diagnostic des modules. Avec `--with-web`, elle génère également le client OpenAPI puis exécute le typage, les tests et le build de production du frontend. Le résultat final affiche chaque endpoint, les deux permissions et la commande exacte de démarrage.

L’opération est atomique. Le rendu se fait dans un répertoire privé de préparation, où le manifeste complet et le catalogue projeté sont validés avant l’installation du moindre fichier de module. Si l’édition de la solution, l’enregistrement, la restauration, la compilation, les tests, la génération du client ou le diagnostic échoue — ou si vous interrompez la commande avec Ctrl+C — Trykatch arrête la commande enfant active, restaure le catalogue, la solution, les projets, les registres, les sorties OpenAPI/client et les lockfiles, puis supprime le nouveau module. Une commande identique répétée signale que le module existe déjà sans rien modifier ; la version 1 ne propose aucun écrasement.

L’application de départ active les deux modules de référence pour des objectifs différents : Projects illustre le CRUD ordinaire appartenant à une organisation, tandis que Documents illustre l’envoi et le téléchargement de fichiers isolés par organisation via un stockage privé compatible S3. Documents n’est plus un second clone CRUD : ses métadonnées sont protégées par le filtrage EF et la RLS PostgreSQL, tandis que les octets des fichiers restent hors de PostgreSQL.

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

Les lectures exigent `invoicing.read` et les mutations `invoicing.manage`. Les cas d’utilisation répètent l’autorisation, les mutations exigent la protection antiforgery et les écritures créent les preuves d’audit et d’outbox dans la transaction de l’hôte. Les handlers Minimal API utilisent des unions de résultats typés et des noms d’opération OpenAPI stables (`Invoicing_List` à `Invoicing_RequestDeletion`). L’outbox publie cinq contrats immuables distincts — `InvoiceCreated`, `InvoiceUpdated`, `InvoiceArchived`, `InvoiceRestored` et `InvoiceDeletionRequested` — au lieu d’une chaîne d’opération libre.

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
