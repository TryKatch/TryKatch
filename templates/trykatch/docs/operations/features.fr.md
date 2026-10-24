# Disponibilité des fonctionnalités installées

[English](features.md)

La disponibilité des fonctionnalités permet à un opérateur de plateforme autorisé de suspendre une fonctionnalité déclarée d’un module installé pour l’environnement de déploiement courant ou pour une organisation. Elle est indépendante des permissions, de l’appartenance à une organisation, des droits d’usage et de l’installation du module. Un contrôle de fonctionnalité n’accorde jamais un accès que les vérifications d’autorisation existantes refusent.

## Avant le déploiement

Appliquez la migration de plateforme `20261024160455_AddFeatureAvailabilityControls` avec le migrateur privilégié à exécution unique avant de déployer une API utilisant les contrôles de fonctionnalité. La migration crée `platform.feature_environment_controls`, `platform.organization_feature_overrides` et `platform.feature_control_history`, avec RLS forcée et des droits limités aux profils d’exécution existants de plateforme et d’organisation. Le profil plateforme gère les contrôles et ajoute les entrées d’historique ; le profil organisation lit les règles d’environnement sûres et uniquement la surcharge de son organisation courante. Les rôles d’exécution ne peuvent pas supprimer les lignes de contrôle ni modifier l’historique. Les profils identité, outbox et coordination des tâches n’y ont aucun accès. N’ajoutez pas de rôle d’exécution et ne fournissez pas la connexion du migrateur à l’API. Suivez le [processus de migration de base de données](../database.md).

La politique de déploiement est immuable pendant l’exécution du processus et se configure ainsi :

```text
Features__Disabled__0=projects.reads
Features__Defaults__projects.writes=false
```

`Features:Disabled` est une liste de clés de fonctionnalité désactivées de manière contraignante pour le déploiement. `Features:Defaults` associe des clés de fonctionnalité à des valeurs booléennes par défaut. Ces paramètres sont validés au démarrage. Ils ne s’appliquent qu’aux fonctionnalités déclarées par des modules installés ; une valeur conservée ne peut ni installer un module retiré ni rendre disponible une fonctionnalité non déclarée. Leur modification exige la mise à jour normale de la configuration et le redémarrage du processus selon le déploiement habituel. Les contrôles de plateforme et d’organisation sont stockés séparément dans PostgreSQL.

L’environnement provient du `IHostEnvironment.EnvironmentName` du serveur ; les clients ne peuvent pas le choisir. Son nom est normalisé en minuscules selon les règles invariantes et doit contenir de 1 à 64 lettres ou chiffres ASCII, points, tirets bas ou tirets, en commençant par un caractère alphanumérique.

## Priorité et contrôles indépendants

Chaque module déclare une fonctionnalité avec une clé stable préfixée par le propriétaire, un nom, une description et une valeur booléenne explicite par défaut. La disponibilité effective suit cet ordre :

1. Le module propriétaire doit être installé et déclarer la fonctionnalité. Une désactivation contraignante du déploiement la rend indisponible.
2. Une désactivation forcée par la plateforme la rend indisponible.
3. Une surcharge d’organisation non nulle prend le dessus.
4. Sinon, le contrôle d’environnement non nul s’applique.
5. Sinon, la valeur configurée par défaut s’applique.
6. Sinon, la valeur déclarée par défaut s’applique.

Une désactivation forcée ou de déploiement prévaut sur toute valeur activée de priorité inférieure. `projects.reads` et `projects.writes` sont des contrôles de disponibilité indépendants ; ils ne représentent pas les permissions `projects.read` ou `projects.manage`. L’acteur doit toujours disposer de la permission requise, et les points de terminaison d’organisation exigent toujours une appartenance et un contexte d’organisation valides.

## Contrôles de plateforme

Ouvrez **Administration de la plateforme → Contrôles des fonctionnalités**. Le serveur affiche l’environnement courant. Une permission de lecture permet de lister les déclarations, consulter les contrôles et lire l’historique immuable paginé. Une permission distincte de gestion est nécessaire pour enregistrer les contrôles. Les permissions des fonctionnalités n’accordent ni accès à l’annuaire des organisations ni appartenance à une organisation. Utilisez le sélecteur paginé uniquement si la session dispose également de `platform.tenants.read` ; un lien depuis une page d’administration d’organisation peut ouvrir une cible précise sans accorder l’accès à l’annuaire.

L’API de plateforme expose ces opérations :

