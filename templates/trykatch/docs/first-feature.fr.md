# Du premier démarrage à la première fonctionnalité

[English](first-feature.md) · [Accueil développeur](developer-onboarding.fr.md)

Créez un petit catalogue Equipment pour comprendre le parcours complet d'un module. Cet exemple pédagogique CRUD appartient à une organisation ; il ne réalise pas un produit de location. Disponibilité, réservations, facturation et tarif non négatif nécessitent des règles et tests supplémentaires. Utilisez une application fraîchement générée et des données jetables, jamais la production. Aucune clé IA n'est nécessaire.

## 1. Vérifier avant de démarrer

Depuis la racine contenant `Trykatch.slnx` et `trykatch.modules.json`, utilisez la CLI locale :

```bash
dotnet run --project tools/Trykatch.ModuleTool -- doctor
dotnet run --project tools/Trykatch.ModuleTool -- setup
dotnet run --project tools/Trykatch.ModuleTool -- status
```

Si la CLI ne compile pas, commencez par les [prérequis du README](../README.md). `doctor` vérifie SDK, Docker, présence du certificat et graphe des modules. Avec web, il vérifie Node, Corepack et les versions pnpm sélectionnées par Corepack **et** PATH selon `web/package.json`. Un pnpm global différent peut bloquer la génération même si une installation manuelle réussit : corrigez l'écart sans contourner le contrôle. Le parent peut déclarer un autre `packageManager` ; restez dans l'application. Voir la [référence CLI](../tools/Trykatch.ModuleTool/README.md).

`setup` restaure les dépendances verrouillées, sans secrets, démarrage, migrations ni approbation de certificats. Sans frontend, les étapes frontend sont ignorées. La présence d'un certificat ne prouve pas sa confiance : effectuez la configuration de développement nécessaire sans désactiver TLS.

Point de contrôle : `doctor` et `setup` réussissent. Sans URL, `status` affiche `Runtime health: unknown` : compilation et configuration ne prouvent pas que l'application tourne.

## 2. Prouver le démarrage

```bash
dotnet run --project tools/Trykatch.ModuleTool -- start
```

Gardez le terminal ouvert. AppHost orchestre PostgreSQL, migrateur ponctuel, API et web installé. Relevez les URL dans Aspire, sans supposer les ports. Dans un autre terminal, remplacez le paramètre par l'origine API réelle (protocole, hôte, port seulement) :

```bash
dotnet run --project tools/Trykatch.ModuleTool -- status --api-url <origine-API-dans-Aspire>
```

Point de contrôle : `/health/live` et `/health/ready` retournent HTTP 200. Sinon, consultez les journaux de la ressource concernée. Une page Vite ou un tableau de bord ne prouve pas la disponibilité API. Ouvrez web, connectez-vous avec le Owner de développement du README et vérifiez Projects. Sans frontend, suivez les sections 1–2 du [parcours HTTP](backend-only-http.fr.md) : identifiants de démonstration, connexion cookie, antiforgery renouvelé et sélection d'espace. Déconnectez-vous avant d'arrêter AppHost. Il n'existe ni page React ni client API généré.

Arrêtez AppHost avec Ctrl+C avant génération pour éviter une composition périmée et des compilations concurrentes. Ne terminez pas les autres applications.

## 3. Générer Equipment

```bash
dotnet run --project tools/Trykatch.ModuleTool -- module facts
dotnet run --project tools/Trykatch.ModuleTool -- module create Equipment \
  --entity EquipmentItem --resource equipment_items --ownership organization \
  --fields "name:string:required:max(120),dailyRate:decimal:required" \
  --with-web
```

Omettez `--with-web` sans `web/package.json`. Si Equipment ou sa relation existent déjà, inspectez le code sans relancer la création sur du code personnalisé. Organisation et acteur viennent du contexte authentifié, jamais de champs éditables.

Point de contrôle : étapes `OK`, puis résumé des fichiers/endpoints/permissions. Le générateur enregistre et active, restaure, compile et exécute tests unitaires/architecture du module. Avec web, il génère le client OpenAPI, vérifie types, teste et construit le frontend. Cela ne prouve pas encore le CRUD réel ni toutes les règles métier. Sur `FAIL`, conservez les diagnostics et vérifiez le rollback annoncé avant de corriger/réessayer. Ne désactivez pas de tests et ne lancez pas de mutations concurrentes.

## 4. Comprendre le résumé

