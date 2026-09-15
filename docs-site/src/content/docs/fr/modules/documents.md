---
title: Envoi de documents
description: Comprendre le module Documents de référence, la ressource MinIO locale et la frontière du stockage en production.
---

Projects et Documents sont tous deux activés dans l’application de départ, mais ils illustrent des formes de modules différentes. Projects représente un CRUD ordinaire appartenant à une organisation. Documents est un flux de fichiers full-stack : le navigateur envoie un fichier à l’API autorisée, PostgreSQL conserve les métadonnées recherchables et le stockage objet privé conserve les octets.

## Développement local

`trykatch start` démarre MinIO avec Aspire lorsque l’application a été générée avec l’option par défaut `--storage true`. Aspire lie les ports de développement dynamiques de MinIO à l’interface locale et ne les publie pas comme points d’accès externes ; le navigateur ne reçoit jamais leurs identifiants.

1. Démarrez Docker Desktop ou un autre moteur de conteneurs pris en charge par Aspire.
2. Exécutez `trykatch start` depuis la racine de l’application générée.
3. Connectez-vous, ouvrez **Documents**, puis choisissez **Envoyer un document**.
4. Choisissez un fichier PDF, Office, texte, CSV, JPEG, PNG ou WebP non vide de 25 Mo maximum. La fenêtre affiche son nom et sa taille et propose un titre à partir du nom du fichier ; vous pouvez modifier ce titre.
5. Choisissez un **Type de document** : Facture, Contrat, Certificat, Rapport ou Autre. Ajoutez une description facultative, puis choisissez **Envoyer un document**. Gardez la fenêtre ouverte pendant l’envoi.
6. Le type enregistré apparaît dans le tableau et les détails du document. Utilisez **Modifier** pour changer le titre, le type ou la description sans renvoyer le fichier. Ouvrez **Voir**, puis **Télécharger**, pour vérifier le flux autorisé.

## Classification métier

Le type de document décrit son usage, pas son format : une facture peut être un PDF ou une image. L’API utilise les valeurs stables `invoice`, `contract`, `certificate`, `report` et `other` ; leurs libellés sont traduits en anglais et en français. L’API et la contrainte de base de données rejettent les valeurs inconnues.

La migration additive du module classe les documents existants comme `other`, sans modifier leurs fichiers ni leur isolation par organisation. Un ancien client qui omet `documentType` lors de l’envoi obtient aussi `other`. Lors d’une modification de métadonnées, l’omission conserve la classification existante. Sous Aspire, le migrateur applique ce changement avant le démarrage de l’API. La mise à jour du package CLI/template ne modifie pas à elle seule le code d’une application déjà générée.

`--storage false` conserve le même contrat côté hôte, mais utilise le système de fichiers local. Ce mode convient au développement simple ; il ne convient pas aux conteneurs de production en lecture seule ou répliqués horizontalement.

## Sécurité et isolation

L’API vérifie `documents.manage` pour les envois et les changements de métadonnées, et `documents.read` pour la liste et le téléchargement. Les mutations exigent également un jeton antifalsification. Le serveur crée une clé opaque de la forme `organizations/{organization-id}/documents/{document-id}` ; les noms fournis par l’utilisateur ne deviennent jamais des chemins de stockage. Cette clé et tous les identifiants de stockage restent absents des réponses API.

La table `app.documents` utilise le même filtre automatique d’organisation et la même politique RLS PostgreSQL forcée que les autres données d’organisation. Un utilisateur doit d’abord pouvoir lire l’enregistrement de métadonnées isolé avant que l’API ne demande les octets au stockage objet. Les noms de fichiers sont normalisés, les types sont autorisés explicitement, la taille de la requête est limitée et chaque envoi enregistre une empreinte SHA-256. Si l’enregistrement des métadonnées échoue après l’envoi de l’objet, l’application tente de supprimer l’objet en compensation.

L’archivage et la demande de suppression conservent les octets parce que le flux central de récupération peut restaurer l’enregistrement. La suppression physique du stockage appartient à un worker séparé de politique de rétention, avec son propre contrat d’autorisation et d’audit.

## Production

Documents dépend du contrat neutre `IObjectStorage`. MinIO est uniquement l’adaptateur de développement démarré par Aspire ; les couches Domain et Application du module ne référencent ni MinIO ni un SDK S3. L’adaptateur d’infrastructure fourni fonctionne avec les points d’accès compatibles S3, et un autre fournisseur peut le remplacer en implémentant le même contrat sans modifier le module Documents.

L’image communautaire MinIO épinglée est uniquement un outil de développement local. Les avis de sécurité MinIO actuels orientent les utilisateurs de production vers des versions corrigées et prises en charge ; Trykatch ne présente donc pas le conteneur MinIO local comme une recommandation de stockage en production.

Configurez l’API de production avec `Storage__ServiceUrl`, `Storage__AccessKey`, `Storage__SecretKey`, `Storage__Bucket` et `Storage__Region`. Créez le compartiment privé à l’avance, définissez `Storage__CreateBucket` sur `false`, limitez l’identité de l’application à ce seul compartiment, utilisez TLS, activez le chiffrement et la gestion des versions selon votre politique de données, puis surveillez la capacité et les échecs d’opérations objet.
