# Implementation Plan: Unified RMF Role Assignments with Org → System Inheritance

**Branch**: `049-unified-rmf-role-assignments` | **Date**: 2026-05-19 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/049-unified-rmf-role-assignments/spec.md`

## Summary

Unify the two parallel role-assignment data models (legacy `RmfRoleAssignment` from Features 002/003 and the newer `OrganizationRoleAssignment` + `SystemRoleAssignment` from Features 047/048) so that the dashboard's red "No Mission Owner Assigned" banner can be cleared by naming a person at the Org level once, with that named individual inheriting down to every system in the tenant, and per-system overrides remaining possible. No new database tables. No new agents or MCP tools. The implementation extends the existing `OrganizationRole` enum by 3 values, introduces a single read facade (`IUnifiedRoleReader`) that consolidates the existing 3 sources behind a deterministic precedence chain (override → inherited → Org-level fallback → legacy), adds a role-tiered RBAC authorization service and a DoDI 8510.01 separation-of-duties detector, wraps the legacy write-through in a single EF Core transaction, exposes a typed `AssignRoleDialog` to the dashboard banner + Roles panel + onboarding wizard, and materializes inherited rows for existing systems asynchronously via an in-process `IHostedService` worker fed by `System.Threading.Channels` with a startup reconciliation sweep. Banner clearing is **synchronous from the reader's perspective** because the reader resolves through to the Org-level row directly — the worker materializes inherited rows only for OSCAL-export consistency, audit, and per-system edit affordance.

## Technical Context

**Language/Version**: C# 13 / .NET 9.0 (backend); TypeScript 5.7 / React 19 (Dashboard); TypeScript 5 / Node.js 20 LTS (VS Code + M365 extensions — touched only for MCP-tool description string updates per FR-025)
**Primary Dependencies**: ASP.NET Core 9.0 (Minimal APIs), EF Core 9.0 (`Microsoft.EntityFrameworkCore` + `.SqlServer` + `.Sqlite`), `Microsoft.Extensions.Hosting` (`IHostedService` for FR-028 worker), `System.Threading.Channels` (in-process propagation intent queue), `System.Diagnostics.Metrics` (`Meter` + `Counter<long>` + `Histogram<double>` per Feature 029 patterns), `Microsoft.Identity.Web` 3.5+, Serilog 4.2 (structured logs per FR-028), xUnit 2.9.3 + FluentAssertions 7.0 + Moq 4.20 (tests); React 19, React Router 7, Axios 1.7, Tailwind CSS 3, Vite 6 (dashboard)
**Storage**: EF Core dual-provider — SQLite (dev) / SQL Server (prod) via the existing `AtoCopilotContext`. **No new tables. No new migrations.** Three new values appended to the existing `OrganizationRole` enum (`MissionOwner`, `AuthorizingOfficial`, `SystemOwner`); the column is stored as `nvarchar` (string-serialized enum), so no schema column change is required.
**Testing**: xUnit + FluentAssertions + Moq for unit (`Ato.Copilot.Tests.Unit`); `WebApplicationFactory<Program>` for integration (`Ato.Copilot.Tests.Integration`) including a fault-injection harness for SC-010 (DB failure between the two `Add` calls); a generator-driven RBAC matrix coverage test for SC-009 (every disallowed cell asserts HTTP 403 with `RBAC_ROLE_ASSIGN_DENIED`); `Tests.Manual/` scenarios for the dashboard banner-click flow. Local TypeScript type-check parity (`npm run typecheck` in `Ato.Copilot.Dashboard`) per Constitution § Local Type-Checking Parity.
**Target Platform**: Linux server (containerized via existing `docker-compose.mcp.yml`); Chromium-class browser for Dashboard; AzureUSGovernment + AzureCloud regions per Azure Gov & Compliance Requirements.
**Project Type**: Existing multi-project monorepo (web service + React SPA + extensions) — no new top-level project added.
**Performance Goals**: SC-011 — `org_role_propagation_duration_seconds` p99 ≤ 60s for tenants with ≤500 systems (14-day rolling window, bucketed 1–10 / 11–100 / 101–500 systems). Legacy POST atomic write completes within the existing endpoint's response-time budget (≤ 5 s per Constitution § Performance Standards). `IUnifiedRoleReader.GetSystemRolesAsync` returns within the existing dashboard profile-completeness budget (no measurable regression vs. today's single-source read — single tenant-scoped join across three tables, bounded by system count).
**Constraints**:

- **§VI TDD non-negotiable** — every new code path opens with a failing unit test using AAA markers (FR-022).
- **Tenant Isolation non-negotiable** — every read, write, and worker iteration filters by `TenantId` (FR-004, FR-024, FR-028).
- **Legacy contracts frozen** — `RmfRole` enum values, ordinals, and serialized names MUST NOT change (FR-020); MCP tool envelope shapes MUST NOT change (FR-021).
- **Legacy endpoint auth frozen during 90-day deprecation window** — `/api/dashboard/systems/{systemId}/roles` keeps "any authenticated tenant member" authorization; the role-tiered matrix (FR-027) applies only to new unified write paths.
- **Atomicity non-negotiable** — legacy write-through stages both `Add` operations on a single `AtoCopilotContext` instance and commits with one `SaveChangesAsync`; no partial state observable by any reader (FR-018, SC-010).
- **Banner clearing observability synchronous from the reader's perspective** — Org-level fallback in the reader's precedence chain (FR-029) clears the banner on the next read even before the FR-028 worker has materialized inherited rows.

**Scale/Scope**:

- **Tenants**: 1 to thousands; FR-028 worker tested up to 500 systems (SC-011 upper bound). Worker propagation degrades linearly above that bucket but still completes; no architectural ceiling.
- **Roles**: 7-role unified surface (`AuthorizingOfficial`, `Issm`, `Isso`, `Sca`, `SystemOwner`, `MissionOwner`, `Administrator`). `OrganizationRole` enum extends from 4 to 7; `RmfRole` enum stays at 6 (frozen).
- **Surfaces touched**: Dashboard (`SystemDetail.tsx`, `RoleAssignmentPanel.tsx`, new `AssignRoleDialog.tsx`, onboarding wizard Step 2), backend (`SystemProfileService`, `DashboardEndpoints`, `OrganizationRoleAssignmentService`, new `SystemRolesEndpoints`, new `OrganizationRoleFanoutWorker`), and string-only updates to MCP tool descriptions in `Ato.Copilot.Agents/Tools/**` for error-suggestion drift (FR-025, SC-007).
- **Code surfaces NOT touched**: M365 Teams bot adaptive cards; VS Code chat participant; Web Chat React client (no role surfaces); MCP tool envelope shapes; CAC OID-to-role mapping (Feature 003).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| # | Principle / Standard | Verdict | Evidence in spec / plan |
|---|---|---|---|
| I | Documentation as Source of Truth | PASS | Spec exists at [spec.md](./spec.md); this plan + [research.md](./research.md) + [data-model.md](./data-model.md) + [contracts/](./contracts) + [quickstart.md](./quickstart.md) cover every decision. |
| II | Simplicity | PASS | No new database tables. No new abstractions beyond the `IUnifiedRoleReader` facade (which **net-reduces** call-site complexity by consolidating today's three readers behind one interface) and the in-process `Channel<T>` queue (chosen over a new DB-backed job table per [research.md decision R1](./research.md#r1-fr-028-fan-out-queue-strategy)). 3 enum values added — minimum required. |
| III | YAGNI | PASS | Every component is driven by an FR with at least one acceptance scenario or SC. No speculative interfaces; the auth and SoD services are tied to FR-027 and FR-026 respectively. No multi-cloud queue abstraction, no plugin model, no premature generalization for "future RMF roles" (the enum is closed). |
| IV | Single Responsibility Principle | PASS | `IUnifiedRoleReader` resolves precedence only. `RoleAuthorizationService` evaluates the FR-027 matrix only. `SoDConflictDetector` returns warning records only. `OrganizationRoleFanoutWorker` materializes inherited rows only. Legacy `DashboardEndpoints.UpsertRole` retains its single responsibility — adding the unified-model write-through is a single sequential operation on the same `DbContext`, not a new concern. |
| V | BaseAgent / BaseTool Architecture | N/A — no new agents or tools | FR-025 only touches existing tool *description strings* to surface the unified 7-role set in error suggestions; envelope shapes preserved (FR-021). |
| VI | Test-Driven Development (NON-NEGOTIABLE) | PASS — enforced by tasks | FR-022 mandates failing-test-first with AAA markers. SC-009 mandates that every disallowed cell in the FR-027 matrix has a negative test; this drives the generator-based `RoleAuthorizationMatrixCoverageTests`. SC-010 mandates a fault-injection test for atomicity. SC-011 mandates a worker SLO test. Concrete tests enumerated in [research.md R6](./research.md#r6-test-strategy). |
| VII | Observability & Structured Logging | PASS | FR-028 mandates a structured log per propagation task with named fields. FR-018 mandates `legacy_role_endpoint_bypass_total`. FR-019 mandates `legacy_role_endpoint_call_total`. SC-011 reads `org_role_propagation_duration_seconds`. All implemented via `System.Diagnostics.Metrics.Meter` per Feature 029 patterns. No PII in metric labels (tenant + role only). |
| — | Azure Government & Compliance | PASS | No new Azure resources. No change to data residency. No change to identity model. |
| — | Security: Zero-Trust + Tenant Isolation | PASS | FR-024 reaffirms `[TenantScoped]` on every read; FR-027 enforces RBAC on every new write path; FR-018 retains legacy auth only during the 90-day window with telemetry to measure bypass impact ahead of the sunset cleanup. |
| — | Security: Secrets / Transport | PASS — no change | No new secrets, no new endpoints exposed outside the existing TLS-terminated cluster. |
| — | Local Type-Checking Parity (NON-NEGOTIABLE) | PASS | FR-023 requires `tsc --noEmit` on every touched TS file; the Dashboard's existing `npm run typecheck` script covers the changes. |
| — | DevOps: CI/CD Zero Warnings | PASS | Standard gate; no expected warnings introduced. |
| — | DevOps: GitHub Issue Discipline (NON-NEGOTIABLE) | TODO at `/speckit.tasks` time | Feature 049 parent issue + 4 User Story sub-issues (P1, P2, P3, P4) MUST be created before tasks begin. Tracked in [Implementation Phasing](#implementation-phasing) below. |
| — | Complexity Justification | NOT APPLICABLE | No Simplicity (§II) or YAGNI (§III) deviation; Complexity Tracking table left empty. |

**Gate result**: **PASS** — proceed to Phase 0.

## Project Structure

### Documentation (this feature)

```text
specs/049-unified-rmf-role-assignments/
├── plan.md                  # This file (/speckit.plan output)
├── spec.md                  # Feature specification (already exists)
├── research.md              # Phase 0 output — architecture decisions
├── data-model.md            # Phase 1 output — entity changes (enum extension only)
├── contracts/
│   ├── http-api.md          # Phase 1 — REST contracts (legacy headers + new endpoints)
│   ├── internal-services.md # Phase 1 — IUnifiedRoleReader, IRoleAuthorizationService, ISoDConflictDetector, IOrganizationRoleFanoutQueue
│   └── frontend-types.md    # Phase 1 — TS types for AssignRoleDialog + wizard Step 2
├── quickstart.md            # Phase 1 — local verification recipe
├── checklists/
│   └── requirements.md      # (already exists from /speckit.specify)
└── tasks.md                 # Phase 2 output (/speckit.tasks — NOT created by /speckit.plan)
```

### Source Code (repository root)

```text
src/
├── Ato.Copilot.Core/
│   ├── Models/Onboarding/
│   │   └── OrganizationRoleAssignment.cs              # MODIFY: extend OrganizationRole enum (+3 values, appended)
│   └── Services/Roles/                                # NEW directory
│       ├── IUnifiedRoleReader.cs                      # NEW: precedence-chain read facade
│       ├── UnifiedRoleReader.cs                       # NEW: implementation (override → inherited → org → legacy)
│       ├── IRoleAuthorizationService.cs               # NEW: FR-027 matrix evaluator
│       ├── RoleAuthorizationService.cs                # NEW: static matrix + bootstrap exception
│       ├── ISoDConflictDetector.cs                    # NEW: FR-026 DoDI 8510.01 detection
│       ├── SoDConflictDetector.cs                     # NEW: tenant-scoped person-role conflict check
│       ├── IOrganizationRoleFanoutQueue.cs            # NEW: Channel<PropagationIntent> facade
│       ├── OrganizationRoleFanoutQueue.cs             # NEW: bounded in-process queue
│       ├── ICallerEffectiveRoleResolver.cs            # NEW: resolves caller's highest-privileged RmfRole (FR-027)
│       └── CallerEffectiveRoleResolver.cs             # NEW: tenant-scoped union read across Org + System + legacy role tables
├── Ato.Copilot.Agents/
│   ├── Compliance/Services/
│   │   ├── SystemProfileService.cs                    # MODIFY: replace direct legacy read with IUnifiedRoleReader
│   │   ├── Onboarding/OrganizationRoleAssignmentService.cs  # MODIFY: wire IRoleAuthorizationService + ISoDConflictDetector + enqueue fan-out + cascade soft-remove
│   │   └── <SystemRegistrationService>.cs             # MODIFY (FR-005, FR-015): on RegisteredSystem create, stage inherited SystemRoleAssignment rows in same SaveChangesAsync
│   ├── Compliance/Services/Ssp/                       # MODIFY (SC-008): rewire OSCAL party export to use IUnifiedRoleReader; emit party type=person
│   └── Tools/                                         # MODIFY: string-only description updates per FR-025/SC-007 (drift-prevention tests catch any miss)
├── Ato.Copilot.Mcp/
│   ├── Endpoints/
│   │   ├── DashboardEndpoints.cs                      # MODIFY: write-through in single DbContext txn; Deprecation/Sunset headers; bypass+call counters
│   │   ├── SystemRolesEndpoints.cs                    # NEW: unified per-system + Org role write surface; GET /api/roles/effective; bootstrap server-side guard
│   │   └── DeprecationHeadersExtensions.cs            # NEW: endpoint filter emitting Deprecation/Sunset/Link headers (FR-019)
│   ├── Extensions/
│   │   └── AtoCopilotMcpServiceExtensions.cs          # MODIFY: register new role services + LaunchOptions binding with fail-fast (T053)
│   └── Workers/
│       └── OrganizationRoleFanoutWorker.cs            # NEW: IHostedService — drains the Channel + startup reconciliation sweep
└── Ato.Copilot.Dashboard/
    └── src/
        ├── components/
        │   ├── roles/AssignRoleDialog.tsx             # NEW: shared 7-role dialog used by banner + Roles panel + wizard
        │   └── cards/RoleAssignmentPanel.tsx          # MODIFY: 7-role set; render inherited-vs-override indicator; T040a wires it into SystemDetail
        ├── features/
        │   └── onboarding/steps/Step2RoleAssignments.tsx  # MODIFY (C1 corrected path): 3 new role rows; resume-tolerant state hydration (FR-016)
        ├── pages/
        │   └── SystemDetail.tsx                       # MODIFY: banner gets actionable "Assign Mission Owner" button → AssignRoleDialog; import + render RoleAssignmentPanel (T040a)
        ├── lib/api/
        │   └── roles.ts                               # NEW: typed Axios client for the unified endpoints + getEffectiveRole + envelope warnings
        └── types/
            └── roles.ts                               # NEW: RmfRole, RBAC_ASSIGNABLE_BY, ResolvedRoleAssignment, AssignmentResult shapes

tests/
├── Ato.Copilot.Tests.Unit/
│   └── Roles/
│       ├── UnifiedRoleReaderTests.cs                  # NEW: precedence-chain combinatorial coverage
│       ├── RoleAuthorizationServiceTests.cs           # NEW: matrix cell-by-cell (SC-009 drive)
│       ├── SoDConflictDetectorTests.cs                # NEW: 7 conflict pairs + 0 non-conflict pairs
│       └── OrganizationRoleFanoutQueueTests.cs        # NEW: bounded-queue + idempotency contract
└── Ato.Copilot.Tests.Integration/
    └── Roles/
        ├── LegacyWriteThroughAtomicityTests.cs        # NEW: drives SC-010 (DB fault between Add calls → both rollback)
        ├── RoleAuthorizationMatrixCoverageTests.cs    # NEW: generator-driven, drives SC-009
        ├── OrganizationRoleFanoutWorkerTests.cs       # NEW: enqueue → drain → idempotent re-run; startup-sweep reconciliation
        ├── TenantIsolationRolesTests.cs               # NEW: cross-tenant query returns not-found (FR-004, SC-006)
        └── DeprecationHeadersTests.cs                 # NEW: legacy endpoint emits Deprecation + Sunset on every method
```

**Structure Decision**: Existing multi-project monorepo. **No new projects.** Add a `Services/Roles/` subdirectory in `Ato.Copilot.Core` (the natural home for cross-cutting domain services) and a `Workers/` subdirectory in `Ato.Copilot.Mcp` (the host that owns the `IHostedService` registration). Dashboard changes co-locate the shared `AssignRoleDialog` at `src/components/` so banner, Roles panel, and wizard reuse it without prop-drilling.

## Implementation Phasing

The four user stories map to four PR-sized increments. Each increment ships independently and passes CI on its own. The dependency arrow points only forward — no story silently depends on a later one.

```text
US1 (P1) → US2 (P2) → US3 (P3) → US4 (P4)
   │           │           │           │
   │           │           │           └── Deprecation telemetry + write-through atomicity
   │           │           └── Wizard Step 2 + 3 new fields + resume-tolerant hydration
   │           └── Actionable banner + Roles panel renders + AssignRoleDialog
   └── Unified reader + Org enum extension + worker + RBAC + SoD (server-side only)
```

| Increment | Visible to end user | Server-side scope | Drives SCs |
|---|---|---|---|
| US1 / P1 | Banner clears on next read after Org-level Mission Owner is named via existing surface (wizard or `OrganizationRoleAssignmentService`) | `IUnifiedRoleReader`, `OrganizationRole` enum +3, `IRoleAuthorizationService`, `ISoDConflictDetector`, `OrganizationRoleFanoutWorker`, `IOrganizationRoleFanoutQueue`, `SystemProfileService` rewired to the reader | SC-001 (in part), SC-002, SC-006, SC-008, SC-009, SC-011 |
| US2 / P2 | Banner becomes actionable; per-system Roles panel renders 7-role set with inherited indicator; AssignRoleDialog ships; `GET /api/roles/effective` resolves caller's privilege for affordance hiding | Dashboard + new `SystemRolesEndpoints` (incl. `/api/roles/effective`) + new `lib/api/roles.ts` + `ICallerEffectiveRoleResolver` | SC-003, SC-005, SC-007, SC-009 |
| US3 / P3 | Wizard Step 2 captures Mission Owner / AO / System Owner; new tenants never see the banner | Wizard component + state-shape extension; backend already accepts via US1 services | SC-001 (full), SC-005 |
| US4 / P4 | Legacy callers still work; deprecation headers + telemetry visible | Legacy endpoint atomic write-through + deprecation headers + bypass/call counters | SC-004, SC-010, telemetry feeds for sunset planning |

**GitHub Issue Discipline (NON-NEGOTIABLE)**: Before `/speckit.tasks` runs, the following GitHub issues MUST exist with proper parent linkage:

- Feature 049 parent issue (this spec)
  - User Story 1 (P1) — Banner clears via Org-level Mission Owner inheritance
  - User Story 2 (P2) — Actionable banner + per-system override surface
  - User Story 3 (P3) — Onboarding wizard captures 7-role set
  - User Story 4 (P4) — Legacy reconciliation + deprecation telemetry

Tasks generated by `/speckit.tasks` become checklist items in their parent User Story issue body. The Feature parent issue's body links to the four sub-issues and to this plan.

## Complexity Tracking

> No deviation from Simplicity (§II) or YAGNI (§III). Table left intentionally empty.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|--------------------------------------|
| _(none)_ | _(n/a)_ | _(n/a)_ |

## Phase 0 / Phase 1 Outputs

- **Phase 0**: [research.md](./research.md) — 8 architecture decisions resolved (queue strategy, atomicity primitive, matrix encoding, SoD detection placement, metrics infrastructure, deprecation-header strategy, dialog reuse strategy, fan-out timing).
- **Phase 1**: [data-model.md](./data-model.md), [contracts/http-api.md](./contracts/http-api.md), [contracts/internal-services.md](./contracts/internal-services.md), [contracts/frontend-types.md](./contracts/frontend-types.md), [quickstart.md](./quickstart.md).

## Post-Design Constitution Re-Check

Re-evaluated after Phase 1 artifacts were produced. No new violations introduced.

| # | Principle / Standard | Phase 1 Verdict | Notes |
|---|---|---|---|
| II | Simplicity | PASS | Phase 1 confirms zero new tables; `OrganizationRoleAssignment` schema unchanged at the column level. Queue is `Channel<PropagationIntent>` — single in-process primitive. |
| III | YAGNI | PASS | Contracts include exactly the endpoints driven by FRs. No "v2" reservations, no feature flags beyond the existing deprecation window. |
| IV | SRP | PASS | Contracts confirm each service has one verb: `IUnifiedRoleReader.GetSystemRolesAsync`, `IRoleAuthorizationService.Authorize`, `ISoDConflictDetector.Detect`, `IOrganizationRoleFanoutQueue.Enqueue` + `DrainAsync`. |
| VI | TDD | PASS — enforced by tasks | Test file layout in [Project Structure](#project-structure) maps 1:1 to FRs and SCs. |
| VII | Observability | PASS | [contracts/internal-services.md](./contracts/internal-services.md) names the 3 counters and 1 histogram explicitly. |
| Security: Tenant Isolation | PASS | Reader, worker, queue, and SoD detector all carry `TenantId` on every signature. |

**Final gate result**: **PASS**. Ready for `/speckit.tasks`.
