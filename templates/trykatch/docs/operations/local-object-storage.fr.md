# Stockage d'objets local et en mode publication

La capacité Documents facultative stocke les octets des fichiers via l'adaptateur `IObjectStorage` compatible avec S3 de l'hôte. Le code du module ne dépend pas d'un fournisseur de stockage particulier. Le déploiement Compose maintenu continue d'utiliser un stockage compatible avec S3 géré à l'extérieur ; le mode d'exécution AppHost fournit un service RustFS local pour le développement.

## Mode d'exécution AppHost

Lorsque `--storage` est activé, `dotnet run --project src/API/<App>.AppHost` démarre l'API et un conteneur RustFS 1.0.3 local. L'image est épinglée sur `rustfs/rustfs:1.0.3@sha256:9ca9559533b537d10482fe923288b676ad08f92a7069d9a17327fbae3cab688b`. RustFS expose les points de terminaison S3 et console via AppHost ; l'API reçoit le point de terminaison S3 et utilise le bucket de développement `trykatch-documents`, avec création automatique du bucket activée.

AppHost conserve les noms de paramètres secrets locaux existants `minio-access-key` et `minio-secret-key` afin que les valeurs Aspire secrètes déjà présentes restent utilisables ; il transmet ces valeurs à RustFS dans `RUSTFS_ACCESS_KEY` et `RUSTFS_SECRET_KEY`. Gardez ces secrets locaux de développement confidentiels et utilisez des identifiants uniques pour toute instance RustFS accessible au-delà de la machine de développement. RustFS conserve les données dans le nouveau volume `trykatch-app-slug-object-storage-data`. L'ancien volume `trykatch-app-slug-minio-data` n'est ni monté, renommé, copié ni supprimé par cette modification.

L'image épinglée et la configuration locale sont des valeurs par défaut de développement, et ne constituent pas une déclaration de préparation à la production ou de haute disponibilité. Consultez la [documentation RustFS sur les conteneurs](https://docs.rustfs.com/en/installation/container/docker) et la [version RustFS 1.0.3](https://github.com/rustfs/rustfs/releases/tag/1.0.3).

## Mode de publication AppHost

Le mode de publication AppHost ne démarre pas le conteneur RustFS local. Une publication avec le stockage activé exige les paramètres externes suivants :

| Paramètre | Exigence |
| --- | --- |
| `storage-service-url` | Point de terminaison du service compatible avec S3 requis |
| `storage-access-key` | Secret requis ; utilisez une clé limitée au bucket |
| `storage-secret-key` | Secret requis ; utilisez un secret limité au bucket |
| `storage-bucket` | Bucket existant requis |
| `storage-region` | Valeur par défaut : `us-east-1` |

La création automatique du bucket est désactivée pour l'API publiée. Fournissez ces valeurs via le mécanisme de configuration et de gestion des secrets du déploiement ; ne validez pas et ne publiez pas les identifiants. Ce modèle ne configure aucun éditeur de production AppHost et ne prétend pas que la publication AppHost équivaut au déploiement Compose maintenu. Le point de terminaison externe compatible S3, la politique du bucket, TLS, les sauvegardes, la disponibilité et le cycle de vie des identifiants restent sous la responsabilité du déploiement.

## Compose et applications générées

Voir le [stockage conditionnel des objets](conditional-object-storage.fr.md) pour l’espace d’upload géré, les marqueurs d’annulation permanents, les empreintes de destination capturées et les exigences de qualification du système de fichiers local.

La configuration Compose de production maintenue continue d'utiliser un fournisseur externe. Définissez `TRYKATCH_STORAGE_SERVICE_URL`, `TRYKATCH_STORAGE_ACCESS_KEY`, `TRYKATCH_STORAGE_SECRET_KEY`, `TRYKATCH_STORAGE_BUCKET` et `TRYKATCH_STORAGE_REGION` comme décrit dans `.env.example`. Compose ne démarre pas RustFS et ne crée pas le bucket configuré. Protégez `.env` et fournissez des identifiants limités au bucket via le processus de gestion des secrets du déploiement. L'application continue d'utiliser l'adaptateur S3 du SDK AWS existant et le contrat `IObjectStorage` ; aucune API applicative propre à un fournisseur ni aucun format de clé d'objet n'est introduit.

Les nouveaux produits générés utilisent par défaut le service de développement RustFS d'AppHost lorsque le stockage est activé. Les produits générés existants ne sont pas mis à jour automatiquement lorsque le modèle change. Le responsable d'une mise à niveau d'une application générée existante doit planifier séparément tout déplacement de données : copier et vérifier les objets stockés dans le service cible, puis vérifier et rediriger `Storage:ServiceUrl` de cette application avant de modifier les enregistrements de documents existants ou d'acheminer les requêtes vers le nouveau stockage. Conservez l'ancien volume MinIO jusqu'à la vérification des données copiées et du comportement de l'application, puis obtenez l'approbation explicite de son propriétaire avant toute suppression. Cette mise à jour du modèle n'effectue aucune migration de données en direct, suppression de volume ni publication d'identifiants.
