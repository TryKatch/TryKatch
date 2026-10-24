# Invalidations temps réel autorisées

[English](realtime.md)

Le temps réel transmet de petites références appartenant aux modules. L’historique PostgreSQL et les lectures HTTP courantes restent la source de vérité. Une notification réveille la réconciliation HTTP ; elle ne transporte aucun document métier et ne fait pas avancer le curseur du navigateur. Le mode par défaut est `Realtime:Mode=Disabled`. La publication après validation d’un événement et l’historique HTTP fonctionnent sans Valkey.

## Mise à niveau et démarrage

Exécutez le migrateur avant la nouvelle API. La migration applicative `20261024064323_AddOrganizationRealtimeHistory` ajoute l’historique et les compteurs isolés par organisation ; la migration Identity `20261024071616_AddDurableAccountSessions` ajoute les sessions durables. Les rôles d’exécution ne peuvent ni modifier ni supprimer l’historique. Les compteurs utilisent le profil organisation ; seul le profil Identity accède aux sessions. Ne déployez pas les nouveaux contrôles de cookie sur une base non migrée.

Les anciens cookies sans ligne de session durable exigent une nouvelle connexion. Le registre conserve un hash de session, l’empreinte de sécurité, l’expiration, la révocation et la génération de l’espace de travail. Un renouvellement normal prolonge uniquement une ligne existante encore valide ; il ne recrée jamais une session révoquée. Une connexion WebSocket conserve l’expiration du ticket capturée à son ouverture, même si des requêtes HTTP renouvellent ensuite le cookie. L’administration des sessions et la politique de conservation restent des travaux ultérieurs.

Projects installe un consommateur `projects.created` et une délégation système finie. L’ajout à l’historique et l’accusé de traitement inbox sont validés dans la même transaction du worker existant. Ce consommateur exige donc une connexion explicite `ConnectionStrings:trykatch-work`, même avec les tâches générales et les notifications désactivées. AppHost et les variantes Compose standard/backend fournissent déjà le rôle coordinateur dédié. Une configuration absente ou vide bloque le démarrage ; aucun identifiant propriétaire ou organisation n’est utilisé comme secours. La génération OpenAPI valide les inscriptions sans connexion au coordinateur ou au backplane.

Le checkpoint évolutif `20261024094642_RespectProjectsSqlMigrationOwnership` enregistre cette propriété SQL sans opération DDL. Le DDL de `app.projects` appartient au registre immuable de migrations SQL du module Projects. Le mapping EF reste disponible pour les requêtes, mais marque cette relation comme exclue de la génération DDL dans l’instantané canonique des migrations EF ; cela n’assouplit pas le contrôle strict de dérive du schéma réel par l’hôte. Ajoutez les changements futurs du schéma Projects comme nouvelles entrées SQL évolutives. Ne modifiez pas le baseline déjà appliqué ni aucune migration déjà appliquée.

## Modes et configuration

| Mode | Comportement |
| --- | --- |
| `Disabled` | Aucun hub, relais ou adaptateur Redis ; historique HTTP autorisé disponible. |
| `LocalOnly` | WebSockets authentifiés par cookie et relais des connexions locales, sans Redis. |
| `Distributed` | Relais local et adaptateur SignalR Redis standard, avec un seul endpoint authentifié et un espace de noms explicite. |

Configurez `Realtime:Mode` dans AppHost et, pour Distributed, un `Realtime:ApplicationNamespace` explicite. Le mode distribué crée une ressource Valkey distincte `realtime`, avec le paramètre secret Aspire `realtime-password`. Il ne réutilise ni le cache de lecture ni le stockage objet. En mode React local, AppHost fournit l’origine réelle du frontend, sauf si `Application:PublicUrl` est explicitement défini. Définissez cette URL publique pour les déploiements publiés.

Les deux variantes Compose acceptent l’overlay :

```sh
docker compose --env-file .env -f compose.yml -f compose.realtime.yml up -d --build
# Application backend seule :
docker compose --env-file .env -f compose.backend.yml -f compose.realtime.yml up -d --build
```

