# Stockage conditionnel des objets et clôture des uploads

L’hôte fournit le port `IConditionalObjectStorage` pour les uploads soumis aux quotas, en complément du port historique `IObjectStorage`. L'adaptateur conditionnel permet de retrouver un résultat d'écriture à partir de la clé et de l'identité de l'opération ; il n'accorde aucune autorité sur l'organisation ni sur l'annulation. Le flux d'admission et de clôture durable doit autoriser chaque appel.

## Clés gérées et stockage historique

Exemple de forme de clé gérée (le flux d’upload de l’hôte définit la structure réelle) :

```text
_module-uploads/v1/organizations/{organization-guid}/documents/{document-guid}
```

Les clés conditionnelles exigent le préfixe exact en minuscules `_module-uploads/v1/`, des segments ASCII et une longueur maximale de 500 caractères. Les segments vides, ceux qui commencent par `.`, `..`, les barres obliques inverses et les caractères non ASCII sont invalides. Une écriture avec une clé ou une identité invalide renvoie `InvalidPayload` ; `StatAsync` signale une clé invalide comme `Unowned`.

Tout l'arbre `_module-uploads/` est réservé, y compris les fichiers internes de staging et de verrouillage. Les appels bruts `PutAsync` et `DeleteAsync` refusent cet espace avant toute opération de fichier, y compris les alias avec barre oblique initiale, différence de casse ou résolution de chemin. `GetAsync` peut lire un objet géré via le port historique, mais ne renvoie que les octets du contenu ; les métadonnées de l'enveloppe ne sont pas exposées comme contenu de fichier. Les objets ordinaires hors de l'espace géré conservent leur format et leur comportement historiques, octet pour octet.

## Identité, révisions et résultats

Chaque objet provisoire conserve une identité immuable : l'identifiant de l'opération d'upload, la longueur positive originale, le SHA-256 du contenu et l'empreinte normalisée des métadonnées. Les valeurs hexadécimales des empreintes sont normalisées sans distinction de casse. `StatAsync` rapporte séparément la longueur actuelle réelle, car un marqueur d'annulation a une longueur actuelle nulle tout en conservant l'identité et la longueur originale du contenu.

L'adaptateur publie les objets initiaux avec une sémantique de création uniquement si absent. Une nouvelle tentative avec la même identité renvoie `AlreadyExists` ; un objet portant une autre identité ou un fichier non géré ou mal formé renvoie `Conflict` et reste inchangé. Une écriture confirmée renvoie `Written`. Une révision opaque identifie la représentation actuelle exacte ; ce n'est ni un condensat de contenu ni un jeton d'autorisation métier.

La clôture d'annulation crée un marqueur de longueur nulle à une clé absente, ou remplace un objet provisoire uniquement si son identité et sa révision actuelle exacte correspondent. Une nouvelle demande pour le marqueur correspondant renvoie `AlreadyExists`, même si la révision attendue fournie est périmée. Une révision périmée, une identité étrangère ou un objet non géré renvoie `Conflict`. Le marqueur conserve l'identité originale afin que la récupération distingue un marqueur d'annulation vide d'un upload initial vide.

Les états possibles de `StatAsync` sont `Missing`, `Provisional`, `Cancellation`, `Unowned` et `Unavailable`. Traitez `Unavailable` comme un résultat inconnu : ne supposez pas que l'écriture a échoué, ne réessayez pas sous une autre clé et ne libérez pas une réservation à partir de ce seul résultat. Après un délai d'attente ou une réponse perdue, appelez `StatAsync` indépendamment pour retrouver l'état visible. `Unowned` ne fournit aucune identité ni révision fiable.

## Comportement de l'adaptateur local

L'adaptateur local capture le chemin absolu canonique de `Storage:LocalPath` lors de sa construction. Son empreinte d'espace de noms est liée à cette racine ; un rechargement de configuration ne peut donc pas rediriger une opération admise vers un autre répertoire. Un autre endpoint, bucket ou chemin local produit une empreinte différente ; les identifiants restent exclus de cette empreinte.

