---
title: Module data isolation
description: Understand the mandatory ownership, database placement, PostgreSQL RLS, and package-trust contract for persistent modules.
---

Trykatch treats data ownership as executable module metadata. A module that persists data must declare every relation it owns as `organization`, `platform`, `global`, or `infrastructure`. Invalid or incomplete declarations fail before the application is released.

## Organization-owned data

An organization-owned entity must:

- implement the organization-ownership contract;
- contain a required `OrganizationId`;
- use an organization-first database index;
- receive the host-provided EF Core query filter;
- declare its PostgreSQL relation and isolation policy; and
- enable and force RLS with matching `USING` and `WITH CHECK` predicates.

The EF Core filter protects normal application queries from accidental omissions. PostgreSQL RLS is the independent database boundary and also applies to raw SQL.

## Validation and release gates

At startup, Trykatch validates the composed EF Core model. After migrations, the migrator inspects the live PostgreSQL catalog and rejects undeclared tables, missing policies, unsafe grants, unexpected functions, object ownership by runtime roles, or any role capable of bypassing RLS.

The generated-application qualification uses PostgreSQL Testcontainers to discover declared organization tables and prove:

- no organization context means no access;
- one organization cannot read or write another organization's rows;
- cross-organization relationships are rejected; and
- EF Core filtering and PostgreSQL RLS remain aligned.

## Separate runtime roles

Production uses distinct PostgreSQL identities for organization requests, platform administration, identity storage, and outbox processing. They cannot be reused interchangeably, own schema objects, inherit privileged roles, or receive `BYPASSRLS`. Schema changes use a separate migrator identity.

Modules receive a narrow organization-data interface instead of a host `DbContext`. This keeps platform, identity, placement, and migration-owner access outside the module boundary.

## Shared and dedicated placement

Shared PostgreSQL is the default. It combines organization identifiers, EF Core filters, forced RLS, restricted runtime roles, and automated cross-organization tests.

The control plane can record an organization as `Shared` or `Dedicated`, but dedicated placement fails closed until the deployment supplies a provisioner. A provisioner must create the database, retain only a secret reference, run migrations and isolation inspection, and return verified readiness before traffic is routed to it. The template does not pretend that a dedicated database exists when no provider has been configured.

## Package trust

Package modules execute as trusted application code; they are not sandboxes. Installation and upgrades therefore verify an allowlisted publisher, NuGet signer, pinned artifact hashes, signed SLSA provenance, an SPDX SBOM, builder identity, and a signed vulnerability result before changing the workspace. A failed installation restores the previous catalog, registries, project references, and lockfiles.

Workspace modules remain source-reviewed and are registered locally. The included Documents module is the independently packaged full-stack proof: it contributes backend behavior, React UI, permissions, migrations, forced RLS, audit/outbox events, archive and restore behavior, and assistant-tool declarations exclusively through stable module contracts.

:::caution
A production module publisher still needs organization-controlled signing keys, certificates, provenance generation, SBOM generation, and vulnerability attestation. Trykatch validates that evidence but never invents trust credentials.
:::
