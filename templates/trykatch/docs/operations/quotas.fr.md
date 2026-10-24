# Configuration des quotas d’organisation

La configuration de l’hôte est la première source des droits de consommation. Billing reste facultatif et n’intervient pas dans ce chemin. Aucun endpoint HTTP ne modifie les plafonds. Exécuter le migrateur avant les API et workers mis à jour : il crée la projection applicative et l’historique de publication, avec les profils existants et la RLS forcée par organisation.

Par défaut, `Quotas:EnforcementEnabled` vaut `false` et `Quotas:Revision` vaut `0`, sans règle de ressource. Pour activer les plafonds, utiliser une révision positive croissante et des règles explicites :

```json
{
  "Quotas": {
    "EnforcementEnabled": true,
    "Revision": 1,
    "Resources": {
      "documents.storage-bytes": { "Limit": 104857600 }
    }
  }
}
```

Cette valeur illustre la syntaxe ; elle ne définit aucune offre commerciale. `Limit` accepte un entier Int64 positif ou nul, y compris zéro. `{ "Unlimited": true }` supprime explicitement le plafond. Lorsque le contrôle est activé, une ressource sans règle reste non configurée et refuse les nouvelles allocations. Le mode désactivé conserve les identités d’admission et de consommation. Il ne remplace jamais une révision durable plus récente : désactiver un contrôle déjà publié exige aussi une nouvelle révision positive.

Respecter l’orthographe des propriétés et les clés exactes, sensibles à la casse, du catalogue installé. Les propriétés/clés inconnues, valeurs invalides, nombres négatifs ou hors Int64, règles finite/Unlimited combinées et activation à la révision zéro empêchent le démarrage. La révision zéro est réservée au mode désactivé sans règles explicites. La configuration est figée au démarrage ; la modifier exige un redémarrage avec une nouvelle révision. Le hash dépend du contenu et non de la révision ou du catalogue. Retirer une règle explicite obsolète dans une nouvelle configuration avant de retirer son module.

Avant le premier verrou de quota d’une transaction, l’hôte publie un lot complet et trié dans sa propre transaction du profil plateforme. Il vérifie le même hôte/port/base PostgreSQL direct et la même identité serveur que la transaction d’organisation. Les placements dédiés/routés ne sont pas pris en charge. L’acteur technique fixe sert à la provenance ; aucune appartenance n’est inventée. La publication reste durable même si la transaction métier est ensuite annulée.

Une nouvelle révision actualise atomiquement les ressources installées et conservées, sans changer leur propriétaire/unité. Son en-tête d’historique existe même sans ressource installée. À révision et hash identiques, seules les ressources nouvellement installées et absentes sont initialisées. Un contenu différent à la même révision échoue. Une ancienne instance utilise la projection durable plus récente sans ajouter de valeurs anciennes. Une provenance absente ou incohérente est indisponible, jamais implicitement désactivée ou Unlimited. Les nouvelles allocations relisent la projection sous verrou ; les réservations admises, leurs reprises et leur clôture restent valides après une réduction du plafond.

Un verrou occupé annule le lot entier pour une reprise ultérieure. Une panne de base produit un état indisponible, sans secours en cache ou en mémoire locale. Les délais sont bornés. Pour corriger un conflit, publier la configuration voulue à une révision supérieure ; ne pas supprimer l’historique, remettre les révisions à zéro ou modifier manuellement la projection.

Au jalon Task3, la publication de l’hôte et l’API de réservation transactionnelle sont implémentées. L’admission et l’inventaire des uploads Documents, leur réconciliation, la limitation des requêtes et l’interface de consommation restent des tâches S5 suivantes. Ces tests ne prouvent pas encore les garanties de stockage ou du produit composé.