- `src/Modules/Equipment/` : source éditable ; Domain porte invariants, Application cas d'usage autorisés, Presentation HTTP, Infrastructure persistance/composition, IntegrationEvents contrats publics.
- Endpoints : opérations sécurisées à retrouver dans `/docs` après redémarrage ; inspectez les routes/identifiants réels.
- Permissions : définitions, pas des droits pour tous ; lisez manifest/provider et contrôlez lecture/gestion côté serveur.
- Route `/equipment_items` : Web du module possède formulaires, navigation et traductions anglaises/françaises.
- Registres, lockfiles et client API : artefacts machine ; changez source/manifest puis régénérez, jamais ces fichiers à la main.

Lisez catalogue, manifest, migration et tests. `name` est obligatoire, limité à 120 caractères. `dailyRate` utilise `numeric(18,2)` avec une **chaîne** JSON invariante, pas un nombre JavaScript. RLS forcé et contexte transactionnel isolent par organisation. Lisez [l'isolation](module-data-isolation.md) avant de changer la persistance. Utilisez IntegrationEvents/outbox, pas les repositories privés d'autres modules.

Les catalogues d'interface EN/FR ne traduisent pas automatiquement vos termes métier. Relisez le fichier éditable `src/Modules/Equipment/Web/src/messages.ts` : remplacez notamment la valeur française initiale `Daily rate` de `fieldDailyRate` par `Tarif journalier`, puis adaptez les mots Equipment/entité avant le contrôle des langues. Ignorez cette étape sans web et relancez les contrôles frontend affectés après modification.

```bash
dotnet run --project tools/Trykatch.ModuleTool -- module facts
dotnet run --project tools/Trykatch.ModuleTool -- module doctor
```

Point de contrôle : Equipment activé, graphe sain. Une permission déclarée ne prouve pas les droits d'un compte.

## 5. Tester en fonctionnement

Redémarrez avec `start` puis contrôlez `status --api-url`. Le migrateur applique la migration avant l'API. Avec le Owner, créez `Training excavator` à `125.50` sur `/equipment_items`, actualisez, modifiez le tarif puis actualisez encore. Vérifiez persistance, nom obligatoire/longueur et format décimal. Archivez puis restaurez cet enregistrement jetable via Archive. Passez anglais/français et contrôlez page, formulaire et navigation.

Sans frontend, répétez connexion et sélection d'espace puis exécutez les requêtes Equipment création/lecture/modification/archivage/restauration du [parcours HTTP](backend-only-http.fr.md). Il utilise curl, pas un client généré inexistant ; n'inventez pas un endpoint de token ou password grant. Provisionnez séparément des comptes avec droits lecture/gestion générés : lecture seule interdit mutations, aucun droit interdit accès, seconde organisation ne peut lire/modifier l'ID de la première. Masquer un bouton n'autorise rien ; un administrateur PostgreSQL ne prouve pas RLS. Les refus doivent conserver cohérence métier/audit/outbox. Nettoyez uniquement les données jetables par le cycle de vie supporté.

Point de contrôle : notez réussites, échecs et non exécutés. Tests générés ne remplacent pas l'exercice HTTP/navigateur réel.

## 6. Vérifier puis étendre

Suivez la [matrice](development/verification.md). Réutilisez les contrôles du générateur jusqu'à un changement de code. Après personnalisation, recompilez et relancez tests affectés et architecture hôte. Tests PostgreSQL réels doivent s'exécuter, pas être ignorés :

```bash
dotnet test tests/Trykatch.IntegrationTests \
  --filter FullyQualifiedName~EveryDeclaredOrganizationRelationIsDefaultDenyUnderTheRealRuntimeRole
```

Cela inspecte les relations sous le rôle runtime réel, pas le parcours HTTP Equipment. Ajoutez les cas API/intégration métier. Avec web, recompilez l'API et utilisez `generate` pour régénérer client OpenAPI et contrat assistant. Vérifiez ensuite la fraîcheur du contrat assistant :

```bash
corepack pnpm --dir web generate:check
```

`generate:check` vérifie uniquement le contrat assistant, pas la fraîcheur du client TypeScript. Relisez le client régénéré par rapport à la référence de votre tâche, fichiers nouveaux/non suivis inclus, puis types/tests/build selon la matrice. La CI compare les sorties suivies après régénération ; un `git diff` vide ne valide pas seul des fichiers nouvellement générés non suivis. Ne corrigez pas le client généré à la main.

Prêt à étendre signifie disponibilité prouvée, couches comprises, contrôles réels CRUD/validation/permissions/langues documentés et tests choisis réussis. Faites ensuite suivre ce parcours sans coaching par un développeur découvrant Trykatch ; l'automatisation ne prouve pas seule la facilité d'apprentissage.

Question à votre assistant :

```text
Lis AGENTS.md et docs/first-feature.fr.md. Inspecte Equipment et explique le
parcours d'une requête avec références au code. Propose où placer un tarif non
négatif et quels tests le prouvent. Ne change pas de fichiers et ne démarre pas
de services avant mon accord.
```
