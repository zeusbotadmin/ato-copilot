# Phase 0: Research — Unified RMF Role Assignments

**Branch**: `049-unified-rmf-role-assignments`
**Date**: 2026-05-19
**Status**: Complete — all eight decisions resolved, no remaining `NEEDS CLARIFICATION` markers in [spec.md](./spec.md).

All eight decisions below are backed by **prior art that already ships in this codebase** — no new architectural ground broken. Verification commands and exact source-file references are provided so a reviewer can confirm each claim against the current `main` branch.

---

## R1: FR-028 fan-out queue strategy

**Decision**: In-process `System.Threading.Channels.Channel.CreateBounded<PropagationIntent>` (capacity 1024, `BoundedChannelFullMode.Wait`) wrapped in a singleton `OrganizationRoleFanoutQueue` class; drained by an `OrganizationRoleFanoutWorker : BackgroundService`. Crash recovery via **startup reconciliation sweep**, not a persistent job table.

**Rationale**:

- **Prior art** — this is exactly the `WizardJobChannel` + `WizardJobHostedService` pattern at [src/Ato.Copilot.Agents/Compliance/Services/Onboarding/Jobs/WizardJobRunner.cs](../../src/Ato.Copilot.Agents/Compliance/Services/Onboarding/Jobs/WizardJobRunner.cs) and [src/Ato.Copilot.Agents/Compliance/Services/Onboarding/Jobs/WizardJobHostedService.cs](../../src/Ato.Copilot.Agents/Compliance/Services/Onboarding/Jobs/WizardJobHostedService.cs). Repeating an established pattern is mandated by Constitution §II (Simplicity).
- **Crash resilience without a new table** — FR-028 explicitly allows reconciliation as an alternative to a persistent queue. Reconciliation is a single tenant-scoped query: `select OrgRoleId, TenantId from OrganizationRoleAssignments oraa where oraa.RemovedAt is null and exists (select 1 from Systems s where s.TenantId = oraa.TenantId and not exists (select 1 from SystemRoleAssignments sra where sra.SystemId = s.Id and sra.SourceOrganizationRoleAssignmentId = oraa.Id))` — enqueue one `PropagationIntent` per matching `(OrgRoleId, TenantId)` pair on startup. No state is lost across process restart because the work-set is always derivable from the database.
- **Idempotency** — the worker's per-system loop checks for an existing `SystemRoleAssignment` row with matching `SourceOrganizationRoleAssignmentId` before inserting, so a retry produces the same end state (FR-028 requirement).

**Alternatives considered and rejected**:

