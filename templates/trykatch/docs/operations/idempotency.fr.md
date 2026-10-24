# Idempotence des mutations d’organisation

[English](idempotency.md)

L’idempotence est une protection facultative, activée explicitement pour les mutations HTTP limitées à une organisation. Elle permet au client de réessayer une opération logique après un résultat réseau incertain sans exécuter deux fois le cas d’utilisation du module pendant la fenêtre de rejeu. Elle ne rend pas automatiquement tous les points de terminaison d’écriture idempotents et n’ajoute ni interface client, ni indicateur d’activation, ni API publique d’administration.

## Mise à niveau avant déploiement

Appliquez la migration applicative `20261024113623_AddMutationReceipts` avec le migrateur privilégié à exécution unique avant de déployer une API utilisant l’idempotence des mutations. La migration crée `app.mutation_receipts` avec RLS forcée et un accès limité à l’organisation et à l’acteur. Conservez les cinq rôles d’exécution existants à privilèges minimaux et leurs limites de propriété ; cette fonctionnalité n’ajoute aucun rôle d’exécution ni propriétaire d’exécution. Ne fournissez jamais la connexion du migrateur à l’API. Suivez le [processus de migration de base de données](../database.md).

La table des accusés contient des octets de réponse privés et doit être protégée comme les autres données d’organisation. La clé d’idempotence brute et le JSON original de la requête ne sont pas stockés séparément : la clé et l’identité canonique sont conservées sous forme d’empreintes SHA-256. Les réponses enregistrées peuvent néanmoins contenir des champs soumis par le client. La table n’a ni tâche automatique de purge ni politique de conservation distincte. L’expiration rend un accusé remplaçable lorsqu’une requête réutilise cette identité ; elle ne supprime pas la ligne à elle seule.

## Activation et compatibilité

Un point de terminaison doit s’inscrire explicitement via `WithMutationIdempotency` et fournir des contrôles d’autorisation de requête et de rejeu appartenant au module. L’hôte vérifie qu’un point de terminaison inscrit est une mutation d’organisation nommée, sécurisée et munie de métadonnées de réponse bornées et non diffusée en continu. Un point de terminaison non inscrit n’utilise pas cette fonctionnalité. Pour un point de terminaison inscrit, l’en-tête `Idempotency-Key` est facultatif par défaut : sans cet en-tête, la requête suit le chemin normal et aucun accusé n’est réservé. `ModuleMutationPolicy.KeyRequired` rend l’en-tête obligatoire pour ce point de terminaison.

Le module Projects inscrit actuellement ces cinq opérations :

| Identifiant d’opération | Route | Ressource liée après succès |
| --- | --- | --- |
| `Projects_Create` | `POST /api/v1/projects/` | Identifiant du projet créé par le serveur |
| `Projects_Update` | `PUT /api/v1/projects/{id}` | Identifiant du projet dans la route |
| `Projects_Archive` | `POST /api/v1/projects/{id}/archive` | Identifiant du projet dans la route |
| `Projects_Restore` | `POST /api/v1/projects/{id}/restore` | Identifiant du projet dans la route |
| `Projects_Delete` | `DELETE /api/v1/projects/{id}` | Identifiant du projet dans la route |

Projects enregistre la ressource avec `ModuleMutationIdempotency.BindResource` uniquement après le succès de son cas d’utilisation. Consultez [l’inscription des points de terminaison Projects](../../src/Modules/Projects/Trykatch.Modules.Projects.Presentation/ProjectsEndpoints.cs) et [le pipeline d’idempotence de l’hôte](../../src/Common/Trykatch.Modules.AspNetCore/ModuleIdempotency.cs). La création d’organisation conserve son mécanisme de récupération existant.

N’inscrivez pas les réponses de fichier ou de flux, les événements envoyés par le serveur, les téléchargements ni les opérations qui émettent des URL temporaires ou signées. Il s’agit d’une règle d’inscription à la charge du module ; l’hôte n’analyse pas le JSON arbitraire pour détecter la signification des URL. Seules les réponses finies qu’il est sûr de renvoyer sous forme d’octets stockés conviennent.

## Clés, identité de requête et limites

Fournissez exactement une clé ASCII de 1 à 128 caractères. Le premier caractère doit être une lettre ou un chiffre ASCII ; les caractères suivants peuvent aussi être `.`, `_`, `:` ou `-`. Les valeurs d’en-tête dupliquées, les valeurs vides, les caractères non ASCII et les autres signes de ponctuation sont rejetés. Choisissez une clé pour une intention utilisateur logique, conservez-la pour les nouvelles tentatives et utilisez-en une autre pour une nouvelle intention. Ne générez pas une nouvelle clé simplement parce qu’une réponse a été perdue : cela pourrait autoriser une seconde écriture.

La politique par défaut permet le rejeu d’un accusé réussi pendant 24 heures. Un point de terminaison peut choisir une durée de 1 minute à 24 heures. La fenêtre effective se termine à la première échéance entre l’expiration enregistrée et la date de réussite initiale augmentée de la durée de la politique courante. Après expiration, la même clé peut exécuter une nouvelle mutation ; si le résultat initial reste incertain, vérifiez-le par des lectures autorisées avant une nouvelle soumission. La taille de requête par défaut est de 65 536 octets UTF-8 (64 Kio) et la politique ne peut pas dépasser cette limite. Les corps non vides doivent être du JSON UTF-8 strict avec un type média `application/json` ou `application/*+json`. Les corps compressés ne sont pas acceptés.

