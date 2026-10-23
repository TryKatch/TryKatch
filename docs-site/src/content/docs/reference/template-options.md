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
| `--storage true` | enabled | Runs a private RustFS 1.0.3 resource in local Aspire development and adds a provider-neutral S3-compatible adapter. Use `--storage false` for filesystem-backed local storage. |
| `--documents true` | disabled | Adds Open XML SDK and PDFsharp/MigraDoc adapters. |
| `--images true` | disabled | Adds SkiaSharp validation, resizing, and metadata removal. |

RustFS 1.0.3 is pulled as a separate Apache-2.0 local-development container; its image is pinned to `rustfs/rustfs:1.0.3@sha256:9ca9559533b537d10482fe923288b676ad08f92a7069d9a17327fbae3cab688b`. It is not embedded in the generated application and is not a production-readiness claim. Production continues to use an externally managed S3-compatible service with bucket-scoped credentials. Existing generated applications are not rewritten by a CLI/template update; preserve their old storage volume until any separately planned object copy is verified.
