# ADR 0014: Organization-owned AI provider settings

Status: Accepted for the organization settings implementation; deployment qualification remains separate.

## Context

Organization activation alone did not fulfill the user's requirement to configure its own subscribed provider from the UI. ADR 0013's provider adapters and orchestration remain authoritative, but operator-only provider selection is extended through an explicit organization management surface, never through prompts or tool arguments.

## Decision

Administration → Settings → AI Configuration accepts provider, exact model ID, approved base endpoint, timeout and a write-only API key. View requires `organizations.read` or `organizations.manage`; save and connection test require management authority revalidated under the existing organization management lock. Existing default Admin grants are unchanged. Settings retain optimistic versions, antiforgery and forced organization RLS.

Keys are protected with ASP.NET Core Data Protection using a distinct purpose and organization ID. Only ciphertext enters the settings table. Read/update responses expose key presence, never plaintext or ciphertext. Blank key input retains the key; explicit removal requires disabling key-dependent inference. Changing provider or endpoint invalidates retention and requires a replacement for enabled key-dependent providers. Model/timeout changes may retain a key. Secret-bearing command formatting is redacted; request bodies, headers and provider errors must not be logged. The browser clears submitted key fields after success or failure and does not store credentials in browser storage.

Production uses the existing certificate-encrypted persistent Data Protection ring. Development's plaintext master-key ring is not production secret storage. Back up the database, active/retired certificates and passwords separately; retain cryptographic material while any saved credential or backup requires it. No automatic key retirement or full credential re-encryption is promised.

Tenant destinations must exactly match operator-approved HTTPS root or `/v1` bases on port 443. OpenAI Responses retains its fixed destination. Defaults approve DeepSeek and OpenAI-compatible public bases; Ollama needs an explicitly approved HTTPS root gateway. Arbitrary URLs, loopback HTTP, credentials, queries and fragments are rejected. Enrollment of additional endpoints is operator-owned configuration; operators must control their DNS/network egress. Redirects and automatic HTTP retries remain disabled. The feature is not an arbitrary proxy or automatic support for every vendor protocol.

One scoped provider session is created for each assistant turn, preserving `IChatClient`, tool permission checks and bounded orchestration. Tenant settings never mutate global options or another tenant's client. A configured but invalid/unavailable tenant provider does not fall back to the platform provider. Tenant activation stays off by default. `Assistant:AllowTenantConfiguration=false` is an operator kill switch for tenant configuration/inference; it does not alter independent legacy platform configuration.

The explicit Test connection action uses the saved version and subscription, an eight-output-token request without workspace data or tools, and at most a ten-second deadline. It may incur provider charges, disclosed beside the button. It shares the existing assistant actor/concurrency rate gates and returns only a safe success/failure result. It does not certify model tool-call compatibility or production quality.

## Consequences

Existing activation-only requests and server-managed provider settings remain supported until a tenant configures its own provider. Forward platform migrations add bounded fields without dropping retained data. Provider changes invalidate continuation tokens through the settings version. No public/anonymous AI, daily billing quotas, arbitrary environment-variable editing, streaming or write tools are introduced. Deterministic tests do not establish paid-provider UAT.
