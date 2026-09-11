---
title: Reprise et rejeu de l’outbox
description: Exploiter une livraison bornée « au moins une fois » et un rejeu autorisé.
---

Le worker outbox utilise une nouvelle portée d’injection et une transaction pour chaque lot borné. Une panne PostgreSQL transitoire détruit la portée en échec puis réessaie avec un délai exponentiel borné et aléatoire. Les erreurs d’authentification, TLS/configuration, schéma, droits ou programmation arrêtent la distribution et rendent la readiness défaillante jusqu’à correction et redémarrage.

La livraison est **au moins une fois**. Un succès de publication suivi d’un rollback ou d’un commit incertain peut relivrer le même identifiant logique. Le consommateur doit dédupliquer atomiquement `MessageId` avec son effet métier. Aucun adaptateur ne doit annoncer une livraison exactement une fois.

À la limite d’échecs durables, le message terminal et son événement immuable sont validés ensemble. Une baisse de limite terminalise sans publier les lignes déjà au-delà. Les payloads dépassant un Mio sont détectés côté serveur et terminalisés sans matérialisation. La télémétrie reste bornée et ne contient jamais payload, erreur brute, SQL, paramètres, secrets ni nom d’acteur.

Operator et Auditor lisent les métadonnées terminales bornées. Administrator peut soumettre un rejeu avec un identifiant de requête stable et la génération échouée observée. `202` n’est retourné qu’après validation de la requête immuable. Le même identifiant et tuple est idempotent ; un tuple différent donne `409`. Le worker inscrit un seul résultat `replayed`, `not_found`, `not_exhausted` ou `stale_generation`, conserve identifiant/type/payload/date du message et incrémente sa génération.

Déploiement : mettre tous les workers au repos, vérifier une sauvegarde, appliquer la migration, provisionner et inspecter droits/RLS, puis déployer ensemble API et workers. Le mélange de versions est non pris en charge. Ne jamais supprimer l’historique pour rétrograder.

Le délai de transport libère le lot après enregistrement d’un échec sûr. Si l’adaptateur ignore l’annulation, la readiness reste défaillante et aucune nouvelle publication ne démarre dans ce processus avant sa fin ; l’arrêt n’attend pas indéfiniment. D’autres réplicas peuvent relivrer : la déduplication reste indispensable. Le rejeu relit le résultat immuable sous sérialisation transactionnelle par message. L’idempotence de l’identifiant de requête inclut l’acteur authentifié. Le suivi retourne un corps d’attente `202` documenté, puis le résultat final `200`.
