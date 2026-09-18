# AI providers and chat behavior

## Provider-neutral inference

The running assistant uses Microsoft.Extensions.AI.IChatClient, not Microsoft Agent Framework. The host explicitly selects an adapter: OpenAI Responses, native Ollama chat or configurable Chat Completions, including a DeepSeek-compatible endpoint. Module tools and guide retrieval use the neutral runtime, not vendor message types. Compatibility still depends on the configured vendor/model protocol; this does not promise ready-made support for every vendor or Azure-specific authentication.

The platform provider remains operator-owned configuration. Organizations can instead configure their subscription through the management UI described below. Enabled configuration is validated; invalid or failed organization providers never silently fall back to platform credentials. Platform `Assistant:TimeoutMs` defaults to 45,000 milliseconds and must be between 1,000 and 60,000 when enabled, matching organization settings. Invalid deadlines fail startup validation and are rejected before inference; `-1` cannot disable the deadline. For local platform Development settings, use dotnet user-secrets on the API project; user-secrets is not encrypted production secret management. Never paste keys into chat, commit them, log them or return saved keys to the browser.

## Configure an organization's subscription

Each organization starts with AI Help disabled. Its Owner, or a delegated role with `organizations.manage`, opens **Administration → Settings → AI Configuration**. Readers with `organizations.read` can inspect metadata but cannot change it; the default Admin role is read-only here unless management permission is delegated.

1. Select DeepSeek, OpenAI Responses, OpenAI-compatible Chat Completions or Ollama. Enter the exact model ID from your subscription.
2. For compatible providers, choose an approved endpoint. OpenAI Responses has a fixed destination. Ollama needs an operator-approved HTTPS root gateway; this UI does not accept arbitrary destinations or HTTP loopback URLs.
3. Enter your subscription's API key and a timeout between 1,000 and 60,000 milliseconds. Saved keys are write-only: only their presence is displayed. An empty replacement field keeps the existing key. Changing provider or endpoint requires a replacement key for enabled key-dependent providers. Disable AI before explicitly removing a required key.
4. Select **Enable AI Help for this organization**, then **Save changes**.
5. Click **Test connection** after saving. This sends a tiny request without workspace data or tools and may incur provider charges. A successful test does not prove tool-calling compatibility; verify the actual model with normal AI Help usage.

## Storage and destination safety

The configuration is organization-isolated in PostgreSQL with forced RLS. Apply forward platform migrations through normal Migrator startup. Saves and tests require antiforgery and the saved version; reload after conflicts. Submitted keys are cleared from the form after success or failure and encrypted server-side with an organization-bound Data Protection purpose. Production requires the existing certificate-encrypted persistent key ring; retain its certificates/passwords and backups while saved credentials need them. See [production identity](../production-identity.md).

Operators can disable tenant configuration with `Assistant__AllowTenantConfiguration=false`. Additional destinations must be enrolled in `Assistant:AllowedTenantEndpoints` on the server, as an explicit list of trusted HTTPS root or `/v1` bases on port 443; control their DNS and network egress. Defaults include DeepSeek and OpenAI-compatible bases. This is not native support for every vendor protocol. With no organization override, the explicitly enabled platform provider can still be used after organization opt-in. Invalid overrides never fall back. Anonymous/public AI and daily billing quotas are not introduced.

## Development migrations

For development migration scaffolding, run from the application root:

```bash
dotnet ef migrations add YourChange --project src/Common/Trykatch.Infrastructure --context PlatformDbContext --output-dir Persistence/Migrations/Platform
```

The design-time factory uses a non-production placeholder connection unless `TRYKATCH_DESIGN_CONNECTION` is supplied. Scaffolding does not connect to the database; apply migrations through the configured Migrator, not against that placeholder.

## Read-only chat and memory

AI Help can explain these curated guides and read authorized project/document metadata through explicitly registered module adapters. It cannot mutate records, upload files, run shell commands, inspect arbitrary source code or grant permissions. The model sees guide excerpts as reference data, not instructions. Architecture guidance describes the shipped documented design and enabled declarations, not automatic verification of every custom implementation.

Recent completed text exchanges provide follow-up context: at most four exchanges and an absolute 20-minute conversation lifetime, with additional byte limits. The browser keeps the transcript in memory; close/reopen preserves it while the organization shell is mounted. Refresh, logout, workspace change or New conversation clears it. Older displayed messages may no longer be in the model's context. Expired/changed-access continuation requires a new conversation, without automatic paid retries.

The API allows ten asks per actor per minute and at most eight concurrent assistant asks per instance, without queuing. The concurrency guard runs before organization transaction middleware to bound slow calls holding database connections. Busy requests return 429; other routes do not consume assistant concurrency slots. These limits are per replica, not a distributed quota.

## Privacy and limitations

Questions, recent conversation context, approved guide excerpts and retrieved authorized workspace data are sent to the selected AI provider. Files are not uploaded by this assistant. Provider-specific retention terms still apply; an application-level store flag is not a universal zero-retention guarantee. Answers can be wrong. Inspect Guides consulted and validate important details in the normal UI/API. Internal list pagination metadata should be described in plain language by default, not exposed as JSON or inferred totals.