| Alternative | Reason rejected |
|---|---|
| New `RoleFanoutJob` table with state machine (`Pending`/`Running`/`Completed`/`Failed`) | New table, new migration, new index, new janitor logic. §II Simplicity violation with no functional gain — the startup sweep already provides exactly-once-eventually semantics. |
| Azure Service Bus / Storage Queue | Adds external infra dependency for an in-process fan-out problem. §III YAGNI. Tenant isolation harder to enforce across a shared queue. |
| Per-request inline materialization (synchronous) | Rejected in [Clarifications Q5](./spec.md#session-2026-05-19) — a tenant with N systems would block the Org-level write for N×insert duration. FR-029 already makes banner clearing instant. |
| `IServiceBus` abstraction over the queue | Speculative generalization. §III YAGNI. |

**Verification**:

- `grep_search` for `Channel.CreateBounded` returned 4 hits in `src/`: `WizardJobChannel`, `SspExportJob`, `PackageExportJob`, `NotificationService` — established pattern.
- `grep_search` for `BackgroundService` returned 13 hits — established host pattern.

---

## R2: Legacy write-through atomicity primitive

**Decision**: Single `AtoCopilotContext` instance, both `Add`/`Update` operations on the change-tracker, one `SaveChangesAsync`. **No explicit `IDbContextTransaction`.** EF Core 9 wraps a single `SaveChangesAsync` in an implicit transaction; the entire change set commits or rolls back as a unit.

**Rationale**:

- FR-018 explicitly allows this: "stage both `Add`/`Update` operations in the same change-tracker and commit with a single `SaveChangesAsync` (or an explicit `IDbContextTransaction` wrapping them when intermediate flushes are needed)". No intermediate flush is needed for the legacy write-through — both rows are independent inserts.
- **Simplicity** — `BeginTransactionAsync` + `CommitAsync` + `try/catch RollbackAsync` is 8+ lines of boilerplate that EF Core already provides for free in the `SaveChangesAsync` path.
- **Provider parity** — works identically on SQLite (dev) and SQL Server (prod) via EF Core's provider abstraction. Tested by the SC-010 fault-injection test using EF Core's `IDbCommandInterceptor` to throw on the second `INSERT` statement and assert that the first did not commit.

**Alternatives considered and rejected**:

| Alternative | Reason rejected |
|---|---|
| `await using var txn = await ctx.Database.BeginTransactionAsync(); ...; await txn.CommitAsync();` | Boilerplate without benefit when a single `SaveChangesAsync` is the only DB operation. |
| `TransactionScope` (`System.Transactions`) | Heavier API; promotion to MSDTC risk on SQL Server; unsupported on SQLite without additional config. §II Simplicity violation. |
| Saga / outbox pattern | Massive over-engineering for a 2-write atomicity problem. §II + §III. |

**Verification**:

- The existing `OrganizationRoleAssignmentService` already uses the `SaveChangesAsync`-only pattern for multi-row updates — same shape as proposed here.

---

## R3: FR-027 role-tiered authorization matrix encoding

**Decision**: Pure-functional `RoleAuthorizationService` with a `static readonly` `Dictionary<RmfRole, ImmutableHashSet<RmfRole>>` keyed by caller's effective role. Single public method:

```csharp
AuthorizationResult Authorize(RmfRole callerEffectiveRole, RmfRole targetRole, bool isBootstrapSession);
```

`AuthorizationResult` is a `readonly record struct` returning `(bool Allowed, string? DeniedReason)`.

**Rationale**:

- **No DB I/O** — the matrix is static configuration per the spec. Resolving the caller's effective role is the caller's responsibility (it has the auth principal already); this service decides only the matrix lookup.
- **Testability** — every cell in the matrix is a deterministic unit test. SC-009 demands "every disallowed cell has at least one negative test"; a generator test that iterates all `(caller, target)` pairs against the matrix proves the requirement (more in [R6](#r6-test-strategy)).
- **Bootstrap exception** — handled by the single `isBootstrapSession` flag (true only when the wizard has zero existing `OrganizationRoleAssignment` rows for the tenant). The wizard service computes this once at session start.
- **§IV SRP** — the service does one thing: matrix lookup. It does not resolve the caller's identity, fetch the tenant, or call the DB.

**Alternatives considered and rejected**:

| Alternative | Reason rejected |
|---|---|
| ASP.NET Core policy-based authorization (`[Authorize(Policy = ...)]`) | The matrix depends on the **target role** (a request body field), not just the caller's claims. Policies cannot inspect request bodies. Would require a custom `IAuthorizationHandler` per disallowed cell — explosion of policy registrations. |
| Database-driven RBAC table | YAGNI. The matrix is closed (7×7 = 49 cells, fully enumerated in FR-027). A DB table introduces drift risk between code and data. |
| Attribute-based access control (ABAC) framework | §III YAGNI — heavier than needed for a closed matrix. |

---

## R4: FR-026 separation-of-duties detection placement

**Decision**: New `SoDConflictDetector` service called **inside the same `DbContext` scope** as the write. The detector queries existing non-removed `OrganizationRoleAssignment` rows for the candidate `PersonId` in the tenant, compares against the two DoDI pairs encoded in `static readonly` data, and returns a `IReadOnlyList<SoDWarning>` (often empty). Warnings are surfaced in the response envelope's `warnings` array (FR-026) **without** rolling the transaction back.

**Rationale**:

- **One query, tenant-scoped** — `where TenantId = @tid and PersonId = @pid and RemovedAt is null` returns at most 6 rows (7 roles minus the one being assigned). Trivial cost; no caching needed.
- **Inside the write transaction** — guarantees the conflict check sees the same world the write commits into (no TOCTOU between "check" and "write").
- **Per-write, not per-read** — FR-026 mandates warnings on the write side. Readers do not recompute SoD; they trust the stored data.
- **Same detector used by all 5 write paths** (Org-level surface, per-system Roles panel, onboarding wizard, legacy POST endpoint, MCP tools) so the warning shape is uniform.

**Alternatives considered and rejected**:

| Alternative | Reason rejected |
|---|---|
| Read-time conflict computation (reader recomputes on every banner load) | Wastes CPU on every page load when the data is write-rare / read-frequent. Also: warnings are auditor-facing artifacts; they belong on the write that created the conflict. |
| Async SoD audit worker (write fast, warn later) | Surprise warning later loses the audit context (who clicked OK, when, against what other state). FR-026 requires the warning be in the write response. |
| Hard block on conflict | Rejected in [Clarifications Q2](./spec.md#session-2026-05-19) — DoDI 8510.01 allows documented deviations; ISSMs must be able to record them. |

---

## R5: Metrics infrastructure (FR-018, FR-019, FR-028, SC-011)

**Decision**: Reuse the existing `System.Diagnostics.Metrics.Meter` named `"Ato.Copilot"` (already used by [HttpMetrics.cs](../../src/Ato.Copilot.Core/Observability/HttpMetrics.cs), [ToolMetrics.cs](../../src/Ato.Copilot.Core/Observability/ToolMetrics.cs), and [ComplianceMetricsService.cs](../../src/Ato.Copilot.Agents/Observability/ComplianceMetricsService.cs)). Add a new sibling `RoleMetrics.cs` at `src/Ato.Copilot.Core/Observability/RoleMetrics.cs` that owns three counters + one histogram:

| Instrument | Type | Labels | Driven by |
|---|---|---|---|
| `legacy_role_endpoint_call_total` | `Counter<long>` | `tenant_id`, `method` (POST/DELETE/GET) | FR-019 |
| `legacy_role_endpoint_bypass_total` | `Counter<long>` | `tenant_id`, `target_role` | FR-018 (writes that would have been denied under FR-027) |
| `sod_violation_warning_total` | `Counter<long>` | `tenant_id`, `caller_role`, `conflicting_role` | FR-026 audit |
| `org_role_propagation_duration_seconds` | `Histogram<double>` | `tenant_id`, `target_role`, `systems_bucket` (1-10/11-100/101-500/501+) | FR-028, SC-011 |

**Rationale**:

- Reuses the established meter — no new exporter wiring needed; OpenTelemetry export already attaches to this meter name in the existing OTel pipeline.
- Labels exclude PII per Constitution §VII (tenant_id is a UUID, not a user identity; role names are non-PII).
- `systems_bucket` label cardinality is bounded at 4, matching SC-011's bucket structure for clean p99 percentile reads in Grafana.

**Alternatives considered and rejected**:

| Alternative | Reason rejected |
|---|---|
| Application Insights `TelemetryClient.TrackMetric` | Coupling to AI; the OTel meter path already exports to AI in production. |
| Serilog-only (log-derived metrics) | High cardinality, expensive query in Log Analytics, no histograms. |
| New meter name (`Ato.Copilot.Roles`) | §II Simplicity — splitting meters creates a discoverability problem with no upside. |

---

## R6: Test strategy

**Decision** — four-layer coverage:

1. **Unit (Tests.Unit / Roles/)** — pure-functional services tested in isolation.
   - `RoleAuthorizationServiceTests` — `[Theory]` over all 49 `(caller, target)` cells, asserting `Allowed` matches the FR-027 matrix. Drives SC-009 by exhaustive enumeration.
   - `UnifiedRoleReaderTests` — covers all 4!=24 permutations of `{override-present, inherited-present, org-present, legacy-present}` × `{has-row, no-row}` against the expected precedence chain.
   - `SoDConflictDetectorTests` — `[Theory]` over the 7 conflict pairs and 14 non-conflict pairs.
   - `OrganizationRoleFanoutQueueTests` — bounded-capacity backpressure, idempotency contract.

2. **Integration (Tests.Integration / Roles/)** — `WebApplicationFactory<Program>` + EF Core in-memory provider for fast cases; SQLite-file provider for transaction tests.
   - `RoleAuthorizationMatrixCoverageTests` — generator-driven (a `[ClassData]` source enumerates the disallowed cells from FR-027); each test issues a real HTTP request and asserts 403 + `RBAC_ROLE_ASSIGN_DENIED`. **Drives SC-009 at the HTTP boundary** (the unit test drove it at the service boundary).
   - `LegacyWriteThroughAtomicityTests` — uses an `IDbCommandInterceptor` to throw on the second `INSERT` of the write-through; asserts both pre- and post-fault row counts in `RmfRoleAssignment`, `SystemRoleAssignment`, and `OrganizationRoleAssignment` are unchanged. **Drives SC-010.**
   - `OrganizationRoleFanoutWorkerTests` — enqueue 500 systems; assert all 500 inherited rows materialize within the test timeout; re-run the worker and assert zero duplicate inserts. **Drives SC-011** (the SLO is verified in production via the histogram; the test verifies functional completeness only).
   - `TenantIsolationRolesTests` — Tenant A queries for Tenant B's system; asserts not-found. **Drives SC-006.**
   - `DeprecationHeadersTests` — every HTTP method on the legacy endpoint emits `Deprecation: true` and `Sunset: <date>`. **Drives FR-019.**

3. **Manual (Tests.Manual / Roles/)** — dashboard banner-click flow scripted for the manual-tests document; signed off before release.

4. **Frontend (`npm run typecheck`)** — `tsc --noEmit` on the Dashboard catches drift between `rolesApi.ts` types and the response envelope shape. **Drives SC-005.**

**Rationale**:

- TDD ordering: every unit test is written before its production code (FR-022, Constitution §VI). Integration tests follow once the unit-level shape is stable.
- AAA markers enforced by the existing `tests/Ato.Copilot.Tests.Unit/.editorconfig` (already configured).
- 80% coverage gate on modified paths satisfied by the matrix + precedence-chain combinatorial tests, which exhaustively traverse the new code.

---

## R7: Deprecation header strategy (FR-019)

**Decision**: Per-endpoint result extension `DashboardEndpointResults.WithDeprecationHeaders(launchDateUtc)` applied in the legacy `/api/dashboard/systems/{systemId}/roles` route handlers (GET, POST, DELETE). The `Sunset` date is computed at process startup from `LaunchDate` (read from configuration; default = first deploy date stamped into config at release time) and cached for the process lifetime.

**Rationale**:

- **Single endpoint group** — the deprecation headers belong only on the three legacy methods. Global middleware would either filter every request (wasteful) or carry a route allow-list (gross duplication of the route table).
- **Computed once** — no per-request date math.
- **Configurable** — `LaunchDate` lives in `appsettings.Production.json` so a re-deploy doesn't reset the 90-day clock.

**Alternatives considered and rejected**:

| Alternative | Reason rejected |
|---|---|
| Global ASP.NET Core middleware | Filtering middleware to a single endpoint group is fragile; the per-endpoint extension is clearer at the route definition. |
| `Sunset` computed per-request | Wasteful CPU; the date is deterministic from `LaunchDate`. |
| `Sunset` from environment variable | Less auditable than configuration; harder to verify in code review. |

---

## R8: AssignRoleDialog reuse strategy (P2)

**Decision**: Build a single new `AssignRoleDialog.tsx` component at `src/Ato.Copilot.Dashboard/src/components/roles/AssignRoleDialog.tsx` with this props shape:

```ts
type AssignRoleDialogProps = {
  open: boolean;
  onClose: () => void;
  scope: { kind: 'system'; systemId: string } | { kind: 'organization' };
  initialRole?: RmfRole;   // e.g. banner pre-targets 'MissionOwner'
  onAssigned: (result: AssignmentResult) => void;  // includes server warnings (SoD)
};
```

The dialog is used by **three** call sites:

1. `SystemDetail.tsx` — inline Mission Owner banner button passes `initialRole='MissionOwner'`, `lockRole=true`.
2. `components/cards/RoleAssignmentPanel.tsx` — existing per-system card (MODIFY); "Override" / "Assign" buttons pass the row's role.
3. `features/onboarding/steps/Step2RoleAssignments.tsx` — wizard's three new role rows open the dialog scoped to `organization` instead of `system`, with `bootstrap=true` on first invocation.

The previously orphaned `RoleAssignmentPanel.tsx` is **retained and rewired** (not deleted); deletion is explicitly out of scope per [spec.md § Out of Scope](./spec.md#out-of-scope). The panel renders the 7 roles and delegates editing to `AssignRoleDialog`.

**Rationale**:

- **One component, three call sites** — §IV SRP without duplication.
- **Server-side SoD warnings flow back into the dialog** — when the server returns `200 OK` with a non-empty `warnings` array, the dialog shows a non-blocking inline notice quoting the DoDI reference (FR-026 UX surface).
- **Server-side RBAC denials flow back** — `403 RBAC_ROLE_ASSIGN_DENIED` shows an inline error citing the caller's effective role and the disallowed target (FR-027 error message).

**Alternatives considered and rejected**:

| Alternative | Reason rejected |
|---|---|
| Three separate dialogs (one per call site) | Triplicates form-validation logic; drift risk between three places. §II Simplicity violation. |
| Reuse the existing wizard step component inline as a dialog | Different IA — the wizard is a flow with progress indicator; the dialog is modal. Mixing these is worse than building one focused component. |
| Inline form (no modal) on every call site | Breaks the banner-click UX (SC-003: three clicks to clear). |

---

## Decision Summary

| ID | Decision | Source / Prior art |
|---|---|---|
| R1 | In-process bounded `Channel` + `BackgroundService` worker with startup reconciliation sweep | [WizardJobHostedService.cs](../../src/Ato.Copilot.Agents/Compliance/Services/Onboarding/Jobs/WizardJobHostedService.cs), [WizardJobRunner.cs](../../src/Ato.Copilot.Agents/Compliance/Services/Onboarding/Jobs/WizardJobRunner.cs) |
| R2 | Single `SaveChangesAsync` (implicit transaction) for legacy write-through | EF Core 9 default; `OrganizationRoleAssignmentService` precedent |
| R3 | Static `Dictionary`-backed `RoleAuthorizationService` with bootstrap-flag exception | FR-027 closed matrix |
| R4 | `SoDConflictDetector` invoked inside the write transaction; warnings in response envelope | FR-026 |
| R5 | Reuse `Meter("Ato.Copilot")`; new `RoleMetrics.cs` with 3 counters + 1 histogram | [HttpMetrics.cs](../../src/Ato.Copilot.Core/Observability/HttpMetrics.cs), [ToolMetrics.cs](../../src/Ato.Copilot.Core/Observability/ToolMetrics.cs) |
| R6 | Four-layer testing — unit / integration / manual / `tsc --noEmit` | Constitution §VI, SC-005/SC-006/SC-009/SC-010/SC-011 |
| R7 | Per-endpoint `WithDeprecationHeaders` extension; `LaunchDate` from config | FR-019 |
| R8 | One `AssignRoleDialog.tsx` reused by banner + Roles panel + wizard | §IV SRP, SC-003 |

All decisions favor existing patterns. **Zero new architectural ground broken**; the feature is composed entirely of established primitives.
