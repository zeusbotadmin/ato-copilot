# Implementation Plan: CSP-Inherited Capability Lifecycle (Vetting + Reparent)

**Branch**: `050-csp-capability-lifecycle` | **Date**: 2026-05-22 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/050-csp-capability-lifecycle/spec.md`

## Summary

Close the three remaining defects in the CSP-inherited capability surface
introduced by Feature 048 / US9: (1) the manual-add path bypasses the same
vetting gate the AI mapping pipeline passes through, (2) a capability attached
to the wrong parent component during import cannot be moved without losing
history, and (3) the "Remap capabilities" action is one click away from a
destructive AI re-run with no confirmation. The implementation adds one new
audit-trail entity (`CapabilityHistoryEvent`) and one EF Core migration,
extends `CspInheritedComponentService` with a tenant-scoped reparent operation
and a manual-add default that lands new rows as `NeedsReview` unless the
caller opts in to `markMappedImmediately = true`, exposes both via two new
MCP endpoints (`POST .../capabilities/{id}/move` and `GET .../capabilities/{id}/history`)
that honor the existing `If-Match` / `412 ROW_VERSION_MISMATCH` envelope, and
re-skins the component / capability drawers to surface the History section,
"Move to another component…" action, "Mark as mapped immediately" checkbox,
"Advanced" disclosure for Remap, and the picker review-count chip. No new
agents and no new BaseTool implementations — the existing CSP HTTP endpoints
extend with two methods.

## Technical Context

**Language/Version**: C# 13 / .NET 9.0 (backend); TypeScript 5.7 / React 19 (Dashboard)
**Primary Dependencies**: ASP.NET Core 9.0 (Minimal APIs), EF Core 9.0
(`Microsoft.EntityFrameworkCore` + `.SqlServer` + `.Sqlite`), Serilog 4.2,
xUnit 2.9.3 + FluentAssertions 7.0 + Moq 4.20 (tests); React 19, React
Router 7, Axios 1.7, Tailwind CSS 3, Vite 6, `@testing-library/react` (dashboard tests)
**Storage**: EF Core dual-provider — SQLite (dev) / SQL Server (prod) via the
existing `AtoCopilotContext`. **One new table** (`CapabilityHistoryEvents`),
**one new EF Core migration**. No schema changes to the existing
`CspInheritedCapability` table; the reparent operation reuses the existing
`CspInheritedComponentId` FK column. No breaking changes.
**Testing**: xUnit + FluentAssertions + Moq for unit tests
(`Ato.Copilot.Tests.Unit/Tenancy/`); `WebApplicationFactory<Program>` for
endpoint integration tests (`Ato.Copilot.Tests.Integration/Csp/`);
`@testing-library/react` + `vitest` for dashboard component tests; manual
verification via the existing CSP-Admin simulated context (`tenant
00000000-0000-0000-0000-000000000001`, `oid 00000000-0000-0000-0000-000000000002`).
Local TypeScript type-check parity (`npm run typecheck` in
`Ato.Copilot.Dashboard`) per Constitution § Local Type-Checking Parity.
**Target Platform**: Linux server (containerized via `docker-compose.mcp.yml`);
Chromium-class browser for Dashboard; AzureUSGovernment + AzureCloud regions.
**Project Type**: Existing multi-project monorepo — no new top-level project.
**Performance Goals**:

- Reparent endpoint completes within the existing CSP capability PATCH
  response-time budget (≤ 1 s p95) — single transaction, two row updates plus
  one history insert.
- History GET returns within 200 ms p95 for capabilities with ≤ 100 events;
  the typical capability is expected to have < 20 events over its lifetime.
- No background workers, no fan-out queues — every state change writes its
  history row synchronously in the same transaction (per the design
  conversation).

**Constraints**:

- **§VI TDD non-negotiable** — every new code path opens with a failing unit
  test using AAA markers.
- **Tenant Isolation non-negotiable** — every read and write filters by
  `TenantId` (FR-013). The reparent target-component lookup MUST verify the
  target component belongs to the same tenant as the source capability.
- **Optimistic concurrency preserved** — reparent honors `If-Match` on the
  capability's current `rowVersion` and returns `412 ROW_VERSION_MISMATCH`
  on stale ETag (FR-012, FR-002).
- **`NeedsReview` reset on reparent is intentional, not a bug** — the move
  itself is a state change that must be re-confirmed even when the underlying
  mapping was previously approved (per the design conversation, US2
  acceptance, FR-002).
- **Manual-add default is `NeedsReview`** — `markMappedImmediately = true` is
  the explicit override; the absence of the field MUST NOT be coerced to
  `true` by serializer defaults (FR-001).
- **History rows are immutable** — `CapabilityHistoryEvent` has no edit or
  delete endpoint. The audit trail is append-only.
- **History retention** — `CapabilityHistoryEvent` rows are NOT removed when
  the parent capability is archived; archive is a state change (`Archived`
  event), not a delete. The only operation that cascades history rows away
  is **tenant offboarding** (Feature 048 tenant-removal flow). No
  application endpoint exposes a capability hard-delete (FR-015).
- **History endpoint pagination contract** — `GET .../history` follows the
  same `page` / `pageSize` shape as every other CSP list endpoint:
  defaults `page=1`, `pageSize=50`, clamped 1–200, response
  `{ items, page, pageSize, total }`, `items` ordered by `OccurredAt DESC`,
  empty history returns HTTP 200 with `items: []` (FR-014).
- **Move-dialog picker fetch strategy** — `MoveCapabilityDialog` fetches
  candidates with a single call to the existing `ListCspInheritedComponents`
  endpoint at `page=1, pageSize=200` (the endpoint's max), filters
  server-side to non-archived rows in the caller's tenant, and applies an
  inline client-side filter-as-you-type textbox. If a tenant ever exceeds
  200 candidates the picker surfaces a visible "showing first 200; refine
  your component catalog" notice rather than silently truncating. The
  existing `ListCspInheritedComponents` endpoint MUST NOT gain a new
  server-side search parameter as part of this feature (FR-003 extended).
- **Move action disabled-state** — when the tenant has zero other
  non-archived CSP-inherited components, the "Move to another component…"
  affordance on `CapabilityDetailDrawer` is rendered disabled with an
  inline tooltip; the dialog is not openable. State recomputes on next
  drawer render (FR-003 extended).
- **Remap audit semantics** — a Remap run writes **one**
  `CapabilityHistoryEvent` per **changed** capability (created / edited /
  archived as a result of the run) and **zero** events for preserved
  rows (`mappedBy = User` and AI-unchanged AI rows). All events from
  the same run share a `remapRunId` correlator in `metadataJson`. The
  `actorOid` is the CSP-Admin who clicked Continue in the Advanced
  disclosure confirm dialog (FR-016).
- **Self-review allowed** — no UI or backend constraint enforces that the
  reviewer be a different identity from the creator (FR-010).

**Scale/Scope**:

- **Tenants**: 1 to thousands; bounded by the same dual-provider EF Core
  surface as the rest of Feature 048.
- **Capabilities per component**: typical ≤ 50, expected upper bound ≤ 200.
- **History events per capability**: typical ≤ 20 over its lifetime.
- **Surfaces touched**: dashboard (`ComponentDetailDrawer.tsx`,
  `CapabilityDetailDrawer.tsx`, `api.ts`, possibly one new
  `MoveCapabilityDialog.tsx`), backend (`CspInheritedComponentService.cs`,
  `CspInheritedComponentEndpoints.cs`, `AtoCopilotContext.cs`, new
  `CapabilityHistoryEvent.cs` + `ICapabilityHistoryService.cs` +
  `CapabilityHistoryService.cs`).
- **Code surfaces NOT touched**: M365 Teams bot, VS Code chat participant,
  Web Chat React client, MCP tool envelope shapes, agent tools, organization
  role assignment surface (Feature 049), POA&M / SSP / evidence repositories.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| # | Principle / Standard | Verdict | Evidence in spec / plan |
|---|---|---|---|
| I  | Documentation as Source of Truth | PASS | Spec at [spec.md](./spec.md); this plan + [research.md](./research.md) + [data-model.md](./data-model.md) + [contracts/](./contracts) + [quickstart.md](./quickstart.md) cover every decision. |
| II | Simplicity | PASS | One new entity (`CapabilityHistoryEvent`) — the minimum required by FR-004. One new EF Core migration. No new agents, no new BaseTool implementations, no new queue infrastructure. Reparent is a method on the existing `ICspInheritedComponentService`, not a new service. |
| III | YAGNI | PASS | Every component is driven by an FR with at least one acceptance scenario. No speculative event sourcing, no global review inbox (NG-1), no 4-eyes enforcement (NG-2), no parent-type compatibility validation (NG-3), no bulk reparent (NG-4), no cross-profile reparent (NG-5). |
| IV | Single Responsibility Principle | PASS | `ICapabilityHistoryService` writes / reads history rows only. `CspInheritedComponentService.ReparentCapabilityAsync` performs the atomic move + history write only. The dashboard `MoveCapabilityDialog.tsx` (if extracted) picks a target component only; existing drawers gain dedicated sections (History, Advanced) rather than overloading existing ones. |
| V | BaseAgent / BaseTool Architecture | N/A — no new agents or tools | This feature extends HTTP endpoints in the existing CSP capability surface. No agent reasoning. No MCP tool envelope. |
| VI | Test-Driven Development (NON-NEGOTIABLE) | PASS — enforced by tasks | Every new method opens with a failing test using AAA markers. Concrete tests enumerated in [research.md R6](./research.md#r6-test-strategy) and in the file layout below. |
| VII | Observability & Structured Logging | PASS | Structured Serilog log per state-changing operation (reparent, mark-mapped-immediately, history write) with `tenantId`, `capabilityId`, `actorOid`, `eventType` fields. No PII in log labels. No new metrics counters required (the existing CSP-endpoint metrics cover request volume). |
| —  | Azure Government & Compliance | PASS | No new Azure resources. No change to data residency. No change to identity model. Inherits tenant isolation from Feature 048. |
| —  | Security: Zero-Trust + Tenant Isolation | PASS | FR-013 reaffirms tenant filtering on every read. The reparent endpoint requires CSP-Admin role authorization server-side (same gate as existing CSP capability PATCH). The history-read endpoint requires the same CSP-Admin gate (no auditor-only read mode in this feature). |
| —  | Security: Secrets / Transport | PASS — no change | No new secrets. No new endpoints exposed outside the existing TLS-terminated cluster. |
| —  | Local Type-Checking Parity (NON-NEGOTIABLE) | PASS | All dashboard TS changes pass `npm run typecheck` in `Ato.Copilot.Dashboard` before commit. |
| —  | DevOps: CI/CD Zero Warnings | PASS | Standard gate. No expected warnings. |
| —  | DevOps: GitHub Issue Discipline (NON-NEGOTIABLE) | TODO at `/speckit.tasks` time | Feature 050 parent issue + 5 User Story sub-issues (US1–US5) MUST be created before tasks begin. |
| —  | Complexity Justification | NOT APPLICABLE | No Simplicity (§II) or YAGNI (§III) deviation. Complexity Tracking table left empty. |

**Gate result**: **PASS** — proceed to Phase 0.

### Post-Design Re-Check (after Phase 1)

*Re-evaluated 2026-05-22 against [research.md](./research.md),
[data-model.md](./data-model.md), [contracts/http-api.md](./contracts/http-api.md),
[contracts/internal-services.md](./contracts/internal-services.md),
[contracts/frontend-types.md](./contracts/frontend-types.md), and
[quickstart.md](./quickstart.md).*

| # | Principle / Standard | Re-Verdict | Post-design evidence |
|---|---|---|---|
| I  | Documentation as Source of Truth | PASS — unchanged | All six artifacts authored. Cross-reference matrices in data-model.md § 9, http-api.md § 5, internal-services.md § 5, frontend-types.md § 8 map every FR to a concrete artifact section. |
| II | Simplicity | PASS — unchanged | Final entity count = 1 (`CapabilityHistoryEvent`). Final new service interface count = 1 (`ICapabilityHistoryService` with exactly two methods, `AppendAsync` + `ListAsync`). One new method on `ICspInheritedComponentService` (`ReparentCapabilityAsync`). One signature extension (`AddCapabilityAsync` gains `markMappedImmediately` default-false param). No new agents, tools, queues, outbox tables, or storage providers. |
| III | YAGNI | PASS — unchanged | Two optional indexes documented in data-model.md § 1.8 are **NOT** shipped in the migration (only the leading `(TenantId, CapabilityId, OccurredAt DESC)` index ships). A separate `RemapRuns` table was considered and rejected — `remapRunId` GUID in `metadataJson` is sufficient (data-model.md § 8). |
| IV | Single Responsibility Principle | PASS — unchanged | `CapabilityHistoryService.AppendAsync` does not call `SaveChangesAsync` — the calling service owns the transaction. This separates "decide to audit" from "decide to commit". `MoveCapabilityDialog` is its own component (R4) rather than inlined into the drawer. |
| V | BaseAgent / BaseTool Architecture | N/A — unchanged | Re-verified: no new agents or MCP tools introduced by any Phase 1 artifact. |
| VI | Test-Driven Development (NON-NEGOTIABLE) | PASS — unchanged | research.md § R5 enumerates the 4-layer pyramid (C# unit, C# integration, TS component, manual). Each new method has at least one failing test enumerated. Per-artifact test counts: data-model.md § 1.6 (immutability test), internal-services.md § 4 (transaction-rollback test), frontend-types.md § 7 (five `.test.tsx` files). |
| VII | Observability & Structured Logging | PASS — unchanged | internal-services.md § 1.4 retains structured `ILogger<CapabilityHistoryService>`. No PII/CUI in log fields (only tenant/capability/actor GUIDs + event-type enum). |
| —  | Azure Government & Compliance | PASS — unchanged | Verified — no new Azure resources, no new data residency considerations across all six artifacts. |
| —  | Security: Zero-Trust + Tenant Isolation | PASS — unchanged | http-api.md § 1, § 2, § 3 each open with the CSP-Admin gate and tenant scoping. data-model.md § 1.8 confirms the composite index leads with `TenantId`. internal-services.md § 1.3 pins the FR-013 invariant. Cross-tenant target returns 404 (not 403) per spec edge-case — existence-leak guard documented in http-api.md § 2.5. |
| —  | Security: Secrets / Transport | PASS — unchanged | No new secrets introduced by any artifact. |
| —  | Local Type-Checking Parity (NON-NEGOTIABLE) | PASS — unchanged | frontend-types.md § 1.1 and § 1.2 produce well-typed TS wire models. quickstart.md § 2 mandates `npm run typecheck` on dashboard, VS Code, and M365 projects. |
| —  | DevOps: CI/CD Zero Warnings | PASS — unchanged | No expected warnings from the proposed code (one new entity, one migration, one service, one new + one extended endpoint method, one new dialog component, three component edits). |
| —  | DevOps: GitHub Issue Discipline (NON-NEGOTIABLE) | DEFERRED to `/speckit.tasks` | Status unchanged — parent issue + 5 User Story sub-issues MUST be created before tasks begin. Re-verified that artifact set does not introduce any additional Feature-level work that would need its own issue. |
| —  | Complexity Justification | NOT APPLICABLE — unchanged | Complexity Tracking table remains empty. Zero deviations from §II / §III across all Phase 1 artifacts. |

**Post-Design Gate result**: **PASS** — no new violations introduced by
Phase 0 / Phase 1 artifacts. Proceed to `/speckit.tasks`.

### Post-Implementation Re-Check (after Phase 8)

*Re-evaluated 2026-05-28 against the merged Feature 050 implementation
(Phases 2–7) and the [tasks.md](./tasks.md) deviation notes.*

| # | Principle / Standard | Re-Verdict | Post-implementation evidence |
|---|---|---|---|
| I  | Documentation as Source of Truth | PASS | All deviations from the original plan are documented inline in [tasks.md](./tasks.md) per task (T009 EnsureSchemaAdditions module vs `dotnet ef`, T038 RemapAsync diff-by-name refactor, T039 in-memory ordering for SQLite, etc.). |
| II | Simplicity | PASS | Final entity count = 1 (`CapabilityHistoryEvent`). One new service (`ICapabilityHistoryService`, 2 methods). One new method on `ICspInheritedComponentService` (`ReparentCapabilityAsync`). One signature extension (`AddCapabilityAsync`). No new agents, tools, queues, or storage providers. |
| III | YAGNI | PASS | Only the leading `(TenantId, CapabilityId, OccurredAt DESC)` index ships. No separate `RemapRuns` table — `remapRunId` GUID lives in `metadataJson`. |
| IV | SRP | PASS | `CapabilityHistoryService.AppendAsync` does not call `SaveChangesAsync` (verified by unit test). `MoveCapabilityDialog.tsx`, `HistoryPanel`, `RemapConfirmDialog` are each their own component / sub-component. |
| V | BaseAgent / BaseTool | N/A | No new agents or MCP tools. |
| VI | Test-Driven Development (NON-NEGOTIABLE) | PASS | Every new method opened with a failing test (RED confirmed each phase before GREEN). Final counts: 19 new unit tests (Phases 2–5), 16 new integration tests (Phases 3–5), 24 new frontend tests (Phases 3–7), all with AAA markers. |
| VII | Observability & Structured Logging | PASS | Structured Serilog logs for every state-changing operation on `CspInheritedComponentService` carry `tenantId`, `capabilityId`, `actorOid`, `eventType`. No PII / CUI in log fields. |
| —  | Azure Government & Compliance | PASS | No new Azure resources, no new data residency considerations. |
| —  | Security: Zero-Trust + Tenant Isolation | PASS | All endpoints gate on `ITenantContext.IsCspAdmin`. `CapabilityHistoryEvent` is `[TenantScoped]` → automatic query filter applied by `AtoCopilotContext.OnModelCreating`. Cross-tenant target → 404 (existence-leak guard verified in integration tests). |
| —  | Security: Secrets / Transport | PASS | No new secrets. |
| —  | Local Type-Checking Parity (NON-NEGOTIABLE) | PASS | `npm run typecheck` / `npm run compile` / `npm run build` clean on all three TS projects (Dashboard, extensions/vscode, extensions/m365) as of 2026-05-28. |
| —  | DevOps: CI/CD Zero Warnings | PASS | `dotnet build Ato.Copilot.sln` → 8 warnings, all pre-existing `NU1902` package-vulnerability advisories (MailKit, Microsoft.Identity.Web, OpenTelemetry); 0 new warnings from Feature 050 code. |
| —  | DevOps: GitHub Issue Discipline (NON-NEGOTIABLE) | **STILL DEFERRED** | T001 deferred per user-instructed approval gate. Parent Feature 050 issue + 5 User Story sub-issues remain to be created before merge. |
| —  | Complexity Justification | N/A | Complexity Tracking table remains empty. No deviations from §II / §III. |

**Post-Implementation Gate result**: **PASS** — pending T001
(GitHub issue discipline) which is gated on explicit user approval.

## Project Structure

### Documentation (this feature)

```text
specs/050-csp-capability-lifecycle/
├── plan.md                  # This file (/speckit.plan output)
├── spec.md                  # Feature specification (already exists)
├── research.md              # Phase 0 — entity-design decisions, endpoint shape, history-write timing
├── data-model.md            # Phase 1 — CapabilityHistoryEvent schema + indexes + migration sketch
├── contracts/
│   ├── http-api.md          # Phase 1 — POST .../move + GET .../history + PATCH manual-add markMappedImmediately
│   ├── internal-services.md # Phase 1 — ICapabilityHistoryService, ICspInheritedComponentService.ReparentCapabilityAsync
│   └── frontend-types.md    # Phase 1 — TS types for History section, Move dialog, Advanced disclosure
├── quickstart.md            # Phase 1 — local verification recipe
├── checklists/
│   └── requirements.md      # (created by /speckit.checklist if requested)
└── tasks.md                 # Phase 2 output (/speckit.tasks — NOT created by /speckit.plan)
```

### Source Code (repository root)

```text
src/
├── Ato.Copilot.Core/
│   ├── Models/Tenancy/
│   │   ├── CapabilityHistoryEvent.cs                  # NEW: audit-trail entity (FR-004)
│   │   └── CapabilityHistoryEventType.cs              # NEW: enum Created / Edited / Reviewed / Moved / Archived / Unarchived
│   ├── Interfaces/Tenancy/
│   │   ├── ICapabilityHistoryService.cs               # NEW: tenant-scoped writes / reads
│   │   └── ICspInheritedComponentService.cs           # MODIFY: add ReparentCapabilityAsync + extend CreateCapabilityAsync with markMappedImmediately
│   ├── Services/Tenancy/
│   │   ├── CapabilityHistoryService.cs                # NEW: append-only writer + reverse-chronological reader
│   │   └── CspInheritedComponentService.cs            # MODIFY: ReparentCapabilityAsync (atomic move + history write); default NeedsReview on manual-add; history rows on every state change
│   └── Data/Context/
│       └── AtoCopilotContext.cs                       # MODIFY: DbSet<CapabilityHistoryEvent>; OnModelCreating indexes (tenantId+capabilityId+occurredAt desc)
├── Ato.Copilot.Core/Data/Migrations/
│   └── <timestamp>_AddCapabilityHistoryEvents.cs      # NEW: EF Core migration — table + indexes
├── Ato.Copilot.Mcp/
│   └── Endpoints/Csp/
│       └── CspInheritedComponentEndpoints.cs          # MODIFY: POST .../capabilities/{id}/move + GET .../capabilities/{id}/history; extend POST create-capability with markMappedImmediately
└── Ato.Copilot.Dashboard/
    └── src/
        ├── features/csp-inherited-components/
        │   ├── api.ts                                 # MODIFY: reparentCapability + getCapabilityHistory + markMappedImmediately request field
        │   ├── ComponentDetailDrawer.tsx              # MODIFY: Advanced disclosure for Remap (FR-006/7/8); Mark-mapped-immediately checkbox in + Add form (FR-001); picker review-count chip (FR-009)
        │   ├── CapabilityDetailDrawer.tsx             # MODIFY: "Move to another component…" action (FR-003); History section (FR-005)
        │   └── MoveCapabilityDialog.tsx               # NEW: target-component picker (filtered to non-archived, same tenant, excluding current parent)
        └── __tests__/features/csp-inherited-components/
            ├── ComponentDetailDrawer.advanced.test.tsx     # NEW: Advanced disclosure + confirm dialog
            ├── ComponentDetailDrawer.markMapped.test.tsx   # NEW: + Add form default NeedsReview + override
            ├── CapabilityDetailDrawer.move.test.tsx        # NEW: move-to-component flow
            └── CapabilityDetailDrawer.history.test.tsx     # NEW: history section rendering

