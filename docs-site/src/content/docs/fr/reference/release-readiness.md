---
title: Préparation à la livraison
description: Comprenez ce que Trykatch fournit et ce que chaque produit généré doit encore démontrer.
---

Trykatch fournit une architecture orientée production et des critères de qualification automatisés. Il ne certifie pas chaque application générée ni chaque environnement d’hébergement.

## Critères de livraison du modèle

- compilations propres et suites de tests sans avertissement du compilateur ;
- tests d’intégration de la RLS inter-organisation et de l’authentification ;
- contrôles de dérive entre l’OpenAPI généré et le client TypeScript ;
- matrices couvrant la configuration par défaut, le backend seul, les modules optionnels et les noms complexes ;
- construction des conteneurs, audit des dépendances, liste blanche de licences, génération du SBOM et recherche de secrets ;
- vérification de l’ingestion de l’observabilité et de l’état de préparation.

## Responsabilités du produit

Chaque produit généré doit encore valider son propre modèle de menaces, sa classification des données, sa capacité, ses sauvegardes et restaurations à un instant donné, ses exercices de restauration, sa rotation des clés, ses règles d’entrée réseau, sa réponse aux incidents et ses obligations réglementaires.

Le dépôt suit les critères encore incomplets avant une version stable dans le [plan de préparation à la production](https://github.com/TryKatch/TryKatch/blob/develop/docs/production-readiness-plan.md).
