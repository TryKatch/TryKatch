# Production identity credentials and key maintenance

## Configuration contract

All environments except `Development` require durable PostgreSQL Data Protection keys encrypted with an operator-supplied certificate. OpenAPI generation is an offline tooling exception, not a deployment environment. Startup fails before serving when credentials are missing/unusable, stored keys are plaintext/malformed, or a required decryption certificate is absent. Development deliberately permits plaintext database keys; never promote that ring without the maintenance operation below.

Use three separate password-protected PKCS#12 files: `signing.pfx` (OpenIddict signing), `encryption.pfx` (OpenIddict encryption), and `data-protection.pfx` (cookies, antiforgery, identity tokens, pending MFA, and other protected application data). Compose mounts these and their corresponding `signing.password`, `encryption.password`, and `data-protection.password` files read-only. Provision through your secret manager, not Git, build arguments, image layers, or command-line password arguments. Grant only the application UID read access; protect the host directory. Temporary acceptance assets are not production key provisioning.

The three active credentials must use distinct private keys. Startup compares public-key identity, rejecting copied or reissued certificates that reuse a key even under different paths or subjects. Data Protection also rejects duplicate retained keys and an active key repeated in `DecryptionCertificates`; the active certificate already supports decryption. During A→B rotation, replace the staged B decryption entry with A when B becomes active.

| Purpose | Certificate path | Password source |
| --- | --- | --- |
| OpenIddict signing | `OpenIddict:SigningCertificate:Path` | `Password` or `PasswordFile` in the same section |
| OpenIddict encryption | `OpenIddict:EncryptionCertificate:Path` | `Password` or `PasswordFile` |
| Active Data Protection encryption | `DataProtection:Certificate:Path` | `Password` or `PasswordFile` |
| Retained Data Protection decryption | `DataProtection:DecryptionCertificates:0:Path` (then `1`, etc.) | `Password` or `PasswordFile` per entry |

Environment variables replace `:` with `__`. Existing OpenIddict `Path`/`Password` settings still work. Compose now prefers mounted password files; do not leave an inline password configured alongside a password file. Password files contain only the password; terminal CR/LF is removed. Password-free PFX files are no longer accepted outside Development. Limits: 4 MiB per certificate and 4 KiB per password file.

Active certificates must be within their validity period, contain a usable private key, and permit the purpose when Key Usage is present. All identity certificates require RSA ≥2048 bits, including OpenIddict signing. ECDSA signing certificates are rejected during configuration validation; a separately reviewed signing-key/algorithm adapter is required to support them. Retained decryption certificates may be expired, but must retain usable private keys. These are provisioned cryptographic credentials, not HTTPS certificates: this loader does not apply web-PKI hostname/chain trust to a deliberately provisioned PFX. SMTP still uses normal TLS chain/hostname validation. Changes require restart; no hot reload is promised. Linux/Windows use ephemeral imports; macOS uses temporary keychain imports without persistent-key flags, disposed with the host.

## Upgrade an existing plaintext ring

`ProtectKeysWithCertificate` protects new keys, not existing records. An EF migration or merely restarting with a certificate cannot retrofit a plaintext ring. This release changes no database schema and requires no new EF migration. Run ordinary schema migrations first when needed, using the owner/migrator credential. Never grant the API the migrator connection.

1. Rehearse on a protected database copy with the exact target runtime build. Back up the database and all current/retired private certificates and passwords separately; prove restore. Treat the pre-upgrade backup as plaintext secret material.
2. Stop every API replica and other Data Protection writer, including old-version instances. Keep them stopped until apply and preflight succeed. A transaction lock cannot prevent an old replica from writing another plaintext key after maintenance exits.
3. Supply the owner connection as `ConnectionStrings__trykatchdb` through the secret manager. The effective database user must own `identity.data_protection_keys` or be a superuser; runtime roles are explicitly rejected even if accidentally granted update privileges. Supply active and all retained DP certificate settings. OpenIddict credentials are not needed by this operation.
4. Run the dry-run and inspect only its counts. It validates all key records and decrypts every descriptor using a fresh provider; it performs no XML writes.

   ```bash
   docker compose -f compose.yml -f compose.identity-maintenance.yml run --rm --no-deps migrator --data-protection-keys dry-run
   ```

5. After a verified protected backup and quiescence, explicitly attest and apply:

   ```bash
   docker compose -f compose.yml -f compose.identity-maintenance.yml run --rm --no-deps migrator --data-protection-keys apply --confirm-key-backup true
   ```

   Native equivalent: `dotnet run --project src/API/Trykatch.Migrator -c Release -- --data-protection-keys dry-run` (or `apply --confirm-key-backup true`). Credentials remain environment/secret-file configuration, not command arguments. Maintenance mode exits without executing normal schema migrations.

6. Expect JSON `{ "mode": "apply", "keys": N, "plaintext": N, "updated": N }`. Repeating apply returns `updated: 0`; exit code 1 is failure and must block rollout. The operation takes a transaction-scoped table lock, preserves key IDs/dates/revocations, wraps only plaintext master keys, verifies transformed and persisted XML with fresh key managers, and commits all changes together. It does not print XML, secret values, or cryptographic exception details.
7. Start the new API, verify an existing session and a pre-upgrade antiforgery-protected request, and complete a still-valid pending MFA enrollment. Existing application name and protection purposes are unchanged; token/enrollment expiry is not extended. Take and verify a new encrypted backup.

