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
| `--storage true` | activée | Lance une ressource RustFS privée avec Aspire en développement local et ajoute un adaptateur générique compatible S3. Utilisez `--storage false` pour le stockage local sur le système de fichiers. |
| `--documents true` | désactivée | Ajoute les adaptateurs Open XML SDK et PDFsharp/MigraDoc. |
| `--images true` | désactivée | Ajoute la validation, le redimensionnement et la suppression des métadonnées avec SkiaSharp. |

Aspire télécharge l’image RustFS épinglée pour le développement local. En production, utilisez un service compatible S3 corrigé et pris en charge, avec des identifiants limités au compartiment.