tests/
├── Ato.Copilot.Tests.Unit/Tenancy/
│   ├── CapabilityHistoryServiceTests.cs               # NEW: append-only invariants, tenant filter, chronological order
│   ├── CspInheritedCapabilityReparentTests.cs         # NEW: status reset to NeedsReview, mappedBy preservation, rowVersion bump, history row written
│   └── CspInheritedCapabilityDefaultVettingStateTests.cs  # NEW: manual-add lands as NeedsReview unless markMappedImmediately = true
└── Ato.Copilot.Tests.Integration/Csp/
    ├── ReparentEndpointTests.cs                       # NEW: 412 on stale ETag, 404 on archived target, 403 cross-tenant target, 200 on success
    └── CapabilityHistoryEndpointTests.cs              # NEW: tenant isolation, reverse-chronological order, empty-history shape
```

**Structure Decision**: Existing multi-project monorepo. All new code lives
under existing project folders matching their layer (Core / Mcp / Dashboard).
The only structural addition is the `Data/Migrations/` directory if it does
not already exist for `AtoCopilotContext` (verify in Phase 0 — repo
convention has been mixed between `EnsureCreatedAsync` and explicit
migrations; user explicitly approved one new migration for this feature).

## Phasing

| Phase | Output | Purpose |
|---|---|---|
| **0** — Research | [research.md](./research.md) | Decide history-write timing (sync vs. async), confirm migration vs. `EnsureCreated` convention for this DbContext, settle endpoint shape (POST .../move vs. PATCH .../parent), confirm `If-Match` header re-use, settle whether `MoveCapabilityDialog.tsx` is a standalone component or inline in the existing drawer. |
| **1** — Design | [data-model.md](./data-model.md), [contracts/](./contracts), [quickstart.md](./quickstart.md) | Lock the `CapabilityHistoryEvent` table shape + indexes; lock the HTTP contracts (request / response / error envelopes); lock TS contracts for the new dashboard surfaces. |
| **2** — Tasks | [tasks.md](./tasks.md) (via `/speckit.tasks`) | Dependency-ordered TDD task list — failing tests for each FR before any production code. |
| **3** — Implementation | code under `src/` + `tests/` | TDD cycle per task. |

## Implementation Strategy (Phase 3 preview)

Dependency-ordered, mirrored in [tasks.md](./tasks.md) once `/speckit.tasks`
runs:

1. **Backend foundation (US1, US2, US3 — all P1)**
   1. Failing unit test for `CapabilityHistoryService.AppendAsync` (tenant
      filter, immutability invariants).
   2. `CapabilityHistoryEvent.cs` + `CapabilityHistoryEventType.cs` entities.
   3. `ICapabilityHistoryService.cs` + `CapabilityHistoryService.cs`.
   4. `AtoCopilotContext` DbSet + `OnModelCreating` (composite index on
      `(TenantId, CapabilityId, OccurredAt DESC)`).
   5. EF Core migration `AddCapabilityHistoryEvents`.
   6. Failing test for `CspInheritedComponentService.ReparentCapabilityAsync`
      (status reset to NeedsReview, `mappedBy` preservation, `rowVersion`
      bump, history row written).
   7. Implement `ReparentCapabilityAsync` in single transaction:
      `BEGIN → UPDATE capability SET cspInheritedComponentId=?,
      status=NeedsReview, rowVersion=newVersion WHERE id=? AND
      rowVersion=oldVersion → INSERT history → COMMIT`.
   8. Failing test for manual-add default vetting state (FR-001).
   9. Extend `CreateCapabilityAsync` signature with `markMappedImmediately`
      parameter; default to `false`.

2. **MCP endpoints (still US2 / US3)**
   1. Failing integration test for `POST /csp/inherited-components/{id}/capabilities/{capabilityId}/move`
      (412 on stale ETag, 404 on archived target, 403 cross-tenant, 200 success).
   2. Endpoint implementation in `CspInheritedComponentEndpoints.cs`.
   3. Failing integration test for `GET /csp/inherited-components/{id}/capabilities/{capabilityId}/history`.
   4. Endpoint implementation (tenant-scoped, reverse-chronological).
   5. Extend `POST .../capabilities` create-capability request DTO with
      optional `markMappedImmediately`; defaults to `false`.

3. **Dashboard surfaces (US1, US2, US3, US4, US5)**
   1. Failing TS test for `MoveCapabilityDialog` target list filtering.
   2. `MoveCapabilityDialog.tsx` (filters out archived + current parent).
   3. Failing TS test for `CapabilityDetailDrawer` "Move to another component…" action.
   4. Wire move action into `CapabilityDetailDrawer.tsx` (calls
      `reparentCapability(id, targetComponentId, ifMatch)`); on 412 surface
      the existing `ROW_VERSION_MISMATCH` error envelope and refresh.
   5. Failing TS test for History section rendering.
   6. Add History section to `CapabilityDetailDrawer.tsx` (chronological
      list with timestamp + actor + summary).
   7. Failing TS test for ComponentDetailDrawer Advanced disclosure (US4).
   8. Move Remap button into Advanced disclosure; add pre-flight copy;
      wire confirm dialog with Cancel focused by default.
   9. Failing TS test for "Mark as mapped immediately" checkbox (US1).
   10. Add checkbox to `+ Add capability` form; wire to
       `markMappedImmediately` field on create call.
   11. Failing TS test for picker review-count chip (US5).
   12. Update Linked Capabilities section header to show
       `(N awaiting review)` in amber when N > 0.

4. **Cross-cutting**
   1. Verify `npm run typecheck` clean in `Ato.Copilot.Dashboard`.
   2. Verify `dotnet build Ato.Copilot.sln` clean.
   3. Verify `dotnet test Ato.Copilot.sln` clean.
   4. Manual verification per [quickstart.md](./quickstart.md).
   5. Rebuild dashboard container; visual verification at simulated
      CSP-Admin context.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

*(Empty — no §II / §III deviation.)*

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|

## Open Risks

| Risk | Mitigation |
|---|---|
| EF Core migration may conflict if the repo convention for `AtoCopilotContext` is `EnsureCreatedAsync` rather than migrations. | **Resolved in Phase 0 research (research.md R2)**: `DatabaseInitializationService` already runs `EnsureCreatedAsync` for SQLite (dev) and `MigrateAsync` for SQL Server (prod). Adding the entity to `OnModelCreating` covers SQLite; the new EF Core migration covers SQL Server. Both paths land the same table shape. An `EnsureSchemaAdditions/` module is optional for dev hot-upgrades but not strictly required. |
| History writes inside the same transaction as the state change add latency. | Bounded — each operation writes exactly one history row, indexed for fast insert. Worst-case overhead is one extra `INSERT` per transaction. Async writing was explicitly rejected (per the design conversation) so that a crash between state change and history write cannot leave the audit trail incomplete. |
| The reparent UI relies on listing all non-archived components in the tenant — could grow unbounded. | **Resolved by Clarification Q2 (2026-05-22)**: the `MoveCapabilityDialog` fetches a single page of size 200 from the existing `ListCspInheritedComponents` endpoint and filters client-side; tenants exceeding 200 candidates see a visible "showing first 200" notice. No new server-side search parameter is introduced. |
| The TS interface drift flagged in commit 3 (`CspInheritedCapability.componentId` instead of wire's `cspInheritedComponentId`) will be re-touched by this feature. | Resolved as part of this feature: the `MoveCapabilityDialog` and the `api.reparentCapability` call surface this field, so the type fix lands naturally alongside the new code. Tracked in tasks.md as a discrete cleanup step. |
