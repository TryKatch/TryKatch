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

Les modules qui enregistrent des données doivent également respecter le [contrat d’isolation des données des modules](/fr/architecture/module-data-isolation/) exécutable de Trykatch. Ce contrat valide les modèles EF Core, les politiques PostgreSQL, les rôles d’exécution et le comportement inter-organisation avant toute livraison.

## Rôles et invitations d’organisation

Les invitations d’organisation permettent de choisir un rôle actif avant l’envoi ; l’acceptation attribue ce rôle enregistré. Seuls les gestionnaires disposant de l’autorité actuelle suffisante peuvent inviter ou gérer les personnes et les rôles. Seul un véritable propriétaire peut déléguer le rôle protégé Owner. Les rôles Member et Viewer n’accordent pas de gestion par défaut. Les menus et la palette de commandes suivent les permissions effectives, pas le nom du rôle. Une URL directe ou un menu masqué ne contourne jamais l’autorisation serveur. Les écrans de rôles affichent des libellés lisibles plutôt que des identifiants techniques.

Lorsque le SMTP facultatif est configuré, l’e-mail d’invitation affiche aussi le nom lisible du rôle validé dans ses versions texte et HTML. Les noms personnalisés sont encodés pour le HTML ; le contenu de l’e-mail n’accorde aucun accès indépendamment de l’invitation enregistrée et de ses contrôles d’acceptation. Sans SMTP, l’administrateur peut copier le lien d’invitation.

## Vérifier les rôles de l’organisation

Ouvrez **Gestion des utilisateurs → Rôles** pour consulter les rôles système ou créer un rôle personnalisé. Les rôles système sont en lecture seule ; les rôles personnalisés hors de votre périmètre d’attribution ne peuvent pas être modifiés.

Dans l’éditeur, développez un module pour consulter ses permissions. La recherche ouvre les modules correspondants et **Sélection uniquement** permet de vérifier les droits avant l’enregistrement sans perdre les autres sélections. Chaque module affiche le nombre de permissions sélectionnées. Les droits sensibles restent signalés et les permissions que vous ne pouvez pas accorder sont désactivées. Les commandes de sélection par module s’appliquent aux permissions actuellement visibles. **Effacer la sélection** supprime explicitement tous les droits sélectionnés, y compris ceux masqués.

Ces contrôles facilitent le choix des droits ; le serveur reste responsable de l’application des autorisations.

## L’accès plateforme est séparé

Les administrateurs de plateforme et les opérateurs de support utilisent des rôles et permissions propres à la plateforme. Les rôles d’organisation n’accordent jamais de privilège plateforme, et les noms de permissions plateforme sont validés par leur propre catalogue défini dans le code.

:::note
La RLS constitue la dernière frontière d’isolation ; elle ne remplace pas l’autorisation applicative. Trykatch impose les deux.
:::
