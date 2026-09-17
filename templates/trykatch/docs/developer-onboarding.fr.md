# Bien démarrer le développement

[English](developer-onboarding.md)

Pour un parcours concret, suivez [du premier démarrage à la première fonctionnalité](first-feature.fr.md) : contrôles, création Equipment, lecture du résultat et vérification.

Commencez ici après avoir généré l’application. Ce parcours explique comment comprendre l’architecture, créer un module, connecter backend et frontend, intégrer des capacités et vérifier les changements. Il fonctionne aussi sans assistant IA.

Utilisez votre assistant de développement habituel avec ce dépôt et sa propre configuration. Ce kit ne nécessite aucune clé IA, extension ou conversation intégrée supplémentaire. Les règles de confidentialité et les éventuels coûts de votre assistant restent applicables. Ne partagez pas de secrets, données privées ou code sensible sans autorisation. La découverte automatique dépend de l’outil : demandez explicitement la lecture des fichiers. Un outil de chat sans accès au dépôt ne peut pas inspecter le code ; fournissez seulement des extraits non sensibles que vous avez vérifiés.

## 1. Premier message pour votre assistant

Ouvrez la racine contenant `Trykatch.slnx` et `trykatch.modules.json`, puis copiez :

```text
Lis AGENTS.md et docs/developer-onboarding.fr.md. Inspecte le code réel,
le catalogue de modules et les compétences locales avant de répondre.
Identifie l’espace de noms, les modules activés, la propriété des données
et la présence de web/package.json. Utilise les faits des modules si la CLI
locale est disponible ; signale les prérequis manquants sans les réparer.
Explique l’architecture, trace une requête existante et présente les étapes
pour créer un module et connecter son frontend uniquement s’il est installé.
Distingue le code inspecté, la documentation et les exemples proposés.
Ne modifie aucun fichier, ne lance ni migration, ni service, ni installation,
ne lis aucun magasin de secrets et n’appelle aucun fournisseur IA.
Termine par des questions utiles et les commandes de vérification adaptées.
Demande quelle fonctionnalité je souhaite construire ensuite.
```

Ce message demande une orientation, pas une implémentation. Donnez ensuite une demande explicite avec vos règles métier. Les cinq [compétences locales](../AGENTS.md) couvrent spécification, création, extension, revue et vérification, sans installation globale.

## 2. Démarrer et découvrir les modules

Suivez le [README généré](../README.md) pour les prérequis et le démarrage local. Travaillez depuis la racine, pas `web/`. Démarrez via Aspire AppHost plutôt que l’API seule : il coordonne les rôles de base de données, migrations et ressources installées. Utilisez les URL de ses ressources, sans deviner les ports. Le déploiement suit les guides [base de données](database.md) et [identité de production](production-identity.md) ; les comptes de démonstration ne sont pas des identifiants de production.

La CLI locale correspond aux contrats de cette application :

```bash
dotnet run --project tools/Trykatch.ModuleTool -- module list
dotnet run --project tools/Trykatch.ModuleTool -- module facts
dotnet run --project tools/Trykatch.ModuleTool -- module doctor
dotnet run --project tools/Trykatch.ModuleTool -- module create --help
```

`module facts` expose les déclarations validées : versions, activation, permissions, extensions et points d’entrée. Une déclaration n’accorde aucun droit et ne prouve pas l’existence d’un adaptateur exécutable. Ne supposez pas que tous les exemples sont activés ou qu’une autre version de CLI propose les mêmes options.

## 3. Comprendre les responsabilités

Consultez [les modules](modules.md), [le backend](development/backend.md) et [la sécurité](development/security.md). Domain contient les invariants ; Application les cas d’usage et autorisations ; Presentation les endpoints ; Infrastructure la persistance et l’enregistrement ; IntegrationEvents les contrats publics entre modules.

L’hôte référence l’entrée Infrastructure ; Presentation ne référence jamais Infrastructure. N’importez pas l’implémentation privée d’un autre module. Organization et l’acteur proviennent du contexte authentifié, pas de champs modifiables. Tracez les permissions, filtres EF et politiques RLS forcées dans la transaction réelle. L’isolation nécessite des tests PostgreSQL réels, pas une base en mémoire.

## 4. Créer une capacité

Exemple de CRUD appartenant à une organisation :

```bash
dotnet run --project tools/Trykatch.ModuleTool -- module create Equipment \
  --entity EquipmentItem --resource equipment_items --ownership organization \
  --fields "name:string:required:max(120),dailyRate:decimal:required"
```

Choisissez des noms et ressources libres. Ajoutez `--with-web` seulement si `web/package.json` existe et que vous voulez l’interface React générée. Les applications sans frontend ignorent les étapes frontend ; un dossier `Web` de module ne signifie pas qu’un hôte web est installé.

