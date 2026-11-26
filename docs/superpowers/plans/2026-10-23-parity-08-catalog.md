# TryKatch Catalog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans task-by-task, preserving the project ownership and review workflow.

**Goal:** Deliver a useful product catalog reference with brands, categories, exact prices, stock and multiple images.
**Architecture:** Optional organization-owned five-layer module with a required Documents attachment dependency. Domain invariants and transactional conflict handling extend a scaffold; generated CRUD alone is insufficient.
**Tech Stack:** Existing stack, F1 attachment contract from phase 3, S3 idempotency and E3 integration events.
**Spec:** [Design](../specs/2026-10-23-feature-parity-design.md); [roadmap](2026-10-23-feature-parity-plan.md).

## Global Constraints

Apply all roadmap constraints. Paths below are relative to `templates/trykatch/`; new names are proposals. Catalog requires Documents and configured storage. Product images remain authenticated/owner-authorized; anonymous/public CDN URLs belong to deferred D7. No ordering, stock reservation, purchasing or currency exchange system is included.

## Review Focus

- Category moves cannot create cycles, including concurrent moves.
- SKU uniqueness is organization-scoped and uses a documented normalization rule.
- Concurrent stock updates preserve the selected reference inventory rule and raise a conflict on stale version.
- Product image ordering/thumbnail rules remain valid under concurrent attach/delete.
- Archive/restore and searches never cross organization or restore invalid relationships silently.

## C1: Add catalog domain, ownership and query/write APIs

**Files:** Create `src/Modules/Catalog/trykatch.module.json` and five Catalog projects. Proposed Domain `Brand.cs`, `Category.cs`, `Product.cs`, `ProductImage.cs`, `Money.cs`; Application `BrandUseCases.cs`, `CategoryUseCases.cs`, `ProductUseCases.cs`; Infrastructure `CatalogModule.cs`, `CatalogModelContributor.cs`, immutable migrations; Presentation `CatalogEndpoints.cs`; module-local unit/architecture tests; host `CatalogIntegrationTests.cs`.

**Interfaces:** Stable operation IDs prefixed `Catalog_Brands_`, `Catalog_Categories_`, `Catalog_Products_`; permission IDs `catalog.read`, sensitive `catalog.manage`, `catalog.stock`. Read/query DTOs include explicit optimistic version, exact price strings/currency and stable attachment references. IntegrationEvents contracts `catalog.product-created.v1`, `catalog.price-changed.v1`, `catalog.stock-adjusted.v1` contain safe organization-scoped snapshots.

- [ ] Freeze the reference fields, length/normalization constraints and currency/stock behavior in `docs/specs/catalog.md`. Proposed reference scope uses nonnegative on-hand stock and nonnegative exact unit price, with no backorder/reservation semantics; mark this as an engineering choice for review, not an existing product policy.
- [ ] Add `SkuUniquenessIsPerOrganization`, `CategoryCannotBecomeOwnAncestor`, `ForeignOrganizationBrandCategoryAndImageAreRejected`, exact-money round-trip and forbidden API mutation tests. Include raw SQL/runtime-role isolation assertions.
- [ ] Start from an organization scaffold only where helpful; implement real brand/category/product relationships, tenant-first keys/indexes, bounded search/pagination and lifecycle filters. Declare all module tables and relationships.
- [ ] Implement use-case permission checks in addition to endpoint metadata, expected-version conflict handling, atomic audit/outbox and S3 for retry-prone create/actions. Domain events become explicit safe IntegrationEvents; no private module service references.
- [ ] Verify Catalog unit/architecture tests, `CatalogIntegrationTests`, affected generic organization/isolation/activation tests and common exit.

## C2: Add category/stock concurrency and multi-image behavior

**Files:** Catalog Domain `CategoryHierarchy.cs`, `StockAdjustment.cs`; Application `MoveCategory.cs`, `AdjustStock.cs`, `ProductImageUseCases.cs`; Infrastructure `ProductAttachmentOwnerPolicy.cs` and transactional query/lock implementation; module unit `CatalogConcurrencyTests.cs`, `ProductImageTests.cs`; host `CatalogConcurrencyIntegrationTests.cs`.

**Interfaces:** `Catalog_MoveCategory`, `Catalog_AdjustStock`, `Catalog_AttachProductImage`, `Catalog_ReorderProductImages`, `Catalog_RemoveProductImage`; commands carry expected aggregate version and logical mutation identity. Documents owner type is `catalog.product`, registered through the neutral contract.

- [ ] Test two opposing category moves, simultaneous SKU creation, two adjustments against the same stock/version and replay of one adjustment. Assert the category graph remains acyclic and one stale mutation returns the existing conflict shape rather than losing a write.
- [ ] Enforce hierarchy updates and stock adjustments with a documented transaction/locking strategy under the host organization transaction. Keep immutable adjustment/audit provenance; do not implement arithmetic in React.
- [ ] Test ordered images, one thumbnail when images exist, removing current thumbnail, reorder conflicts and a failed attach after object upload. Authorize the product owner on every Documents route; use opaque references and explicit orphan reconciliation.
- [ ] Test archive/restore with archived parent/brand and attachment owner; reject or explicitly reconcile invalid references per the module spec. No automatic destructive cascade across module-owned files.
- [ ] Verify module concurrency/image tests and `CatalogConcurrencyIntegrationTests`, relevant Documents/idempotency tests and common exit.

## C3: Add full catalog UI and generated-product acceptance

**Files:** Catalog `Web/` package with `BrandsPage.tsx`, `CategoriesPage.tsx`, `ProductsPage.tsx`, `ProductDetailsPage.tsx`, tests/stories/messages and module entry; `web/apps/web/e2e/catalog.spec.ts`; module docs and API examples.

**Interfaces:** Module-owned routes under `/catalog`; named extension points for product details/actions allow future modules to contribute without private component imports. Consume generated clients and public attachment UI contributions.

- [ ] Cover filters, paging, validation, exact prices, category tree/move, stock adjustment, multi-image reorder/thumbnail, empty/error/conflict and archive/restore states in focused tests/stories. Keep decimal/Int64 values as strings at the wire boundary.
- [ ] Browser case creates a brand/category/product, attaches images, adjusts stock, searches, archives/restores and handles a conflicting second editor. Unauthorized users cannot execute hidden actions by URL/API; organization B cannot search the item.
- [ ] Verify module Web tests/stories, named browser case, generated client, typecheck/build, disabled-module composition and common exit. Record evidence in proposed `docs/specs/evidence/parity-catalog.md`.
