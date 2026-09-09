---
title: Options du modèle
description: Consultez l’interface générée et les adaptateurs optionnels proposés par dotnet new.
---

| Option | Valeur par défaut | Résultat |
| --- | --- | --- |
| `--ui react` | activée | Génère l’espace de travail React, TanStack, Vite, l’interface possédée et le client API. |
| `--ui none` | désactivée | Génère le backend sans l’espace de travail web. |
| `--email true` | désactivée | Ajoute MailKit SMTP et une ressource de développement Mailpit. |
| `--storage true` | désactivée | Ajoute le stockage de développement local et un adaptateur compatible S3. |
| `--documents true` | désactivée | Ajoute les adaptateurs Open XML SDK et PDFsharp/MigraDoc. |
| `--images true` | désactivée | Ajoute la validation, le redimensionnement et la suppression des métadonnées avec SkiaSharp. |

Tous les modules optionnels utilisent des packages gratuits et redistribuables conformément à la politique de dépendances du dépôt.