The supported ring is the template's portable ASP.NET Core authenticated-encryption XML format (version 1, .NET 10 descriptor/decryptor types). Unknown/custom/CNG descriptors, unsupported versions, duplicate key IDs, malformed records, or undecryptable keys fail closed without partial writes. Do not delete an offending key or bypass validation: restore a known-good backup or build a separately reviewed format adapter. No arbitrary XML deserializer type is activated before the allowlist checks.

The production XML repository validates one bounded, DTD-disabled snapshot on every initial or refresh read, including fresh cryptographic verification independent of the framework's key-ID cache. It returns precisely that validated snapshot; concurrent valid insertions are picked up by subsequent reads. Writes must also be encrypted and valid before normal EF persistence. Later module option registrations cannot replace this repository or disable certificate encryption. Refresh validation does not continuously poll the database or revoke already cached keys between framework refreshes.

Runtime reads and both maintenance reads share a single ordered PostgreSQL projection that checks every row with `octet_length`: XML is limited to 1 MiB per record, including multibyte text. If any record is oversized, empty, or null, the query returns only row IDs and null failure markers, not any XML. This prevents oversized text from reaching client materialization and leaves rejected rows untouched; the limit is per record, not a total key-count quota.

## Rotation A → B and retention

1. Provision B's private certificate/password to all replicas. For rolling deployment, first keep A active and add B to `DecryptionCertificates` on every replica; restart and verify.
2. Then make B the active `DataProtection:Certificate` and retain A under `DecryptionCertificates:0`; restart. New keys are wrapped by B. Old records remain wrapped by A. Mount retained files explicitly using a deployment overlay, and pass the same retained settings/mounts to the maintenance container.
3. Test newly issued and existing cookies/protected payloads, restart again, and restore a protected backup in isolation. A missing A must fail startup while an A-wrapped record remains.

The plaintext maintenance operation intentionally does not re-encrypt existing certificate-wrapped records or delete old keys. Retain A for as long as any stored key or recoverable backup needs it, not merely until cookies expire: federation secrets, invitation material, and backups may outlive browser sessions. Retirement of A requires a separately reviewed full re-encryption/retention procedure; no finite automatic retirement window is promised. OpenIddict rotation is a separate token-signing/encryption lifecycle and is not expanded by this DP rotation list.

Do not roll back by removing certificates or deleting the ring. Restore a compatible image/configuration with all required private certificates; a prior image that writes plaintext must remain quiesced or be upgraded with encryption configuration before it can write. Plaintext backup restoration requires maintenance again before starting this release. Losing a required private key may irreversibly invalidate protected data; a database-only backup is insufficient.

## Optional SMTP adapter

With `--email true`, configure `Email:Host`, `Port` (1–65535), `From`, and explicit `Security`: `StartTls` (usually 587), `SslOnConnect` (usually 465), or `None` (Development only). `StartTls` requires the advertised upgrade; it never falls back to plaintext. `SslOnConnect` starts TLS immediately. Certificate-chain, hostname, and validity checks are not disabled. No `Auto` or opportunistic mode is accepted.

Set `Email:Username` and exactly one of `Email:Password` / `Email:PasswordFile` together, or omit both for a network-controlled relay. Compose exposes `TRYKATCH_SMTP_*` variables; for authenticated SMTP, mount the password file separately and point `TRYKATCH_SMTP_PASSWORD_FILE` to its container path. SMTP timeout is 30 seconds. Startup validates configuration, not remote availability; delivery failures are safe generic exceptions without remote replies, credentials, MIME bodies, or reset/invitation links. Failure never downgrades TLS.

AppHost run mode alone starts Mailpit and explicitly sets `None` with its allocated development endpoint. AppHost publish is not a production SMTP publisher. A no-email template keeps its existing no-op notification behavior; select and provision an adapter if email recovery/invitations are required.

## Verification and deployment-owned gates

Integration tests use disposable PostgreSQL, actual HTTP account endpoints, the actual migrator command, persisted XML inspection, restart/backup/rotation, rollback and concurrent maintenance. SMTP tests use real loopback SMTP/TLS and a per-client fixture-only CA trust store; no machine trust is changed. Testcontainers is the fallback when no native test connection is supplied.

Before production, qualify the generated image on the target Linux runtime as its non-root UID with read-only root filesystem, actual secret-volume permissions, your SMTP relay/trust chain, multi-replica rotation, protected backup/PITR restoration, and maintenance downtime. A local macOS/native test is not that deployment qualification.

References: [Microsoft Data Protection configuration](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0), [.NET PFX platform behavior](https://learn.microsoft.com/en-us/dotnet/standard/security/cross-platform-cryptography#load-a-pkcs12pfx), [MailKit transport modes](https://mimekit.net/docs/html/T_MailKit_Security_SecureSocketOptions.htm).
