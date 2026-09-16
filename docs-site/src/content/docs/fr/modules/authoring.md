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

### Suivre la progression

La commande affiche un indicateur animé, le numéro de l’étape et le temps écoulé pendant la validation, la génération, l’enregistrement, la restauration des dépendances, la compilation, les tests et le diagnostic des modules. Avec `--with-web`, elle affiche aussi l’installation des dépendances frontend, la génération du client API, la vérification des types, les tests et la compilation frontend. Chaque étape terminée porte la mention `OK` ; un récapitulatif final présente les chemins, les endpoints, les permissions et la commande de démarrage. Il s’agit d’étapes réelles, pas d’un pourcentage ou d’une durée de fin estimés.

Par exemple, pendant la restauration des dépendances .NET :

```text
| [6] Restoring .NET dependencies... (12.4s)
```

Une sortie redirigée ou un terminal avec `TERM=dumb` utilise des lignes simples sans animation. En cas d’échec, l’étape est marquée `FAIL`, le retour à l’état initial est annoncé et les détails du diagnostic sont conservés. Ctrl+C arrête la commande enfant et déclenche le même retour à l’état initial du workspace.

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
GET    /api/v1/invoices/page
GET    /api/v1/invoices/{id}
POST   /api/v1/invoices/
PUT    /api/v1/invoices/{id}
POST   /api/v1/invoices/{id}/archive
POST   /api/v1/invoices/{id}/restore
DELETE /api/v1/invoices/{id}
```

Les lectures exigent `invoicing.read` et les mutations `invoicing.manage`. Les cas d’utilisation répètent l’autorisation, les mutations exigent la protection antiforgery et les écritures créent les preuves d’audit et d’outbox dans la transaction de l’hôte. Les handlers Minimal API utilisent des unions de résultats typés et des noms d’opération OpenAPI stables (`Invoicing_List` à `Invoicing_RequestDeletion`). L’outbox publie cinq contrats immuables distincts — `InvoiceCreated`, `InvoiceUpdated`, `InvoiceArchived`, `InvoiceRestored` et `InvoiceDeletionRequested` — au lieu d’une chaîne d’opération libre.

## Démarrer et vérifier le résultat

Les listes générées utilisent `/page?lifecycle=active&page=1&pageSize=25&search=invoice&sort=newest`. La réponse contient `items`, `page`, `pageSize` et `hasMore`. La taille est limitée à 1–100, la recherche à 200 caractères et le tri à `newest` ou `oldest` (date de création puis ID). La recherche des champs texte s’effectue en SQL avant la pagination, sans désactiver l’isolation organisationnelle. L’ancien endpoint tableau reste disponible pour les consommateurs existants et les archives, pas pour les grandes listes.

Les modifications, archivages, restaurations et demandes de suppression exigent maintenant `expectedVersion`. Un jeton absent échoue à la désérialisation (400) ; un jeton périmé retourne 409 avec `code: stale_version`. EF rejette aussi les écritures concurrentes. Le formulaire conserve les saisies et propose un chargement explicite de la version récente. Ces contrats concernent les nouveaux modules ; une mise à jour du CLI ne modifie pas les modules ni les migrations existants.

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

## Générer un processus métier à partir d’un blueprint

L’application doit déclarer `business-blueprints-v1` dans `hostCapabilities` de son catalogue. Une mise à jour du CLI ou du package de template ne modifie pas une application existante. Ne rajoutez pas ce marqueur pour contourner le contrôle : générez une application avec la version coordonnée, ou migrez et testez le gestionnaire HTTP des erreurs de liaison, le SDK Archive avec version et les libellés de navigation EN/FR avant de déclarer cette capacité.

Utilisez un blueprint JSON pour décrire les règles métier, pas seulement les champs modifiables. L’exemple livré est `blueprints/shipment-reception.json`. Exécutez ces commandes **à la racine de l’application générée**, et non dans `web/` :

```bash
cd /chemin/vers/Horizon
trykatch module validate --blueprint blueprints/shipment-reception.json
trykatch module create ShipmentReceptions --blueprint blueprints/shipment-reception.json --with-web
trykatch start
```

La validation ne modifie aucun fichier. La création ajoute, enregistre, compile et teste le module. Omettez `--with-web` pour générer seulement le backend. Ne combinez pas `--blueprint` avec `--fields`, `--entity`, `--resource`, `--ownership` ou `--description` : le blueprint déclare ces informations.

Dans Aspire, ouvrez la ressource **web**, puis `/shipment_receptions`. La ressource **api**, chemin `/docs`, expose les opérations dans Scalar. Démarrez Docker avant `trykatch start` : PostgreSQL reste nécessaire même sans la pile d’observabilité facultative.

### Règles de l’exemple

- Une réception commence en **Draft** (brouillon), avec une référence obligatoire et des poids reçu/expédié strictement positifs.
- Le brouillon peut être modifié puis **soumis** à vérification.
- Une réception soumise peut être **acceptée** seulement si les documents sont vérifiés, ou **rejetée** avec un motif de 10 à 500 caractères.
- Une décision finale ne peut pas être rouverte en modifiant un champ. Archiver/restaurer change la visibilité, pas la décision métier.
- La soumission exige `shipment-receptions.submit` ; les décisions exigent `shipment-receptions.review`, accordée par défaut aux administrateurs, pas aux membres ordinaires.
- Les modifications, transitions et opérations de récupération exigent `expectedVersion`. Une version obsolète produit HTTP 409. Le formulaire conserve les saisies et propose de charger explicitement la version récente avant un nouvel essai.

Ces règles sont imposées côté serveur. Masquer un bouton React n’est jamais une mesure d’autorisation suffisante. Le contrat CRUD ne permet pas d’affecter directement l’état métier.

### Contrat JSON v1

La racine déclare `schemaVersion: 1`, `module`, `entity`, `resource`, `ownership: "organization"`, les libellés singulier/pluriel anglais/français dans `labels`, les `fields` et le `workflow`.

Les champs réutilisent les types CRUD. Ils déclarent un `label` traduit, `required`, des limites textuelles `minimumLength`/`maximumLength` ou numériques `minimum`/`maximum` et `exclusiveMinimum`. Les bornes doivent être représentables ; les décimaux conservent la précision exacte `numeric(18,2)`.

Le workflow contient les `states` nommés et traduits, un `initialState`, les `editableStates` et les `actions`. Une action déclare `name`, `from`, `to`, `permission`, `label` et, facultativement, `inputs`, `guards`, `assignments`. Chaque état doit être accessible depuis l’état initial.

Les gardes sont des données, pas du code exécutable. Opérateurs : `eq`, `ne`, `gt`, `gte`, `lt`, `lte`, `notEmpty`, `all`, `any`. Une garde référence un `field` ou un `input`, avec une valeur littérale `value` du bon type ou un `compareToField` du même type. Les groupes contiennent `rules`. Chaque garde fournit un `code` stable et un `message` anglais/français. Exemple de règle facultative :

```json
{
  "op": "lte",
  "field": "receivedWeight",
  "compareToField": "dispatchedWeight",
  "code": "received_weight_exceeds_dispatch",
  "message": {
    "en": "Received weight cannot exceed dispatched weight.",
    "fr": "Le poids reçu ne peut pas dépasser le poids expédié."
  }
}
```

**Limites volontaires de v1 :** permissions d’action `submit` ou `review` ; entrées d’action textuelles bornées ; affectation d’une entrée déclarée à `decisionReason` uniquement. Aucun script arbitraire, relation, écriture intermodule, remplacement automatique du code personnalisé ou clé d’idempotence durable. Ajoutez les comportements spécifiques dans le domaine/l’application, avec des tests et une migration évolutive si nécessaire. Un générateur ne certifie pas qu’une application est prête pour toutes les entreprises.

Le blueprint normalisé est conservé dans `src/Modules/ShipmentReceptions/module.blueprint.json`. Les couches restent isolées, avec filtre EF centralisé, RLS PostgreSQL forcée, permissions, antiforgery et audit/outbox transactionnels. Les transitions utilisent un état de domaine privé et la concurrence optimiste EF. React utilise le client OpenAPI généré.

### Vérifier le résultat

```bash
# Depuis la racine de l’application générée :
dotnet test tests/Modules/ShipmentReceptions/Horizon.Modules.ShipmentReceptions.UnitTests
dotnet test tests/Modules/ShipmentReceptions/Horizon.Modules.ShipmentReceptions.ArchitectureTests
trykatch module doctor
```

Remplacez `Horizon` par l’espace de noms de votre application. Les tests générés vérifient les invariants de cycle de vie et de version ; ajoutez les tests de vos règles métier. La CI Trykatch exécute également un scénario HTTP indépendant de réception et des tests PostgreSQL interorganisation sur des applications fraîchement générées.
