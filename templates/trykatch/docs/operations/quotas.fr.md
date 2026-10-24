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

La migration de récupération ajoute la clé/version immuable de la politique d’upload et une file de maintenance à références fermées. Tout nouvel intent d’upload exige une politique explicite. Les anciens intents sans cette identité conservent leurs réservations : la migration n’invente aucune politique et ne libère aucune capacité. Ne pas rattacher manuellement ces lignes à une politique actuelle ; toute récupération exige une provenance durable vérifiée.

La file contient uniquement les références d’organisation/ressource/réservation/upload et un état de planification borné. Le profil organisation peut insérer les intents initiaux ; le coordinateur existant peut lire/modifier le travail, sans suppression ni accès aux données métier de quota. Un travail terminé ne peut pas être rouvert. Installer ce schéma ne lance aucun traitement de récupération.

Au jalon de persistance Task5, la publication, l’API de réservation transactionnelle et les adaptateurs de stockage conditionnel sont implémentés. Le démarrage vérifie désormais les déclarations versionnées et leurs adaptateurs de portée limitée, sans lire l’inventaire. Documents1.3.0 enregistre sa propre source : les pages bornées incluent les uploads actifs, archivés et en attente de suppression, conservent l’isolation d’organisation, excluent les anciennes lignes sans fichier et refusent les tailles non résolues. Les anciennes lignes gardent une identité d’upload NULL et utilisent leur identifiant de document stable pour l’inventaire. Le réconciliateur traite au plus500 entrées par transaction indépendante, sous le même verrou de ressource que les écritures de quota. Il compare la source et les unités des effets existants, ajoute les effets positifs en lot, vérifie les totaux Int64 et valide atomiquement le curseur et la version de source. Une ancienne version de source ne permet pas une admission finie. Une organisation absente/inactive ou un placement non pris en charge suspend le travail ; inventaire invalide, effets incohérents, dépassement numérique ou coordination indisponible ne font pas avancer la page. Le principal technique fixe n’a ni appartenance ni permission métier. L’exécuteur de la file et les services composites d’upload/retrait restent à terminer. Les uploads Documents existants n’utilisent pas encore l’admission durable de quota. La réconciliation, la limitation des requêtes et l’interface de consommation restent également requises. Ces tests de composants ne prouvent pas les garanties du produit composé.
