# Exploitation des courriels en file

Les courriels utilisent l’adaptateur SMTP optionnel et le trousseau Data Protection existant de l’hôte. La file des organisations est `infrastructure.email_deliveries` ; celle de récupération est `identity.email_deliveries`. Chaque frontière exige sa connexion nommée (`trykatch-work` ou `trykatch-identity`). Aucun repli privilégié, DDL à l’exécution ou lien avec `Jobs:Enabled` n’est ajouté. La génération OpenAPI ne démarre aucun worker de courriel.

## Déploiement

Avant de déployer l’API avec courriels sur une base existante, exécutez le migrateur dédié, le provisionnement exact des rôles puis l’inspection du schéma et des droits. Les migrations Identity `20261024001650_AddIdentityEmailDelivery` et Application `20261024001751_AddOrganizationEmailDelivery` doivent être présentes. Le rôle d’organisation peut uniquement insérer dans son contexte acteur/organisation. Le coordinateur lit et met à jour sa file opérationnelle ; Identity conserve son propre rôle. Aucun droit DELETE ni travail de purge/rétention n’est ajouté.

Conservez le dépôt PostgreSQL des clés Data Protection, son discriminateur d’application et le certificat éventuel de déchiffrement lors des déploiements et restaurations. Leur perte empêche la préparation des intentions conservées. Restaurez la configuration approuvée des clés ; ne copiez jamais de jetons ou corps en clair dans les lignes de file. Une rotation doit préserver les anciennes clés nécessaires.

Configurez `Email:Host`, `Email:Port`, `Email:From` et explicitement `Email:Security` (`StartTls` ou `SslOnConnect` hors Development). Les identifiants exigent un nom d’utilisateur et un mot de passe avec une seule source : `Email:Password` ou `Email:PasswordFile`. Utilisez le mécanisme habituel de secrets du déploiement. La validation normale des certificats SMTP reste active. La marque provient des paramètres validés `Email:Branding`. Les liens de récupération et d’invitation utilisent `Application:PublicUrl` configurée, avec HTTPS en Production ; aucun en-tête host/origin de requête ne choisit cette URL.

## Soumission et résultats

Les courriels métier approuvés rejoignent la transaction métier active de l’organisation. Une invitation rejoint sa transaction invitation/audit ; la récupération utilise le stockage Identity. L’acceptation d’une intention durable ne confirme pas la transmission SMTP. `EmailQueued` reste distinct de `EmailDelivered` et les instructions de copie du lien restent disponibles. La récupération publique conserve une réponse générique sans reçu propre au compte.

Avant chaque tentative, l’adaptateur source vérifie à nouveau l’autorisation de divulgation, le destinataire courant, le modèle/version et la validité du jeton/lien d’origine. Une révocation, un destinataire modifié ou un jeton expiré empêche l’envoi. Une invitation en attente conserve sa propre autorisation lorsque son auteur quitte l’organisation ; révoquez l’invitation pour y mettre fin. La transaction de préparation se termine avant SMTP. Un courriel déjà transmis ne peut pas être rappelé.

Le dispatcher réclame au plus un enregistrement par frontière et par passage. Il autorise cinq tentatives, un bail de deux minutes et une tentative de 45 secondes au plus, avec cinq secondes réservées à son enregistrement final. Les résultats transitoires ou incertains utilisent un délai borné. Une perte de connexion après acceptation SMTP ou de l’accusé d’enregistrement peut rendre le résultat incertain. La reprise d’enregistrement conserve exactement le même bail/résultat sans renvoi SMTP. Un bail ultérieur peut toutefois produire un doublon si l’envoi accepté n’a pas été enregistré : cette livraison ne garantit pas un envoi unique.

`queued` attend une tentative, `leased` indique une tentative en cours, `delivered` enregistre une acceptation SMTP, `suppressed` une décision empêchant la divulgation et `terminal` nécessite une investigation. Aucune action opérateur de rejeu, purge, envoi libre ou téléchargement du contenu n’est fournie. Les interfaces synchrones existantes restent des chemins de compatibilité explicites.

## Consultation et aperçus synthétiques

Créez un rôle de plateforme personnalisé avec `platform.email.read` et attribuez-le explicitement à l’opérateur désigné via la gestion des accès existante. Aucun rôle intégré ne reçoit cette permission automatiquement, y compris Administrator. Un Administrator actif peut créer et attribuer ce rôle sans recevoir lui-même l’accès de consultation. Le retrait de la permission ou la suspension bloque les requêtes suivantes, même depuis une session existante.

La page est `/dashboard/email-delivery`. Les routes en lecture seule sont :

| Route | Opération |
| --- | --- |
| `GET /api/v1/platform/email-deliveries` | `EmailDelivery_List` |
| `GET /api/v1/platform/email-deliveries/templates` | `EmailTemplates_List` |
| `GET /api/v1/platform/email-deliveries/templates/preview` | `EmailTemplates_Preview` |

La liste sélectionne une frontière `boundary` : `organization` par défaut ou `identity`. Les filtres sont `state`, `deliveryId` et, uniquement pour une organisation, `organizationId`. La taille de page est bornée à 1–100 et le numéro à 1–10 000. Les modèles sont paginés et un aperçu exige un `templateKey`/`templateVersion` enregistré exact. Un adaptateur absent renvoie une indisponibilité sans sélectionner une base de repli. Chaque opération vérifie les droits explicites actuels avant toute lecture de file ou rendu synthétique.

La réponse contient l’identifiant opaque de livraison, la frontière/l’organisation, un modèle/version connu, l’état, les tentatives, les dates et des codes d’échec/statut autorisés. Les noms de modèles inconnus et textes d’erreur sont masqués. La projection SQL exclut l’intention protégée, le destinataire/jeton et la provenance acteur/utilisateur/bail. Les aperçus proviennent uniquement de données synthétiques compilées, jamais d’un courriel réel. La page affiche l’objet et le texte sans exécuter le HTML du courriel.

## Diagnostic et qualification

Le compteur `trykatch.email.delivery.attempts` du meter `Trykatch.EmailDelivery` utilise uniquement les dimensions bornées `boundary` et `outcome`. La source d’activités `Trykatch.EmailDelivery` émet `email delivery` avec `messaging.message.id` opaque et une frontière/un résultat bornés. Cet identifiant permet la corrélation avec la consultation sûre ; il ne devient jamais une dimension métrique. `acknowledged` décrit le résultat SMTP, sans garantir la réussite de son enregistrement. Les échecs d’enregistrement ont leurs propres résultats bornés, dont `completion_unavailable` et `lease_lost`.

Ne journalisez jamais les destinataires, jetons, liens, paramètres privés, paquets protégés ou erreurs SMTP/adaptateur brutes. Utilisez les codes fermés et les compteurs sûrs. La qualification utilise PostgreSQL jetable et SMTP local, sans destinataire externe. Les [preuves E5](../specs/evidence/parity-execution-e5.md) distinguent les vérifications effectuées des qualifications de déploiement, de redémarrage de processus et de publication encore nécessaires.
