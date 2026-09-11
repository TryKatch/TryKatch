---
title: Isolation des données des modules
description: Comprenez le contrat obligatoire de propriété, de placement, de RLS PostgreSQL et de confiance des packages pour les modules persistants.
---

Trykatch traite la propriété des données comme une métadonnée exécutable du module. Tout module qui conserve des données doit déclarer chaque relation qu’il possède comme `organization`, `platform`, `global` ou `infrastructure`. Une déclaration invalide ou incomplète bloque la livraison.

## Données appartenant à une organisation

Une entité appartenant à une organisation doit :

- implémenter le contrat de propriété d’organisation ;
- contenir un `OrganizationId` obligatoire ;
- utiliser un index commençant par l’organisation ;
- recevoir le filtre de requête EF Core fourni par l’hôte ;
- déclarer sa relation PostgreSQL et sa politique d’isolation ;
- activer et forcer la RLS avec des prédicats `USING` et `WITH CHECK` correspondants.

Le filtre EF Core protège les requêtes applicatives ordinaires contre les oublis accidentels. La RLS PostgreSQL constitue une frontière indépendante au niveau de la base et s’applique aussi au SQL brut.

## Validation et critères de livraison

Au démarrage, Trykatch valide le modèle EF Core composé. Après les migrations, le migrateur inspecte le catalogue PostgreSQL réel et refuse les tables non déclarées, les politiques manquantes, les autorisations dangereuses, les fonctions inattendues, la propriété d’objets par un rôle d’exécution ou tout rôle capable de contourner la RLS.

La qualification de l’application générée utilise PostgreSQL avec Testcontainers pour découvrir les tables déclarées et prouver que :

- l’absence de contexte d’organisation interdit l’accès ;
- une organisation ne peut ni lire ni modifier les lignes d’une autre ;
- les relations inter-organisation sont refusées ;
- le filtrage EF Core et la RLS PostgreSQL restent cohérents.

## Rôles d’exécution séparés

La production utilise des identités PostgreSQL distinctes pour les requêtes d’organisation, l’administration de plateforme, le stockage des identités et le traitement de l’outbox. Elles ne peuvent pas être réutilisées de manière interchangeable, posséder les objets du schéma, hériter de rôles privilégiés ni recevoir `BYPASSRLS`. Une identité de migration séparée gère les changements de schéma.

Les modules reçoivent une interface étroite d’accès aux données d’organisation plutôt qu’un `DbContext` de l’hôte. Les accès à la plateforme, aux identités, au placement et au propriétaire des migrations restent ainsi hors de la frontière du module.

## Placement partagé ou dédié

PostgreSQL partagé est le choix par défaut. Il associe les identifiants d’organisation, les filtres EF Core, la RLS forcée, des rôles d’exécution restreints et des tests inter-organisation automatisés.

L’API publique de création d’un tenant accepte actuellement uniquement `shared` ; l’absence de valeur sélectionne également PostgreSQL partagé. Toute autre valeur renvoie une réponse HTTP `422` avec le code d’erreur stable `unsupported_tenant_placement`, avant la création d’une organisation ou d’une invitation.

`Dedicated` reste un point d’extension interne qui échoue de manière sûre. Les enregistrements dédiés existants renvoient HTTP `503` avec `unsupported_tenant_placement` ; ils ne sont jamais redirigés vers la base partagée comme solution de repli. Un futur fournisseur de placement dédié devra créer la base, ne conserver qu’une référence au secret, exécuter les migrations et l’inspection d’isolation, puis confirmer l’état prêt avant que l’API et l’interface puissent proposer cette option.

## Confiance des packages

Les modules distribués sous forme de packages s’exécutent comme du code applicatif de confiance ; ils ne constituent pas des bacs à sable. L’installation et la mise à niveau vérifient donc un éditeur autorisé, le signataire NuGet, les empreintes des artefacts, une provenance SLSA signée, un SBOM SPDX, l’identité du système de construction et un résultat d’analyse de vulnérabilités signé avant toute modification de l’espace de travail. En cas d’échec, le catalogue, les registres, les références de projet et les fichiers de verrouillage précédents sont restaurés.

Les modules locaux restent examinés sous forme de code source. Le module Documents inclus constitue la preuve full-stack distribuée séparément : il fournit le backend, l’interface React, les permissions, les migrations, la RLS forcée, les événements d’audit et d’outbox, l’archivage et la restauration, ainsi que les déclarations d’outils d’assistance uniquement au moyen de contrats stables.

:::caution
Un éditeur de modules en production doit encore fournir des clés et certificats contrôlés par l’organisation, la provenance, le SBOM et l’attestation de vulnérabilités. Trykatch valide ces preuves mais ne crée jamais de secrets de confiance fictifs.
:::
