# Cache facultatif des lectures d'organisation

Trykatch peut utiliser un cache facultatif limité à l'organisation pour certaines DTO de lecture immuables. PostgreSQL reste la source faisant autorité : les autorisations et la génération d'invalidation validée sont vérifiées à chaque accès au cache. Un cache désactivé, un échec de cache, une portée de transaction non prise en charge ou une absence d'entrée entraîne une lecture de la source. La vérification de génération effectue une requête PostgreSQL indexée à chaque accès au cache ; le cache évite de reconstruire la DTO, pas tout accès à la base.

Le mode par défaut est `Disabled`. Définissez `Cache:Mode` sur `LocalOnly` pour un cache par processus, ou sur `Distributed` pour un cache partagé. Le mode du cache est une configuration d'exécution, indépendante des options de la CLI et des modules installés. Les écritures continuent d'enregistrer les générations d'invalidation durables lorsque les lectures sont désactivées. Ainsi, la séquence désactivation, écriture puis réactivation ne peut pas faire réapparaître une ancienne valeur. Ne réinitialisez ni ne purgez les lignes de génération tant que des entrées de cache peuvent subsister.

## Développement avec AppHost

Définissez `Cache:Mode` sur `Distributed` dans la configuration AppHost (par exemple dans son magasin user-secrets) pour ajouter le conteneur Valkey 9.1.2 épinglé. AppHost demande le paramètre secret Aspire `cache-password` et transmet à l'API une chaîne de connexion StackExchange.Redis authentifiée. Gardez ce mot de passe dans le magasin local de secrets. Laissez `Cache:Mode` absent ou définissez-le sur `Disabled` pour ne pas créer la ressource cache. Le point de terminaison AppHost est destiné au développement ; il ne garantit ni disponibilité de production ni chiffrement du transport.

## Compose

Le cache est une surcharge facultative compatible avec Compose régulier ou backend seul. Ajoutez `TRYKATCH_CACHE_PASSWORD` au fichier `.env` protégé avec une longue valeur aléatoire sûre pour les URL. Gardez-la hors du contrôle de version et des sorties du shell. La même valeur authentifie l'API et Valkey.

Pour l'application régulière, utilisez :

```sh
docker compose --env-file .env -f compose.yml -f compose.cache.yml up -d --build
```

Pour une application backend seule, utilisez :

```sh
docker compose --env-file .env -f compose.backend.yml -f compose.cache.yml up -d --build
```

Sans `compose.cache.yml`, l'API reste dans son mode désactivé par défaut et aucun service Valkey n'est démarré. Le service Valkey de la surcharge rejoint uniquement un réseau Docker interne, ne publie aucun port sur l'hôte, n'a aucun volume durable, exige un mot de passe, désactive la persistance et évince les entrées les moins récemment utilisées avec une limite mémoire de 64 Mio. L'API conserve son réseau habituel pour PostgreSQL et les autres services.

Pour un cache distribué géré à l'extérieur, définissez `Cache__Mode=Distributed` et `ConnectionStrings__cache` dans l'environnement d'exécution de l'API. La chaîne doit contenir exactement un point de terminaison et un mot de passe, selon la syntaxe StackExchange.Redis, par exemple `cache.internal:6379,password=<secret>`. Chargez les identifiants via le magasin de secrets du déploiement ou un environnement protégé, et gardez leurs valeurs littérales hors de la documentation, des journaux et des dossiers de support. Configurez le cache avec une authentification et un accès réseau privé. Ce modèle ne garantit pas le chiffrement en transit ; utilisez TLS pour les liens qui sortent d'un réseau privé de confiance ou lorsque la politique de déploiement l'exige. Le service Valkey local de la surcharge Compose n'est pas un service à haute disponibilité pour la production.

## Durée de vie et espace de noms

Les valeurs par défaut de l'hôte sont de 30 secondes pour `short` et 120 secondes pour `standard`. Dans Compose, définissez `TRYKATCH_CACHE_TTL_SHORT` et `TRYKATCH_CACHE_TTL_STANDARD` ; pour les autres déploiements, définissez `Cache__TtlSeconds__short` et `Cache__TtlSeconds__standard`. Les valeurs doivent être comprises entre 1 et 600 secondes. Gardez une courte durée de vie pour les données sensibles à la fraîcheur ; la vérification des générations PostgreSQL reste requise quelle que soit la durée.