La création prépare, enregistre, compile et teste le scaffold, avec restauration des changements en cas d’échec. Ne modifiez pas simultanément le workspace. Le code du module est personnalisable ; les registres et clients générés ne le sont pas. Ne relancez pas la création sur un module personnalisé.

Pour des transitions gardées et décisions de revue, étudiez [le blueprint fourni](../blueprints/shipment-reception.json) et [la référence CLI](../tools/Trykatch.ModuleTool/README.md), puis validez un blueprint adapté. Un champ CRUD ne garantit pas un workflow. Relations, règles entre enregistrements/modules, propriété plateforme et comportements non pris en charge nécessitent du code spécifique et des tests, pas des options inventées. Un scaffold n’est pas un produit métier terminé.

## 5. Relier backend et frontend

`trykatch.modules.json` et les manifestes versionnés déclarent la composition. Lancez `module generate` après un changement de ces déclarations ; la CLI possède les registres API/migrateur et web lorsqu’il est installé. Préservez les identifiants publics de modules, opérations, permissions et extensions.

Suivez [le guide frontend](development/frontend.md). L’entrée web déclarée compose routes et navigation ; `web/apps/web/src/module-overrides.ts` reçoit les personnalisations de l’application. Utilisez le client TanStack généré et les protections cookie/antiforgery, jamais des tokens d’accès ou de rafraîchissement dans le navigateur. Recompilez l’API avant de régénérer le client OpenAPI :

```bash
dotnet build Trykatch.slnx
corepack pnpm --dir web install --frozen-lockfile
corepack pnpm --dir web generate
corepack pnpm --dir web generate:check
corepack pnpm --dir web typecheck
corepack pnpm --dir web test
corepack pnpm --dir web build
```

Sans frontend, ignorez les commandes pnpm. Ne modifiez pas manuellement les clients/registres générés. Préservez les décimaux/entiers 64 bits transportés en chaînes et les dates UTC. Les contrôles UI ne remplacent pas l’autorisation serveur. Prévoyez chargement, vide, refus, validation, conflits et messages anglais/français.

## 6. Intégrer les capacités

Utilisez les contrats IntegrationEvents ou points d’extension publiés, après inspection des contrats installés. Enregistrez les changements métier, audits et événements outbox requis dans la même transaction. N’importez pas les repositories privés. Étudiez [l’outbox transactionnelle](adr/0010-transactional-outbox.md), puis concevez des consommateurs idempotents ; un descripteur d’événement ne réalise pas seul une intégration.

Pour un service externe, définissez contrat, données autorisées, secrets serveur, délais, politique de reprise/idempotence et récupération avant l’adaptateur. Le template ne connecte pas automatiquement une API arbitraire et ne fournit pas vos règles métier. Une mise à jour du template/CLI ne modifie pas les applications déjà générées ni leur code personnalisé.

## 7. Questions utiles

- « Trace une requête Projects depuis React ou HTTP jusqu’aux permissions, Domain et PostgreSQL, avec les chemins source. »
- « Explique Organization, User et Membership dans cette application. »
- « Montre les versions, permissions et extensions des modules activés avec module facts. »
- « Où placer cette règle métier et quels tests révéleraient une violation ? »
- « Aide-moi à spécifier ma fonctionnalité et identifie les limites du générateur. »
- « Lis .agents/skills/trykatch-build-module/SKILL.md et implémente mon brief approuvé, avec frontend seulement s’il est installé. »
- « Comment ajouter une permission et des droits par défaut sûrs ? »
- « Comment relier manifestes, endpoints, OpenAPI, routes et navigation ? »
- « Comment faire communiquer deux modules via IntegrationEvents et l’outbox sans dépendances privées ? »
- « Comment intégrer cette API externe et traiter les doublons ou échecs ? »
- « Comment créer une migration immuable et tester l’isolation entre organisations ? »
- « Lis .agents/skills/trykatch-extend-module/SKILL.md et étends le module existant sans écraser mes personnalisations. »
- « Lis .agents/skills/trykatch-review/SKILL.md et révise mes changements selon leur spécification sans modifier de fichiers. »
- « Lis .agents/skills/trykatch-verify/SKILL.md et distingue contrôles réussis, échoués, ignorés et non exécutés. »

## 8. Vérifier le résultat

Suivez [la matrice de vérification](development/verification.md) : compilation, tests métier/architecture, HTTP, PostgreSQL réel et frontend/navigateur selon les changements. Une génération réussie ne certifie pas toutes les règles personnalisées ou le déploiement de production. Relancez les contrôles affectés après personnalisation et gardez les guides/spécifications à jour. Ne désactivez pas un test pour prétendre au succès.

Ce parcours concerne les développeurs dans le dépôt. L’aide IA intégrée reste une fonctionnalité produit distincte, en lecture seule, avec ses protections et configuration existantes. Ce kit ne l’active pas et ne lui donne aucun accès au code. L’aide métier multilingue future nécessite les guides approuvés du produit et des tests linguistiques adaptés, pas seulement les documents d’architecture du template.
