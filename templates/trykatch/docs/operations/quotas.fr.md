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

Le démarrage valide les déclarations versionnées et leurs adaptateurs sans lire l’inventaire. Documents 1.3.0 inclut les fichiers actifs, archivés et en attente de suppression ; les anciennes lignes sans fichier sont exclues et les tailles inconnues restent indisponibles. Le réconciliateur traite au plus 500 entrées par transaction indépendante, sous le verrou commun aux écritures, compare source/unités avant déduplication et valide atomiquement les effets et le curseur versionné. Un inventaire incomplet ou ancien ne permet pas une admission finie.

Les uploads Documents utilisent désormais une admission durable indépendante avant l’écriture conditionnelle. Métadonnées, audit, outbox, consommation positive et clôture partagent la transaction métier existante. Après un rollback confirmé, l’hôte installe une barrière durable au lieu de supprimer directement l’objet. Une identité de stockage inconnue conserve la réservation. Un retrait logique exige la politique enregistrée du propriétaire et la charge positive originale ; Documents n’ajoute aucune politique de purge/remboursement. Archivage, restauration et demande de suppression conservent la consommation.

## Réconciliation bornée

Le worker de quota de l’hôte fonctionne indépendamment du contrôle des plafonds et des tâches planifiées de modules. Conserver la connexion du coordinateur existant, même après désactivation/retrait d’un module ayant des uploads en attente. Aucun nouveau profil n’est ajouté. Le profil organisation insère les références fermées ; le coordinateur lit/modifie uniquement leur planification, sans lecture des données métier ni suppression du travail. Une réservation purement transactionnelle et sa référence d’expiration sont insérées dans la même transaction parente. Les uploads durables et inventaires incomplets ajoutent déjà leurs propres références.

Chaque lot réclame au plus 20 éléments selon l’heure PostgreSQL, avec exclusion de lignes et bail de deux minutes. Chaque exécution dispose de 60 secondes, d’opérations SQL bornées et de transactions d’organisation fraîches sous un principal fixe sans appartenance ni permission métier. L’expiration vérifie le type immuable et la référence de réservation sous verrou. La récupération d’upload vérifie intent/réservation, valide Fencing, ferme cette transaction avant l’I/O, confirme indépendamment une sentinelle permanente, puis libère la réservation dans une nouvelle transaction. Aucun objet engagé ni version physique du fournisseur n’est purgé. Toutes les instances doivent conserver l’espace de stockage admis ; un changement d’espace laisse le travail indisponible et la réservation comptabilisée.

Seul le bail courant non expiré peut acquitter le travail. Un worker perdu peut être repris après expiration ; un ancien acquittement ne modifie pas le nouveau bail. Les reprises utilisent l’heure PostgreSQL et un délai de 5 secondes à 5 minutes, sans abandon définitif après un nombre d’essais. Une page incomplète reprend après 5 secondes. Une organisation inactive/absente, un placement non pris en charge ou une source d’inventaire absente suspend le travail, réévalué après 60 secondes. Réactiver la source reprend son curseur conservé. L’hôte peut clôturer un upload admis alors que son module est absent et l’appartenance originale révoquée, sans charger de traitement métier du module. Une ancienne politique d’upload non identifiée reste indisponible.

La sonde de disponibilité `quota-maintenance` expose uniquement les nombres en attente/en reprise/suspendus et l’âge de la plus ancienne échéance. Elle est unhealthy avant la première observation réussie ou si la coordination est indisponible ; les reprises, suspensions et retards supérieurs à 5 minutes produisent un état degraded. Un travail futur ne vieillit pas depuis sa création. Les journaux contiennent le type d’exception et le type fermé de travail, sans contenu, clé ni provenance d’acteur.

Les preuves locales figurent dans [les preuves de maintenance](../specs/evidence/parity-quota-maintenance.md). Les pertes/redémarrages réels de processus et les réponses de stockage incertaines sont qualifiés avec PostgreSQL et le stockage local. Les parcours RustFS à plusieurs instances, la limitation des requêtes, l’interface de consommation, les produits générés et la revue/CI S5 restent à vérifier. Ces tests de composants ne prouvent pas les garanties du produit composé.
