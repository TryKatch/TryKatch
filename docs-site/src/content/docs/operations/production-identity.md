---
title: Production identity
description: Certificate provisioning, encrypted key-ring upgrades, rotation, and SMTP TLS.
---

## Provision before deployment

Outside Development, the API requires three separate password-protected PKCS#12 credentials: OpenIddict signing, OpenIddict encryption, and Data Protection encryption. Compose mounts `signing.pfx`, `encryption.pfx`, `data-protection.pfx` and their corresponding `.password` files read-only from `TRYKATCH_SECRETS_PATH`. Use a secret manager, restrict access to the application UID, and keep all material outside Git and images.

Direct configuration uses `OpenIddict:SigningCertificate`, `OpenIddict:EncryptionCertificate`, and `DataProtection:Certificate`, each with `Path` and exactly one populated `Password` or `PasswordFile`. Existing OpenIddict inline passwords remain compatible; empty overlay values count as absent. Environment variables replace `:` with `__`. Password files contain the password only (terminal CR/LF removed). Password-free PFX files are rejected. Certificates are limited to 4 MiB and password files to 4 KiB.

Active certificates require current validity, a usable private key, and suitable key usage. Encryption requires RSA ≥2048 bits; signing also permits ECDSA ≥256. Retained Data Protection certificates may be expired but must retain private keys. A deliberately provisioned identity PFX is not an HTTPS server certificate; SMTP still performs normal TLS trust/hostname checks. Configuration changes require restart. See the generated `docs/production-identity.md` for the complete contract and platform details.

## Existing plaintext key rings: mandatory upgrade order

Adding `ProtectKeysWithCertificate` protects new records only. Existing plaintext keys require an explicit privileged operation; the API rejects them. There is no EF schema change. Use the following order:

1. Rehearse on a protected database copy with the target runtime. Back up the database and all private certificates/passwords separately; verify restore. Pre-upgrade backups contain plaintext key material and need equivalent protection.
2. Stop all API replicas and other key writers, including old versions. Keep them quiesced throughout maintenance. A transaction lock cannot prevent later writes by an old replica.
3. Run ordinary schema migrations if needed. Supply the owner connection through `ConnectionStrings__trykatchdb` and active/retained Data Protection certificate settings to the migrator. The effective database user must own `identity.data_protection_keys` or be a superuser; runtime roles are rejected.
4. Run a dry-run:

   ```bash
   docker compose -f compose.yml -f compose.identity-maintenance.yml run --rm --no-deps migrator --data-protection-keys dry-run
   ```

5. Only after verifying the protected backup and quiescence, apply:

   ```bash
   docker compose -f compose.yml -f compose.identity-maintenance.yml run --rm --no-deps migrator --data-protection-keys apply --confirm-key-backup true
   ```

6. Require exit code 0 and inspect the count-only JSON. Repeating apply returns `updated: 0`. Startup remains blocked on a failure; do not delete keys to make it pass.
7. Restart the new API with its least-privilege runtime credentials. Verify an existing cookie, an antiforgery-protected request, and a still-valid pending MFA enrollment. Take and verify an encrypted post-upgrade backup.

The native command is `dotnet run --project src/API/Trykatch.Migrator -c Release -- --data-protection-keys dry-run` (or `apply --confirm-key-backup true`). No secret belongs in a command argument. This mode does not run schema migrations automatically.

Maintenance locks the table transactionally, preserves key IDs/dates/revocations, wraps plaintext master keys, and verifies every original, transformed, and persisted key descriptor using fresh providers. Dry-run writes no XML; apply is all-or-nothing and idempotent. Only the template's portable .NET 10 Data Protection XML format is supported. Unknown/custom/CNG descriptors, duplicate IDs, malformed rows, and missing decryption credentials fail closed without printing XML or secrets. A separately reviewed adapter is required for other formats.

At runtime, the production XML repository validates the exact bounded, DTD-disabled snapshot returned on every initial and refresh read, using fresh cryptographic verification even for an existing key ID. Valid concurrent insertions do not cause a separate-snapshot mismatch. Writes are validated before EF persistence, and later module configuration cannot replace the repository or disable certificate encryption. This is a refresh boundary, not continuous database polling or immediate eviction of cached keys.

## Rotation and rollback

For rolling A→B rotation, first distribute B as an additional `DataProtection:DecryptionCertificates` entry while keeping A active, and restart every replica. Then set B as `DataProtection:Certificate`, retain A under `DecryptionCertificates:0`, and restart again. Mount retained certificate/password files explicitly in an overlay for both API and maintenance containers. New keys use B; old keys remain readable with A.

The plaintext maintenance operation does not re-encrypt existing certificate-wrapped records. Retain A while any key record or recoverable backup needs it, not only until cookies expire. Long-lived protected federation/invitation data and backups can require indefinite retention. Removing A requires a separately reviewed full re-encryption/retention procedure. OpenIddict credential rotation is a separate lifecycle.

Rollback must retain the ring and every required private certificate. Restore a compatible image/configuration and verify protected data before admitting traffic. Do not restart an older plaintext-writing version against the upgraded ring without first making it encryption-compatible. A restored plaintext backup requires maintenance before startup. Never delete the ring or replace all certificates to resolve a startup failure; a database-only backup cannot recover data without the private keys.

The PR03 cookie/antiforgery/pending-MFA protection purposes are unchanged. Maintenance does not rotate user security stamps, extend token expiry, or renew an expired enrollment.

## SMTP

For `--email true`, set `Email:Host`, `Port`, `From`, and explicit `Security` to `StartTls` or `SslOnConnect`. `None` is Development-only. STARTTLS is mandatory, not opportunistic; missing support, untrusted/expired certificates, hostname mismatches, or invalid credentials fail delivery without downgrade. `Username` and exactly one `Password`/`PasswordFile` must be supplied together, or both omitted for a network-controlled relay. SMTP timeout is 30 seconds. Startup validates configuration, not remote availability.

Compose uses `TRYKATCH_SMTP_*`; authenticated relays need a separately mounted password file and `TRYKATCH_SMTP_PASSWORD_FILE` pointing to its container path. AppHost run mode alone starts Mailpit with explicit `None` at the allocated development endpoint. No-email generation retains no-op notifications. Delivery errors omit SMTP replies, MIME content, credentials, and recovery URLs.

## Release gates

Local regressions exercise real HTTP, PostgreSQL key XML, the migrator process, restart/rotation/backup restore, concurrent maintenance and rollback. Loopback SMTP tests use real TLS with a per-client fixture-only CA; machine trust is unchanged. Qualify the target Linux image as a non-root user with a read-only filesystem and real secret mounts, actual relay trust, multi-replica rollout, backup/PITR restore, and maintenance downtime before production.

Sources: [Microsoft Data Protection configuration](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0), [.NET PFX platform behavior](https://learn.microsoft.com/en-us/dotnet/standard/security/cross-platform-cryptography#load-a-pkcs12pfx), [MailKit transport modes](https://mimekit.net/docs/html/T_MailKit_Security_SecureSocketOptions.htm).