Protégez `TRYKATCH_REALTIME_PASSWORD` et définissez `TRYKATCH_REALTIME_NAMESPACE`, un nom non secret en minuscules avec tirets. Toutes les répliques d’un produit/environnement partagent cette valeur ; les produits indépendants utilisent des valeurs distinctes. Le Valkey privé épinglé n’expose aucun port hôte, n’a aucun volume persistant, limite sa mémoire à 64 Mio et utilise `noeviction`. Il n’est ni un historique ni un service de haute disponibilité en production.

Pour un backend externe : `Realtime__Mode=Distributed`, `Realtime__ApplicationNamespace=<produit>` et `ConnectionStrings__realtime=<endpoint>,password=<secret>`. Chargez le secret via le gestionnaire du déploiement. Un seul endpoint authentifié est accepté ; Sentinel n’est pas pris en charge. Utilisez TLS si la politique réseau l’exige. Les canaux combinent l’espace de noms et l’environnement hôte. N’y placez aucun identifiant client/utilisateur ou secret.

Chaque API ne relaie que ses connexions locales et revérifie chaque destinataire avant chaque envoi. Les envois SignalR standards vers une connexion locale contournent Redis. Le relais de référence peut donc continuer localement pendant une panne du backplane ; les messages SignalR acheminés vers une autre réplique peuvent échouer ou être perdus. L’historique validé reste intact. Un appel d’envoi terminé ne prouve pas la réception ni la disponibilité du cluster.

## Navigateur et proxy inverse

Le hub `/api/v1/realtime/hub` accepte uniquement WebSockets. La négociation utilise les cookies HttpOnly habituels et `X-CSRF-TOKEN`, comme les autres écritures du navigateur. Aucun jeton dans l’URL, groupe arbitraire ou identifiant d’organisation choisi par le client n’est accepté. Ne stockez pas de jetons dans le stockage local du navigateur.

Chaque proxy doit conserver le Host externe **avec son port** et l’Origin original, puis transmettre Upgrade/Connection via HTTP/1.1. Le proxy Vite `/api` et nginx fournis gèrent ces upgrades ; tous les intermédiaires supplémentaires doivent aussi le faire. Ne faites confiance qu’au proxy immédiat explicitement configuré. Le serveur exige une seule Origin HTTP(S) correspondant exactement à `Application:PublicUrl`, ou au schéma/Host de la requête après contrôle du proxy si cette URL est absente. Ne reflétez jamais l’Origin pour contourner un refus.

Le conteneur web sonde `127.0.0.1:8080/healthz`, correspondant à l’écoute IPv4 de nginx ; `localhost` peut sélectionner IPv6 `::1`.

