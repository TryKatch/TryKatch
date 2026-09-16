---
title: Développement assisté par IA
description: Utiliser les cinq compétences fournies pour spécifier, créer, étendre, réviser et vérifier une fonctionnalité Trykatch.
---

Les applications générées incluent des instructions pour les agents de développement et cinq compétences dans `.agents/skills/`. Le fichier racine `AGENTS.md` oriente chaque tâche vers la compétence et les guides pertinents : backend, React, sécurité, spécifications et vérification. Ces compétences utilisent la CLI de modules et les blueprints existants, puis guident le développement spécifique et les contrôles.

## Partir d'une fonctionnalité

Ouvrez l'application générée dans votre agent et demandez :

```text
Lis .agents/skills/trykatch-build-module/SKILL.md et implémente un processus
de réception de livraisons à partir de blueprints/shipment-reception.json,
avec son interface React. Poursuis de la spécification à la vérification.
```

Certains agents découvrent automatiquement les compétences du projet. Sinon, indiquez directement le chemin du fichier. Aucune installation globale de compétences ni clé de fournisseur n'est nécessaire pour ces instructions ; votre agent conserve sa configuration habituelle.

## Choisir une compétence

| Compétence | Résultat |
| --- | --- |
| `trykatch-spec` | Spécification avec règles métier, permissions, transitions et critères d'acceptation vérifiables |
| `trykatch-build-module` | Nouveau module créé avec la CLI locale ou un blueprint, puis complété par le comportement spécifique |
| `trykatch-extend-module` | Modification d'un module existant ou contribution par un point d'extension publié |
| `trykatch-review` | Constats exploitables sur la conformité à la demande, l'architecture, les autorisations et l'isolation |
| `trykatch-verify` | Résultats des compilations, tests et contrôles navigateur pertinents, avec les échecs et contrôles indisponibles |

Une implémentation importante commence par une spécification dans `docs/specs/`. Une petite correction peut avancer directement. Si la demande autorise déjà l'implémentation et que les décisions métier sont claires, le travail se poursuit jusqu'à la vérification. Une demande de spécification ou de revue seule reste limitée à cet objectif.

## Outils et limites

La CLI incluse dans les sources correspond aux contrats de l'application. Elle génère les modules CRUD d'organisation et les workflows pris en charge, les enregistre, les compile et les teste. L'agent implémente ensuite les règles que le générateur ne couvre pas. Consultez le [guide de création de modules](/fr/modules/authoring/) pour les options et limites.

Les guides renvoient aux modules de référence et aux contrats partagés. Les tests et contrôles serveur soutiennent les règles d'architecture et de sécurité. Des instructions ne garantissent pas le comportement d'un modèle : le rapport distingue ce qui est implémenté de ce qui a réellement été testé.

Les applications React et backend seul reçoivent les mêmes compétences. En mode backend seul, les commandes frontend sont ignorées. Les noms `trykatch-*` restent stables ; les espaces de noms C# et chemins des projets suivent le nom de l'application générée.

Ces fichiers sont fournis aux nouvelles applications issues d'un template contenant cette fonctionnalité. Mettre à jour la CLI globale ou le template ne modifie pas les applications existantes. Pour une ancienne application, comparez les compétences avec ses contrats locaux avant de les copier et préservez ses instructions personnalisées.

## Assistants intégrés au produit

Le contrat d'assistant généré décrit les opérations API explicitement autorisées. Il ne fournit pas l'exécution d'un modèle, une interface de chat, un serveur MCP ou un mécanisme actif de confirmation humaine. Les compétences de développement n'ajoutent pas ces fonctions d'exécution.
