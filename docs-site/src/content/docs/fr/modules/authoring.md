---
title: Créer un module
description: Ajoutez une capacité sans affaiblir le noyau de sécurité ni créer de couplage d’exécution caché.
---

Un module Trykatch est un ensemble fonctionnel full-stack doté d’un contrat explicite. La fonctionnalité de référence Projects illustre le parcours complet, tandis que Federation présente un adaptateur optionnel distribué séparément.

## Ce qu’un module peut fournir

- des services API et des contrôleurs ;
- la configuration du modèle EF Core et les migrations ;
- des permissions stables et des rôles par défaut sûrs ;
- des routes React, la navigation et des extensions d’interface nommées ;
- des événements d’outbox, des abonnés et des workers ;
- des outils d’assistance explicitement autorisés.

## Ce qui reste centralisé

L’identité, la résolution de l’organisation, la RLS, la protection antiforgery, l’application des permissions, l’intégrité de l’audit et la validation des modules ne sont pas des points d’extension.

## Parcours de développement

1. Déclarez des identifiants stables pour le module et ses contributions.
2. Enregistrez les contributions backend et React dans les catalogues de modules correspondants.
3. Implémentez le domaine, l’application, l’infrastructure, l’API et l’interface dans la frontière du module.
4. Ajoutez les permissions, valeurs par défaut d’organisation, migrations, politiques RLS, événements d’audit et comportement de l’outbox.
5. Exécutez le diagnostic des modules, les contrôles de dépendances, les tests de désactivation et la matrice du modèle généré.

```bash
trykatch module list --root ./Horizon
trykatch module doctor --root ./Horizon
```

:::caution
L’acquisition, la mise à niveau, l’extraction, le désenregistrement et la purge de packages tiers restent des critères de livraison. La frontière actuelle est volontairement validée à la compilation ; elle ne charge pas des plugins arbitraires à l’exécution.
:::
