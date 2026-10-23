---
title: Options du modèle
description: Consultez l’interface générée et les adaptateurs optionnels proposés par dotnet new.
---

| Option | Valeur par défaut | Résultat |
| --- | --- | --- |
| `--display-name "Kamenta"` | nom du projet (`-n`) | Remplace la marque affichée dans l’interface et les e-mails indépendamment du nom technique du projet. |
| `--ui react` | activée | Génère l’espace de travail React, TanStack, Vite, l’interface possédée et le client API. |
| `--ui none` | désactivée | Génère le backend sans l’espace de travail web. |
| `--email true` | désactivée | Ajoute MailKit SMTP et une ressource de développement Mailpit. |
| `--storage true` | activée | Lance une ressource RustFS 1.0.3 privée avec Aspire en développement local et ajoute un adaptateur générique compatible S3. Utilisez `--storage false` pour le stockage local sur le système de fichiers. |
| `--documents true` | désactivée | Ajoute les adaptateurs Open XML SDK et PDFsharp/MigraDoc. |
| `--images true` | désactivée | Ajoute la validation, le redimensionnement et la suppression des métadonnées avec SkiaSharp. |

RustFS 1.0.3 est téléchargé comme conteneur de développement local Apache-2.0 séparé ; son image est épinglée sur `rustfs/rustfs:1.0.3@sha256:9ca9559533b537d10482fe923288b676ad08f92a7069d9a17327fbae3cab688b`. Il n’est pas intégré à l’application générée et ne constitue pas une déclaration de préparation à la production. La production continue d’utiliser un service compatible S3 géré à l’extérieur, avec des identifiants limités au compartiment. Une mise à jour de la CLI ou du modèle ne réécrit pas les applications déjà générées ; conservez leur ancien volume jusqu’à la vérification de toute copie d’objets planifiée séparément.
