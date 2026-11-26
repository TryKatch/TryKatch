---
title: Template options
description: Reference the generated UI and optional adapters available from dotnet new.
---

| Option | Default | Result |
| --- | --- | --- |
| `--display-name "Kamenta"` | project name (`-n`) | Overrides the customer-facing UI and email brand independently of the technical project name. |
| `--ui react` | enabled | Generates the React, TanStack, Vite, owned UI, and API-client workspace. |
| `--ui none` | disabled | Generates the backend without the web workspace. |
| `--module-profile full` | default | Enables the Projects and Documents reference modules. |
| `--module-profile minimal` | opt-in | Keeps reference module source and migration history, but starts with no example module enabled. |
| `--email true` | disabled | Adds MailKit SMTP and a Mailpit development resource. |
| `--storage true` | enabled | Runs a private RustFS 1.0.7 resource in local Aspire development and adds a provider-neutral S3-compatible adapter. Use `--storage false` for filesystem-backed local storage. |
| `--documents true` | disabled | Adds Open XML SDK and PDFsharp/MigraDoc adapters. |
| `--images true` | disabled | Adds SkiaSharp validation, resizing, and metadata removal. |

RustFS 1.0.7 is pulled as a separate Apache-2.0 local-development container; its image is pinned to `rustfs/rustfs:1.0.7@sha256:69bd3c4d8d79b74723bfd68c9fbd9085155d4d4b44a5a136d87502f3075cc68c`. It is not embedded in the generated application and is not a production-readiness claim. Production continues to use an externally managed S3-compatible service with bucket-scoped credentials. Existing generated applications are not rewritten by a CLI/template update; preserve their old storage volume until any separately planned object copy is verified.
