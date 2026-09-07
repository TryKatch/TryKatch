# 0002: Shared PostgreSQL with row-level security

- Status: Accepted
- Date: 2026-09-07

## Decision

Use one PostgreSQL database with separate schemas and transaction-local organization context. Enforce isolation in both application use cases and PostgreSQL RLS policies.

## Consequences

Database-per-organization provisioning is outside version 1. Integration tests must prove that RLS prevents cross-organization access.

