# Queued email operations

Queued email uses the existing optional SMTP adapter and shared host Data Protection key ring. The organization queue belongs to `infrastructure.email_deliveries`; recovery email belongs to `identity.email_deliveries`. The API composes each boundary only with its explicit named connection (`trykatch-work` or `trykatch-identity`). There is no privileged connection fallback, runtime DDL or dependency on `Jobs:Enabled`. OpenAPI generation starts no email worker.

## Rollout

Before deploying the email-enabled API against an existing database, run the one-shot migrator and exact runtime-role provisioner, then schema/role inspection. Identity migration `20261024001650_AddIdentityEmailDelivery` and Application migration `20261024001751_AddOrganizationEmailDelivery` must be present. Organization runtime may insert only in its actor/organization scope; the coordinator may read/update only its owned operational queue. Identity uses its own runtime role. Neither queue grants DELETE, and this feature introduces no retention/purge job.

Preserve the existing PostgreSQL Data Protection key repository, its application discriminator and any certificate required to unwrap its keys across deployments and restores. Losing those keys prevents preparation of retained intent. Restore the reviewed host key configuration; never copy plaintext tokens or message bodies into queue rows. Key rotation must preserve old decryption keys for retained intent.

Configure `Email:Host`, `Email:Port`, `Email:From` and explicit `Email:Security` (`StartTls` or `SslOnConnect` outside Development). Credentials require both username and password, with exactly one password source (`Email:Password` or `Email:PasswordFile`). Keep credentials in the deployment's established secret mechanism. Normal SMTP certificate validation remains enabled. Branding uses the validated `Email:Branding` settings. Core reset/invitation links use the configured `Application:PublicUrl`; Production requires HTTPS. Request host/origin headers never select that URL.

## Submission and outcomes

Approved business email joins the active organization business transaction. Core invitation email joins the invitation/audit transaction. Recovery email uses its Identity-owned store. Submission accepts a durable intent; it does not confirm SMTP delivery. The invitation result reports `EmailQueued` separately from `EmailDelivered`, and copy-link guidance remains available. Public password recovery retains its generic response without an account-specific queue receipt.

Before each attempt, the source adapter rechecks current recipient/source authority, approved template/version and original token/link validity. Changed recipients, revoked source access or expired tokens suppress transmission. A pending organization invitation remains its own grant when the inviter leaves; revoke the invitation to end that grant. Preparation closes its database transaction before SMTP. Previously transmitted mail cannot be recalled.

The dispatcher claims at most one record per boundary per pass, allows five attempts, uses a two-minute lease and a whole-attempt limit of 45 seconds with five seconds reserved for completion. Transient and uncertain outcomes use bounded backoff. Acknowledgement and completion are separate: connection loss after SMTP acceptance or loss of the recording acknowledgement can leave uncertainty. The dispatcher retries only the exact completion lease/outcome within its recording budget; it never resends within that completion retry. A later lease after an unrecorded accepted send can still produce duplicate mail. Do not describe this delivery path as exactly once.

`queued` awaits an eligible attempt, `leased` is in progress, `delivered` records SMTP acknowledgement, `suppressed` records a disclosure/intent decision, and `terminal` requires investigation. There is no replay, purge, arbitrary-send or payload-download operator action in this feature. Existing synchronous sender/notifier interfaces remain explicit compatibility paths.

## Inspection and synthetic previews

Create a custom platform role with `platform.email.read` and assign it explicitly to the designated operator through existing platform access management. No built-in role receives this permission automatically, including Administrator. An active Administrator can create and grant the custom role without gaining email inspection themselves. Removing the permission or suspending access denies subsequent requests using an existing session.

The platform page is `/dashboard/email-delivery`. Read-only endpoints are:

| Route | Operation |
| --- | --- |
| `GET /api/v1/platform/email-deliveries` | `EmailDelivery_List` |
| `GET /api/v1/platform/email-deliveries/templates` | `EmailTemplates_List` |
| `GET /api/v1/platform/email-deliveries/templates/preview` | `EmailTemplates_Preview` |

Listing selects one `boundary` (`organization`, the default, or `identity`) and supports `state`, `deliveryId`, and organization-only `organizationId`. Page size is clamped to 1–100 and page to 1–10,000. Templates are also paged; previews require an exact registered `templateKey`/`templateVersion`. Missing runtime adapters return unavailable rather than selecting a fallback database. Every operation checks current explicit authority before accessing a queue or rendering a fixture.

The response contains opaque delivery ID, boundary/organization ID, known template/version, state, attempt count, timestamps and allowlisted failure/status codes. Unknown template names and failure text are masked. The SQL projection excludes protected intent, recipient/token data and actor/user/lease provenance. Previews use only compiled synthetic fixture data, never a real delivery. The page displays the subject and text body without executing email HTML.

## Diagnostics and qualification

Meter `Trykatch.EmailDelivery` exports `trykatch.email.delivery.attempts` with only bounded `boundary` and `outcome` dimensions. Activity source `Trykatch.EmailDelivery` emits `email delivery` attempts with opaque `messaging.message.id`, bounded boundary and outcome. Correlate that ID with safe inspection; it is never a metric dimension. `acknowledged` is the transport outcome, not proof that a completion write succeeded. Completion faults have separate bounded outcomes such as `completion_unavailable` and `lease_lost`.

Do not log recipient addresses, tokens, links, template data, protected packets or raw SMTP/adapter errors. Use the closed failure codes and safe counters. Qualification uses disposable PostgreSQL and loopback SMTP only; no external recipient is used. The [E5 evidence](../specs/evidence/parity-execution-e5.md) distinguishes completed checks from remaining deployment, process-restart and release qualification.
