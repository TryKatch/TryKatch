---
title: Template options
description: Reference the generated UI and optional adapters available from dotnet new.
---

| Option | Default | Result |
| --- | --- | --- |
| `--ui react` | enabled | Generates the React, TanStack, Vite, owned UI, and API-client workspace. |
| `--ui none` | disabled | Generates the backend without the web workspace. |
| `--email true` | disabled | Adds MailKit SMTP and a Mailpit development resource. |
| `--storage true` | disabled | Adds local development storage and an S3-compatible adapter. |
| `--documents true` | disabled | Adds Open XML SDK and PDFsharp/MigraDoc adapters. |
| `--images true` | disabled | Adds SkiaSharp validation, resizing, and metadata removal. |

All optional modules use free and redistributable packages under the repository dependency policy.