Négociation et upgrade doivent atteindre le même processus API, même avec Redis ([documentation SignalR](https://learn.microsoft.com/en-us/aspnet/core/signalr/scale?view=aspnetcore-10.0#sticky-sessions)). nginx utilise `ip_hash` ; les autres répartiteurs exigent une affinité équivalente. Des adresses clientes partagées peuvent concentrer la charge ; un changement d’adresse/réplique peut imposer une reconnexion et une réparation HTTP.

Les modules déclarent leurs abonnements finis et leurs clés de requête via le SDK navigateur. L’hôte utilise le client fournisseur réel sans importer leur UI privée. Les produits backend seuls omettent React et ce client. Notifications, reconnexions et réparation périodique déclenchent un traitement HTTP sérialisé. Le démarrage ou la remise à zéro associe le curseur de tête à un rechargement complet des requêtes concernées ; une page en échec ne valide pas son curseur. Déconnexion, changement d’espace ou perte d’autorisation annulent l’ancien cycle et effacent son état organisation : aucun callback tardif ne doit le restaurer.

## Autorisation courante et récupération

Admission, remplacement d’abonnements, interrogation et chaque envoi revérifient session durable, génération, appartenance, permissions et ressource dans une transaction organisation en lecture seule. Une référence de ressource sur un abonnement organisation/utilisateur nécessite aussi sa visibilité courante. Projects suit la visibilité de son endpoint de lecture, y compris les métadonnées de cycle de vie encore lisibles. Utilisateur étranger, politique/ressource inconnue ou remplacement refusé ne donnent aucun accès ; le remplacement est atomique.

Révocation, nouvelle empreinte de sécurité, expiration du ticket capturé, organisation/appartenance inactive ou transition A→B→A empêchent l’ancienne connexion de redevenir valide. La dernière vérification précède l’envoi borné et attendu. Les octets déjà en vol ne sont pas rétractables ; validation en base et réception réseau ne sont pas sérialisées. Une annulation n’abandonne pas un callback de module et ne détruit pas sa portée de base tant qu’il s’exécute.

`GET /api/v1/realtime/state` renvoie l’activation du push, la génération et un curseur opaque de tête. `GET /api/v1/realtime/history` commence à zéro sans curseur, ou reprend avec `?cursor=...`. Une page examine au maximum 100 lignes et avance aussi au-delà des lignes filtrées. Le curseur lie organisation, acteur, hash de session et génération. Un curseur malformé, trop long, futur, étranger ou périmé donne HTTP 409 `realtime_reset_required` : abandonnez-le, relisez l’état et rechargez les requêtes métier. Ne construisez, journalisez ou transférez jamais ces curseurs entre sessions/espaces.

Un espace absent peut produire le HTTP 409 précis `Workspace context required` dans le middleware avant la lecture temps réel. Le navigateur traite ce résultat, ou HTTP 401/403, comme une perte du contexte organisation et réinitialise les résultats montés tout en conservant la session du compte. Un autre conflit ne révoque pas ce contexte. Un rechargement métier en échec remonte à la réconciliation : ni le démarrage ni une page d’historique ne valident leur curseur avant le succès de ces rechargements.

## Limites et diagnostics

| Paramètre sous `Realtime` | Défaut | Plage |
| --- | --- | --- |
| `MaximumConnections` | 256 | 1–256 par API |
| `MaximumSubscriptions` | 16 | 1–16 par connexion |
| `MaximumConcurrentSends` | 16 | 1–16 par API |
| `PollInterval` | 1 seconde | 100 ms–30 secondes |
| `SendTimeout` | 2 secondes | 100 ms–10 secondes |
| `AuthorityTimeout` | 5 secondes | 100 ms–30 secondes |

Les messages entrants sont limités à 4 Kio et les invalidations à 2 Kio. Un seul envoi possède une connexion à la fois, sans file de messages préautorisés. Un échec/délai d’envoi met la connexion en quarantaine sans dépasser la ligne non envoyée. HTTP permet la récupération. Une limite finie d’essais par API borne aussi le travail d’autorisation.

Le contrôle de disponibilité `realtime` n’interroge aucun backend en Disabled/LocalOnly. Distributed expose une dégradation optionnelle sans endpoint, mot de passe ni exception fournisseur. Le compteur `trykatch.realtime.outcomes` du meter `Trykatch.Realtime` exporte uniquement les dimensions fermées `mode` et `outcome`, jamais les identifiants, curseurs ou sélecteurs.

## Retrait d’un module

Le registre est immuable pendant l’exécution. Désactivez d’abord les modules dépendants avec la CLI, puis désactivez/retirez le module cible, régénérez les registres, déployez cette composition et arrêtez **toutes les anciennes API et tous les anciens workers** avant de déclarer le retrait effectif. Dans le graphe fourni, Documents dépend de Projects. Un nouvel hôte ne peut pas autoriser une politique/producteur absent. Conservez schéma, historique et travail durable suspendu conformément au cycle de vie existant. Aucun basculement global à chaud, nettoyage automatique d’historique, tâche de conservation des sessions ou nouvelle politique de rétention n’est ajouté.

Voir [ADR 0018](../adr/0018-scheduler-cache-and-realtime-adapters.md), les [contrats](../specs/feature-parity-contracts.md#7-cache-and-realtime--s1s2) et les [preuves](../specs/evidence/parity-platform-services.md). Les vérifications des dépendances, de l’application, des processus, des proxys, du navigateur et des produits générés sont distinctes.
