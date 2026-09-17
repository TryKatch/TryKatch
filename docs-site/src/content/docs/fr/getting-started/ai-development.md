---
title: Développement assisté par IA
description: Utiliser les cinq compétences fournies pour spécifier, créer, étendre, réviser et vérifier une fonctionnalité Trykatch.
---

Les applications générées incluent des instructions pour les agents de développement et cinq compétences dans `.agents/skills/`. Le fichier racine `AGENTS.md` oriente chaque tâche vers la compétence et les guides pertinents : backend, React, sécurité, spécifications et vérification. Ces compétences utilisent la CLI de modules et les blueprints existants, puis guident le développement spécifique et les contrôles.

## Parcours développeur après génération

Le template affiche un message **Start here** vers `docs/developer-onboarding.md` et sa version française `docs/developer-onboarding.fr.md`. Le README généré et `AGENTS.md` renvoient au même parcours. Utilisez votre assistant habituel ou suivez le guide manuellement, sans clé IA ni service de chat supplémentaire. Votre outil conserve sa propre configuration, ses coûts et ses règles de confidentialité.

Demandez d’abord la lecture d’`AGENTS.md`, du parcours et du code réel, puis une explication de l’architecture et des modules activés sans modifier de fichiers. Le guide fournit des questions copiables sur les modules, permissions/RLS, frontend/OpenAPI, extensions publiées, IntegrationEvents/outbox, API externes et vérification. Il distingue documentation, code inspecté, génération prise en charge et travail métier spécifique.

La variante React couvre le câblage frontend ; la variante backend seul ignore ces étapes. Le message utilise une post-action informative, même lorsque les scripts exécutables sont refusés. Il ne lance aucune IA, installation ou application. Les applications existantes ne sont pas modifiées par une mise à jour du template. L’aide IA intégrée reste une fonctionnalité produit distincte, inchangée par ce kit.

Pour un parcours concret, ouvrez `docs/first-feature.fr.md` dans l'application générée (anglais : `docs/first-feature.md`). Il couvre `doctor`, restauration verrouillée `setup`, disponibilité API avec l'URL réelle Aspire, création Equipment et lecture du résultat. Les contrôles CRUD, validation, permissions lecture/gestion, isolation entre organisations et langues distinguent une fonctionnalité opérationnelle d'une compilation réussie. Dans le dépôt du template, `scripts/test-first-feature.sh backend|web` vérifie des applications fraîchement générées et l'isolation réelle PostgreSQL ; il ne prétend pas vérifier le navigateur en fonctionnement ni la facilité d'apprentissage d'un nouveau développeur.

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

L’aide IA s’ouvre maintenant dans un panneau de chat depuis le menu du compte ou le bouton en bas à droite ; `/assistant` reste disponible en plein écran. Entrée envoie le message et Maj+Entrée ajoute une ligne. Les suivis utilisent un contexte chiffré lié à l’identité et aux permissions, limité à quatre échanges terminés et 20 minutes. Le chat reste en mémoire React, sans stockage persistant dans le navigateur. Une nouvelle conversation, un changement d’espace, un rafraîchissement ou une déconnexion l’efface.

Six guides approuvés intégrés à l’application permettent d’expliquer l’architecture, Projets/Documents, l’isolation PostgreSQL RLS, le développement des modules et les fournisseurs IA. La recherche lexicale est bornée et utilise les modules réellement activés ; les guides des modules désactivés sont exclus. **Guides consultés** ouvre des pages de sources authentifiées et lisibles. La documentation n’accorde aucun accès aux données métier et décrit la conception documentée, sans inspecter votre code personnalisé. Les extraits, le contexte récent et les métadonnées autorisées sont envoyés au fournisseur sélectionné. Les modifications de guides sont intégrées lors de la compilation, sans dépôt source ni fournisseur d’embeddings requis en production.

L’adaptateur supplémentaire `chat-completions` prend en charge les destinations compatibles configurées côté serveur, notamment DeepSeek pour l’UAT, sans modifier le runtime, les outils ni l’interface. Définissez `Assistant__Provider=chat-completions`, `Assistant__Endpoint=https://api.deepseek.com` et un `Assistant__Model` explicite acceptant les outils (exemple actuel : `deepseek-flash`). Injectez la clé UAT via le secret serveur `Assistant__ApiKey`. Utilisez `Assistant__ReasoningEffort=none` pour les tours UAT bornés de DeepSeek ; omettez ce réglage facultatif si la destination ne l’accepte pas. Les URL racines et bases `/v1` sont acceptées, pas les autres chemins. Compose transmet les réglages uniquement à l’API, avec désactivation par défaut. Le raisonnement privé est conservé entre appels d’outils, jamais affiché. Les tests déterministes ne constituent pas un succès UAT DeepSeek réel. Les protocoles natifs supplémentaires nécessitent toujours leurs adaptateurs. Voir la [référence DeepSeek](https://api-docs.deepseek.com/api/create-chat-completion/).

Le contrat généré décrit les opérations API explicitement autorisées, pas leur exécution. Le template inclut aussi un assistant facultatif en lecture seule, fondé sur l’interface indépendante du fournisseur `Microsoft.Extensions.AI.IChatClient`, avec une page React `/assistant`. Les adaptateurs intégrés prennent en charge OpenAI Responses et le protocole natif Ollama ; il ne s’agit pas de Microsoft Agent Framework. Activez-le explicitement avec `Assistant__Enabled=true`, `Assistant__Provider` et `Assistant__Model`, côté serveur uniquement. OpenAI exige `Assistant__ApiKey` ; Ollama exige une URL racine explicite `Assistant__Endpoint` (par exemple `http://localhost:11434`), sans clé cloud pour une inférence locale. Les destinations non locales exigent HTTPS. Aucun fournisseur n’est choisi par défaut et aucun repli automatique n’est effectué. Choisissez un modèle prenant en charge les appels d’outils. Les questions et métadonnées autorisées sont envoyées à la destination sélectionnée ; vérifiez ses politiques de données avant les usages sensibles. Ne placez jamais une clé dans la configuration frontend. Les autres fournisseurs nécessitent un adaptateur enregistré et testé séparément, sans modifier les outils métier ni l’interface.

Chaque outil nécessite un module activé, un adaptateur de lecture enregistré explicitement et l’autorisation de l’utilisateur. Les requêtes utilisent les transactions/RLS d’organisation, la protection antiforgery et une exécution bornée. Les documents exposent uniquement leurs métadonnées. Les écritures, un mécanisme de confirmation humaine, un serveur MCP et les agents autonomes sont exclus de v1. Les compétences de développement restent indépendantes de cet assistant.

`module facts [module-id]` affiche en JSON les déclarations installées et validées : propriété des données, permissions, extensions, métadonnées d’assistant et points d’entrée des sources/artefacts. Cette commande ne modifie aucun fichier et fonctionne aussi en mode backend seul. Les tests déterministes vérifient le comportement de sécurité, pas la qualité d’un modèle réel.