Avant la publication, l'adaptateur exige un flux lisible, une longueur déclarée positive, une longueur originale correspondante, un type de contenu valide et une somme SHA-256 correcte. Il ne lit pas plus que la longueur déclarée plus un octet et vérifie le condensat dans un spool disque borné. Il écrit ensuite l'en-tête versionné d'identité et le contenu dans un fichier de staging unique sous la racine configurée, puis déplace atomiquement ce fichier fermé à sa destination. Les lectures gérées analysent l'enveloppe depuis un seul fichier ouvert et exposent un flux borné contenant uniquement le contenu. Les fichiers d'objets historiques hors de l'espace géré restent inchangés.

Les rédacteurs locaux se coordonnent au moyen de fichiers de verrouillage permanents sous `_module-uploads/v1/.locks/`, nommés à partir de la clé canonique. Le fichier de verrouillage n'est jamais supprimé : son retrait pourrait permettre à deux processus de verrouiller des inodes différents pour la même clé. Chaque acquisition vérifie qu'une seconde ouverture exclusive échoue avec un résultat de conflit reconnu ; tout verrouillage non pris en charge ou incertain échoue de manière fermée. L'acquisition du verrou est limitée à deux secondes et chaque opération conditionnelle de stockage à 30 secondes. L'annulation demandée par l'appelant est propagée. Ces délais indiquent une incertitude ; ils ne rendent pas sûre l'oubli d'une écriture ayant échoué.

L'adaptateur local exige un système de fichiers cohérent et partagé qui respecte le déplacement atomique et l'ouverture exclusive par tous les rédacteurs participants. Il ne déclare pas de liste de systèmes de fichiers réseau, overlay ou répliqués comme pris en charge. Un test entre processus exerce l'environnement sélectionné, mais ne qualifie pas chaque système de fichiers ou topologie de stockage de déploiement.

## Comportement de l'adaptateur S3 et limites opérationnelles

L'adaptateur S3 lie l'endpoint et le bucket configurés à son empreinte d'espace de noms. Il utilise la création conditionnelle et le remplacement conditionnel avec la révision opaque du fournisseur, puis lit l'identité et la longueur de l'objet dans ses métadonnées. S3 documente `If-None-Match` pour créer uniquement si absent et `If-Match` pour vérifier l'ETag actuel ; ici, l'ETag est un jeton conditionnel opaque, pas le SHA-256 de l'upload. Voir la [documentation AWS sur les écritures conditionnelles](https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes.html).

L'adaptateur ne nécessite ni vérification d'exécution de la gestion des versions ni permission de gestion des versions. Si le bucket utilise la gestion des versions, le remplacement par un marqueur peut laisser une version non courante du contenu. Les quotas comptent les octets logiques uploadés, pas les versions du fournisseur, la réplication, les métadonnées d'enveloppe ni les frais de staging. Aucun nettoyage du stockage physique n'est garanti. Ne configurez pas de règle de cycle de vie ou de suppression externe pour les marqueurs d'annulation gérés : ils sont permanents et leurs clés ne doivent jamais être supprimées ni réutilisées.

Sous Unix, le verrou repose sur le verrouillage exclusif de partage `FileStream` de .NET et vérifie qu'il fonctionne sur le fichier de verrouillage réel. Les sources exactes du runtime .NET 10.0.13 sont disponibles pour [Unix](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs) et [Windows](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Windows.cs). Le test de verrouillage et les tests entre processus apportent des preuves pour l'environnement exercé ; ils ne rendent pas tous les systèmes de fichiers adaptés à un stockage partagé.

Cet adaptateur est une primitive de stockage. Il n'admet pas les uploads, ne vérifie pas l'autorité de l'appelant dans l'organisation, ne décide pas si une annulation est sûre, ne libère pas une réservation et ne récupère pas une transaction de base de données. L'autorité durable `Fencing`, l'admission/finalisation composée des uploads et la récupération bornée restent des travaux S5 à terminer. Le plan d’implémentation est suivi dans le dépôt du modèle et ne constitue pas une dépendance livrée avec l’application. Cette page et les tests de l'adaptateur ne prouvent pas que le flux complet d'upload soumis aux quotas est achevé. Voir également la [configuration des quotas d'organisation](quotas.md) et la [configuration du stockage local des objets](local-object-storage.md).