| Opération | Permission | Utilité |
| --- | --- | --- |
| `GET /api/v1/platform/features` | `platform.features.read` | Lister les fonctionnalités déclarées et les contrôles d’environnement/organisation actuels |
| `GET /api/v1/platform/features/{featureKey}/history` | `platform.features.read` | Lire une page bornée de l’historique immuable |
| `PUT /api/v1/platform/features/{featureKey}/environment` | `platform.features.manage` | Enregistrer une valeur par défaut d’environnement et l’état de désactivation forcée |
| `PUT /api/v1/platform/features/{featureKey}/organizations/{organizationId}` | `platform.features.manage` | Enregistrer une surcharge d’organisation |

Les opérations PUT conservent les protections habituelles par cookie et jeton antifalsification. L’instantané de l’organisation courante est `GET /api/v1/features` ; l’appelant ne peut pas y choisir l’organisation ni l’environnement.

Les valeurs par défaut d’environnement et les surcharges d’organisation ont chacune trois états : activé, désactivé ou hérité. L’héritage est représenté par `null`. Enregistrer l’héritage crée ou met à jour une ligne de contrôle versionnée ; cela ne supprime pas la ligne. La désactivation forcée de l’environnement est un veto distinct qu’il faut désactiver explicitement.

Chaque enregistrement inclut la version affichée à l’opérateur. Utilisez `Guid.Empty` uniquement lorsqu’aucune ligne de contrôle n’existe pour la cible. Un enregistrement réussi avance la version et ajoute un historique immuable avant/après dans la même transaction de base de données. Ne supprimez pas les lignes et ne modifiez pas l’historique pour réinitialiser un contrôle.

Si l’enregistrement renvoie `409 feature_control_conflict`, un autre opérateur a modifié le contrôle depuis son chargement. S’il renvoie `409 feature_control_busy`, une autre transaction détient temporairement le verrou de cette cible. Dans les deux cas, rechargez les contrôles, examinez les valeurs courantes, puis soumettez de nouveau volontairement si nécessaire. L’interface conserve le brouillon et ne réessaie pas l’enregistrement ni ne remplace silencieusement sa version attendue. N’effectuez pas de nouvelles tentatives automatiques à l’aveugle.

## Comportement à l’exécution et reprise

Le serveur évalue la règle de fonctionnalité dans PostgreSQL pour l’organisation courante et n’utilise pas de décision activée mise en cache. Les points de terminaison HTTP et leurs cas d’utilisation applicatifs imposent tous deux la disponibilité. La même vérification s’applique aux appels applicatifs directs, aux lectures de l’assistant et à l’exécution en file d’attente. Un accusé d’idempotence déjà enregistré reste soumis à la disponibilité d’écriture actuelle avant que sa réponse soit renvoyée. Les contrôles de lecture et d’écriture restent indépendants.

| Résultat | Signification | Action opérateur ou client |
| --- | --- | --- |
| `409 feature_unavailable` | Cette fonctionnalité déclarée est désactivée pour l’environnement ou l’organisation courante. | Vérifiez les contrôles d’environnement et d’organisation ainsi que leur priorité ; ne rétablissez la valeur nécessaire que si vous y êtes autorisé. |
| `403 forbidden` | L’acteur ne dispose pas de la permission requise ou de l’autorité d’organisation. | Examinez les rôles et appartenances existants. L’activation d’une fonctionnalité ne les modifie pas. |
| `503 feature_policy_unavailable` | La règle courante n’a pas pu être lue dans son stockage faisant autorité. | Rétablissez la connexion à la base de données puis réessayez lorsque le service est sain. N’activez pas de valeur de secours obsolète. |

Le SDK Web masque les routes et contrôles protégés tant qu’un instantané réussi pour l’organisation courante ne déclare pas la clé activée. Il échoue de manière fermée pendant le chargement, après une erreur, pour une clé inconnue ou si l’organisation de l’instantané ne correspond pas à l’espace de travail sélectionné. L’instantané est actualisé au retour du focus de la fenêtre et toutes les 30 secondes. Le serveur reste la référence : une requête affichée avant la désactivation peut être rejetée lors de sa soumission.

La désactivation d’une fonctionnalité ou d’un module ne supprime ni les données métier validées, ni les lignes de contrôle, ni l’historique des fonctionnalités. Un module absent conserve son comportement habituel de routes et contributions d’interface manquantes ; un contrôle de fonctionnalité ne peut pas réinstaller le module. Réactivez le module avec le cycle de vie normal, puis vérifiez ses contrôles conservés avant d’activer ses fonctionnalités. Consultez le [guide du cycle de vie des modules](../modules.md#lifecycle-commands).

Pour la décision d’architecture et les limites d’accès aux données, consultez [ADR 0021](../adr/0021-installed-feature-availability.md).