L'espace de noms d'application par défaut est dérivé de l'assembly d'infrastructure partagé ; il reste donc stable entre les exécutables API et worker. Pour le remplacer dans Compose, ajoutez `Cache__ApplicationNamespace` à l'environnement de l'API dans une surcharge Compose propre au déploiement ; définissez la même valeur pour tout service worker séparé. Dans AppHost et les autres déploiements, définissez `Cache:ApplicationNamespace` ou `Cache__ApplicationNamespace`. Si plusieurs produits ou environnements partagent un cache distribué, utilisez la même valeur pour tous les processus d'un même produit et des valeurs distinctes pour chaque frontière produit/environnement. Utilisez un nom borné en minuscules avec tirets, jamais un secret ni une valeur client. Un changement d'espace de noms rend les anciennes entrées inaccessibles ; il ne réinitialise ni ne supprime les lignes de génération PostgreSQL durables.

Le cache est uniquement une optimisation. N'y placez aucune décision d'autorisation, d'adhésion, de permission, de jeton, de session, de quota ou toute autre décision faisant autorité. La source reste la solution de repli lors d'une panne du cache, et les échecs de la source restent des échecs de la source.

## Diagnostic

Le cache participe à l'état de disponibilité de l'API sous `/health/ready`, avec le contrôle `organization-cache`. En mode `Disabled` ou `LocalOnly`, ce contrôle est sain sans contacter de cache. En mode `Distributed`, il exécute une sonde fixe de lecture seule avec un délai maximal de deux secondes. Si le cache facultatif est indisponible, le contrôle devient dégradé ; les lectures admissibles continuent depuis la source faisant autorité. La réponse n'expose que l'état de santé, sans clé, point de terminaison, identifiant, erreur du fournisseur ni corps JSON spécifique au cache. La sonde ne crée pas d'entrée et ne modifie aucune génération d'invalidation.

Le meter `Trykatch.OrganizationCache` fournit les compteurs suivants :

| Instrument | Signification |
| --- | --- |
| `trykatch.cache.reads` | Les lectures `eligible` utilisent le cache après admission par la génération PostgreSQL ; les lectures `bypassed` vont directement à la source. |
| `trykatch.cache.sources` | Une fabrique source a démarré. Cela inclut les absences de cache et les contournements ; ce n'est pas un compteur de succès ou d'échec du cache. |
| `trykatch.cache.invalidations` | `staged` indique qu'une intention d'invalidation durable a été enregistrée dans la transaction en cours. Cela ne signifie pas que la transaction est validée. |
| `trykatch.cache.refusals` | Une valeur partagée a été refusée en raison d'un état ou d'une charge utile `dirty`, `oversized` ou `corrupt`. |

Les dimensions de métriques du cache se limitent à `mode` (`disabled`, `local` ou `distributed`) et `outcome`. La télémétrie ne contient aucune valeur d'organisation, d'acteur, de module, de modèle de lecture, de clé, de DTO, de point de terminaison ou d'identifiant. N'ajoutez pas ces valeurs aux tableaux de bord, journaux ou dossiers de support.

Si le contrôle Distributed est dégradé, vérifiez que l'API dispose d'un seul point de terminaison de cache privé et que son mot de passe provient du magasin de secrets du déploiement. Vérifiez l'accès réseau et la santé authentifiée du service sans afficher la chaîne de connexion ni les identifiants. L'application revient aux lectures source lorsque le cache facultatif est indisponible ; les erreurs PostgreSQL et de la source doivent toujours être traitées comme des erreurs de source.

Chaque lecture admissible vérifie d'abord la génération PostgreSQL validée ; prévoyez donc une recherche indexée dans la base par accès admissible. Une invalidation ne réinitialise pas les lignes de génération et ne contacte pas immédiatement chaque processus ; les pairs voient la nouvelle génération à leur prochaine lecture, tandis que les anciennes entrées physiques expirent selon leur durée de vie. Les durées configurées doivent être comprises entre 1 et 600 secondes. Le délai par défaut de la fabrique est de 5 secondes (plage admise de 1 à 30 secondes) ; l'annulation est coopérative et le travail source lié à une portée n'est pas abandonné lors de l'annulation de l'appelant. Les entrées dépassant 65 536 octets sérialisés ne sont pas stockées.
