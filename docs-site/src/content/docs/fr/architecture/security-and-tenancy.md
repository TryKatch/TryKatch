---
title: Sécurité et isolation des organisations
description: Découvrez comment Trykatch résout les organisations et applique les autorisations ainsi que la RLS PostgreSQL.
---

Trykatch utilise **Organisation** comme terme visible par le client. Le terme tenant décrit uniquement le mécanisme technique d’isolation sous-jacent.

## Contexte déduit de la requête

Les URL neutres vis-à-vis de l’organisation évitent d’exposer la multi-tenance technique dans le chemin. Après l’authentification, le middleware détermine l’organisation active depuis le contexte de session protégé par le serveur ou depuis une autre intégration hôte de confiance. Il vérifie ensuite de nouveau l’adhésion avant toute opération cloisonnée.

## Défense en profondeur

Chaque requête liée à une organisation traverse quatre contrôles :

1. l’authentification identifie l’utilisateur global ;
2. la résolution établit l’organisation active ;
3. les gestionnaires de permissions autorisent la capacité demandée ;
4. la transaction de base de données définit `app.organization_id` et `app.actor_id`, puis la RLS forcée de PostgreSQL filtre les tables concernées.

Le rôle d’exécution de la base de données ne peut ni posséder les tables ni contourner la RLS. Un identifiant de migration distinct possède les changements de schéma.

## L’accès plateforme est séparé

Les administrateurs de plateforme et les opérateurs de support utilisent des rôles et permissions propres à la plateforme. Les rôles d’organisation n’accordent jamais de privilège plateforme, et les noms de permissions plateforme sont validés par leur propre catalogue défini dans le code.

:::note
La RLS constitue la dernière frontière d’isolation ; elle ne remplace pas l’autorisation applicative. Trykatch impose les deux.
:::
