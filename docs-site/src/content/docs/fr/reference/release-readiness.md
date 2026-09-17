---
title: Préparation à la livraison
description: Comprenez ce que Trykatch fournit et ce que chaque produit généré doit encore démontrer.
---

Trykatch fournit une architecture orientée production et des critères de qualification automatisés. Il ne certifie pas chaque application générée ni chaque environnement d’hébergement.

## Preuves de la version préliminaire publiée

La réconciliation du 2026-09-17 porte sur Trykatch **0.1.0-preview.25** : la CI du commit livré a réussi 282 tests unitaires du code source et 236 tests d’intégration PostgreSQL, sans test ignoré. Huit correctifs de durcissement du socle sont livrés avec des preuves de non-régression ; les critères complets d’une version stable restent ouverts. Le [registre des preuves](https://github.com/TryKatch/TryKatch/blob/develop/docs/enterprise-foundation-evidence.md) relie le commit immuable, les exécutions de tests, les correctifs et les qualifications restantes. Une ancienne case non cochée ne prouve pas un défaut actuel, et une CI réussie ne constitue pas une certification de production.

## Critères de livraison du modèle

La cible coordonnée **0.1.0-preview.27** conserve l’accueil des développeurs et inclut l’aide IA pour les utilisateurs : activation explicite, fournisseur interchangeable, bouton de discussion flottant, entrée du menu du compte, guides approuvés et conversations de suivi bornées. Les deux entrées et le moteur backend sont vérifiés dans les applications générées depuis les paquets, pas seulement dans le code source. La disponibilité exige toujours la CI du commit exact sur main, la qualification du tag et une publication réussie.

La version publiée **0.1.0-preview.26** ajoute les journaux sûrs des exceptions/annulations, les contrôles SDK/actions/première restauration et la publication de paquets compilés une seule fois puis vérifiés par empreinte. Sa [CI du commit livré](https://github.com/TryKatch/TryKatch/actions/runs/35246520501) a réussi les 18 jobs, 282 tests unitaires du code source et 247 tests d’intégration PostgreSQL, sans test ignoré ; [la qualification du tag et la publication](https://github.com/TryKatch/TryKatch/actions/runs/35247873752) ont également réussi. Ce socle ne contenait pas l’aide IA. Les critères complets d’une version stable restent ouverts.

- compilations propres et suites de tests sans avertissement du compilateur ;
- tests d’intégration de la RLS inter-organisation et de l’authentification ;
- contrôles de dérive entre l’OpenAPI généré et le client TypeScript ;
- matrices couvrant la configuration par défaut, le backend seul, les modules optionnels et les noms complexes ;
- construction des conteneurs, audit des dépendances, liste blanche de licences, génération du SBOM et recherche de secrets ;
- vérification de la configuration d’observabilité, des contrats de télémétrie et de la file persistante du collecteur à l’exécution.

La configuration React par défaut est qualifiée avec des conteneurs de production et un navigateur. Les matrices backend seul et modules optionnels couvrent aussi la génération, la compilation et certains tests à l’exécution ; elles ne démontrent pas l’acceptation complète de chaque combinaison. L’ingestion de bout en bout des journaux, traces et métriques, la livraison des alertes, les exercices de restauration et de charge, l’accessibilité manuelle, la qualification des systèmes/IDE et l’approbation indépendante finale restent nécessaires avant une version stable.

## Responsabilités du produit

Chaque produit généré doit encore valider son propre modèle de menaces, sa classification des données, sa capacité, ses sauvegardes et restaurations à un instant donné, ses exercices de restauration, sa rotation des clés, ses règles d’entrée réseau, sa réponse aux incidents et ses obligations réglementaires.

Le dépôt suit les critères encore incomplets avant une version stable dans le [plan de préparation à la production](https://github.com/TryKatch/TryKatch/blob/develop/docs/production-readiness-plan.md).
