---
title: Template options
description: Reference the generated UI and optional adapters available from dotnet new.
---

| Option | Default | Result |
| --- | --- | --- |
| `--display-name "Kamenta"` | project name (`-n`) | Overrides the customer-facing UI and email brand independently of the technical project name. |
| `--ui react` | enabled | Generates the React, TanStack, Vite, owned UI, and API-client workspace. |
| `--ui none` | disabled | Generates the backend without the web workspace. |
| `--email true` | disabled | Adds MailKit SMTP and a Mailpit development resource. |
| `--storage true` | enabled | Runs a private RustFS resource in local Aspire development and adds a provider-neutral S3-compatible adapter. Use `--storage false` for filesystem-backed local storage. |
| `--documents true` | disabled | Adds Open XML SDK and PDFsharp/MigraDoc adapters. |
| `--images true` | disabled | Adds SkiaSharp validation, resizing, and metadata removal. |

Aspire pulls the pinned RustFS image for local development. Production must use a supported, patched S3-compatible service with bucket-scoped credentials.