La préparation renvoie **400** pour une clé obligatoire absente, une clé invalide ou un JSON mal formé, **413** pour un corps trop volumineux et **415** pour un type média ou un encodage non pris en charge. Ces rejets n’appellent pas la mutation.

L’empreinte de requête comprend la méthode HTTP en majuscules, le chemin échappé (y compris la base du chemin), la chaîne de requête brute, le type média JSON normalisé et l’identité canonique du corps. L’ordre et les espaces des propriétés d’un objet JSON ne modifient pas le corps canonique ; l’ordre des éléments d’un tableau reste significatif, les nombres sont normalisés et les noms de propriétés dupliqués dans un objet sont rejetés. L’absence de corps est distincte d’un corps JSON. L’algorithme JSON canonique partagé est défini dans [`ModuleCanonicalJson`](../../src/Common/Trykatch.Modules.Abstractions/Serialization/ModuleCanonicalJson.cs).

La limite par défaut d’un accusé de réponse est de 262 144 octets (256 Kio). Une politique peut la porter à 1 048 576 octets (1 Mio) au maximum ; la limite transactionnelle externe de réponse de l’hôte peut la réduire davantage. Seuls les résultats 2xx sont enregistrés. Pour une nouvelle exécution réservée, une réponse non-2xx annule la transaction de mutation et la réservation ; la requête corrigée peut donc réutiliser la même clé. Une réponse trop grande, non sérialisable, annulée ou dont la validation de transaction échoue ne crée pas d’accusé terminé.

## Autorisation et résultats

L’authentification normale, la résolution de l’espace de travail et de l’organisation, l’autorisation du point de terminaison, la validation antifalsification et la transaction d’organisation restent actives. Pour une requête avec clé, le magasin résout l’organisation, l’acteur, l’adhésion et les permissions actuels avant de renvoyer une réservation, un accusé, un conflit ou un résultat occupé. La tentative de verrou consultatif a lieu en premier, mais son résultat reste privé jusqu’à ces contrôles. Le contrôle de requête du module valide la cible ; un accusé terminé repasse aussi par le contrôle de rejeu du module, avec l’autorité et la ressource actuelles. Le rejeu renvoie l’accusé sans rappeler le cas d’utilisation du point de terminaison. Projects exige toujours l’autorité `projects.manage` et vérifie que le projet appartient à l’organisation courante, y compris lors d’un rejeu.

Les intergiciels d’authentification, de session, d’espace de travail, d’autorisation ou d’antifalsification peuvent rejeter une requête avant que le filtre d’idempotence ne renvoie son propre résultat. Ils conservent leurs propres codes d’état. Une fois la requête traitée par le filtre avec une clé, les résultats suivants s’appliquent :

| Résultat | Réponse | Action du client |
| --- | --- | --- |
| Même organisation, acteur, module, opération, clé et identité de requête avec un accusé terminé | Rejeu de la réponse 2xx stockée | Traiter la réponse comme le résultat de l’intention initiale |
| Même identité et clé, mais empreinte de requête différente | `409 idempotency_conflict` | Ne changez pas la clé pour masquer l’écart ; vérifiez l’intention et la charge utile en attente |
| Une autre transaction détient actuellement cette identité | `409 idempotency_in_progress`, avec `Retry-After: 1` | Réessayez la même requête avec la même clé après le délai indiqué |
| Le contrôle d’autorisation de requête ou de rejeu courant refuse l’accès | Résultat interdit générique du filtre d’idempotence | Rétablissez un accès valide ; n’en déduisez pas qu’un accusé existe ou non |

L’identité est limitée à l’organisation, l’acteur, le module, l’opération et le hash de la clé. Un autre acteur ou une autre opération ne partage pas l’accusé d’un appelant. Un rejeu doit toujours passer les contrôles d’autorisation et de ressource actuels. Le filtre ne rappelle pas un cas d’utilisation métier lorsqu’un accusé terminé existe.

L’accusé conserve uniquement le code d’état HTTP, le type de contenu, l’éventuel en-tête `Location` relatif à la racine et les octets exacts du corps. Il ne restaure aucun autre en-tête ni cookie de réponse, y compris les cookies d’une réponse précédente. N’utilisez pas un accusé comme source de nouveaux cookies ou d’URL temporaires.

## Exemple client

Créez la clé une fois au début de l’opération logique, conservez-la avec l’opération en attente et renvoyez la même charge utile et la même clé après un délai d’attente ou une réponse perdue. L’exemple obtient le jeton antifalsification existant de même origine et envoie les identifiants normaux des cookies HttpOnly :

```ts
const intentKey = crypto.randomUUID(); // Une fois par intention ; conserver jusqu'à connaître le résultat.
const payload = { name: "Launch", description: "Roadmap" };

async function submitCreate() {
  const antiforgery = await fetch("/api/v1/auth/antiforgery", {
    credentials: "include",
  });
  if (!antiforgery.ok) throw new Error("Antiforgery request failed");
  const { token } = await antiforgery.json() as { token: string };

  return fetch("/api/v1/projects/", {
    method: "POST",
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
      "X-CSRF-TOKEN": token,
      "Idempotency-Key": intentKey,
    },
    body: JSON.stringify(payload),
  });
}

// Si le résultat réseau est incertain, rappelez submitCreate() avec la même clé et la même charge utile.
```

La clé n’est ni un secret ni un justificatif d’autorisation. Ne consignez pas les clés, les corps de requête ou les corps d’accusé. Il n’existe ni journalisation ou métrique par clé, ni console publique d’administration pour consulter les accusés. Appliquez à la table les contrôles habituels d’accès aux données privées et de sauvegarde de la base.
