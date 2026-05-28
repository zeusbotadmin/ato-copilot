# Phase 0 — Research: CSP-Inherited Capability Lifecycle

**Feature**: 050-csp-capability-lifecycle
**Plan**: [plan.md](./plan.md)
**Spec**: [spec.md](./spec.md)
**Date**: 2026-05-22

All decisions below lock in answers derived from (a) the 2026-05-21 design
conversation captured in `spec.md` § Clarifications → Session 2026-05-21,
(b) the 2026-05-22 `/speckit.clarify` round captured in `spec.md` §
Clarifications → Session 2026-05-22, and (c) verified facts about the
current `main` branch.

There are **no remaining `[NEEDS CLARIFICATION]` markers** in `spec.md`.
This document exists to make each decision auditable: what was chosen, why,
and what alternatives were rejected.

---

## R1 — History-write timing

**Decision**: Write the `CapabilityHistoryEvent` row **synchronously, in the
same database transaction** as the state change it audits.

**Rationale**:

- Crash-safety. A crash between the state change and an async audit write
  would leave the audit trail permanently incomplete. For a compliance
  audit log this is unacceptable — the whole purpose is to be the
  authoritative answer to "who approved this and when?".
- Bounded overhead. Each operation writes exactly one indexed `INSERT`;
  the additional latency is on the order of milliseconds against the
  existing capability PATCH transaction.
- Simplicity. No queue, no outbox, no retry policy, no failure-handling
  branch. The transaction either commits both rows or neither.

**Alternatives considered**:

| Alternative | Rejected because |
|---|---|
| Async write via in-process channel | Crash window between commit and channel drain leaves audit gap. |
| Outbox pattern with a dispatcher | Adds new infrastructure (outbox table + dispatcher worker) for a feature whose write volume is < 20 events per capability. Premature. |
| Best-effort fire-and-forget | Silent gap on transient failure. Violates "no silent error swallowing" (Constitution § Engineering Principles, Verification Protocol rule 6). |

**Consequence**: Every state-changing CSP capability operation method
ends with the pattern `await db.History.AddAsync(evt); await
db.SaveChangesAsync()` inside an explicit transaction. The `await
SaveChangesAsync` call covers both the capability mutation and the
history insert atomically.

---

## R2 — Migration vs. `EnsureCreatedAsync`

**Decision**: Add the entity to `OnModelCreating` in `AtoCopilotContext`
**and** ship a new EF Core migration. Both paths cover the table.

**Rationale**:

- Verified via [DatabaseInitializationService.cs](../../src/Ato.Copilot.Core/Data/Services/DatabaseInitializationService.cs):
  the service branches on the configured `DatabaseProvider`. SQLite (dev)
  runs `EnsureCreatedAsync`; SQL Server (prod) runs `MigrateAsync`. **Both
  paths must work** — there is no single "convention" to follow.
- Existing precedent in [src/Ato.Copilot.Core/Data/Migrations/](../../src/Ato.Copilot.Core/Data/Migrations/):
  recent features (033, 035, 039) ship EF Core migrations.
- An `EnsureSchemaAdditions/` module is optional for dev hot-upgrades on
  already-initialized SQLite DBs. We will only add one if developers
  report friction; the `EnsureCreatedAsync` + new entity path handles a
  fresh DB without it.

**Alternatives considered**:

| Alternative | Rejected because |
|---|---|
| `EnsureSchemaAdditions/` module only, no migration | SQL Server production runs `MigrateAsync` — the table would never be created in prod. |
| Migration only, no `OnModelCreating` config | SQLite dev runs `EnsureCreatedAsync`, which uses the model snapshot — the table would never be created in dev. |
| Hybrid where the entity is declared only in the migration's snapshot | Diverges from every other entity in the codebase; fragile. |

**Consequence**: One new file pair — `OnModelCreating` block in
`AtoCopilotContext.cs` plus a new
`<timestamp>_AddCapabilityHistoryEvents.cs` migration. User approved
this in the 2026-05-21 design conversation: *"the
CapabilityHistoryEvent table addition is acceptable (one new migration,
no breaking changes)"*.

---

## R3 — Endpoint shape: `POST .../move` vs. `PATCH .../parent`

**Decision**: `POST /api/csp/inherited-components/{componentId}/capabilities/{capabilityId}/move`
with a JSON body containing the target component id and an `If-Match`
header for the capability's current `rowVersion`.

**Rationale**:

- The reparent is a **named action**, not a generic field update — it
  resets `status` to `NeedsReview`, writes a history event, and re-binds
  a FK. A `POST .../move` URL communicates that intent more honestly than
  a `PATCH` that would imply "change a field on this resource".
- Aligns with the existing `POST .../publish` and `POST .../remap` action
  endpoints in `CspInheritedComponentEndpoints.cs` (verified in
  [CspInheritedComponentEndpoints.cs](../../src/Ato.Copilot.Mcp/Endpoints/Csp/CspInheritedComponentEndpoints.cs)).
- The `POST` verb is non-idempotent in HTTP semantics, which matches the
  reality that re-running the move emits another history event (and is
  guarded by `If-Match` against accidental replay).

**Alternatives considered**:

| Alternative | Rejected because |
|---|---|
| `PATCH .../capabilities/{id}` with a `cspInheritedComponentId` field | Implies a generic field write, masks the audit / status-reset side effects. Confusing to API consumers. |
| `POST .../move` on the capability itself (no parent component in URL) | The existing capability URL nests under `inherited-components/{componentId}/capabilities/{capabilityId}`; the path stays consistent with read URLs even after the move. |
| Custom verb (`MOVE`) | Not in the HTTP verb whitelist for any of the existing CSP endpoint groups; non-standard. |

**Consequence**: Endpoint registration in
`CspInheritedComponentEndpoints.cs` adds a single `MapPost` for `.../move`
returning the updated capability DTO + new `rowVersion`. The capability
URL after the move is the same (capability id is stable; component path
remains the original for back-compat with deep links — the client
refreshes by id, not by URL).

---

## R4 — `MoveCapabilityDialog` as standalone component vs. inline

**Decision**: Standalone component
`src/Ato.Copilot.Dashboard/src/features/csp-inherited-components/MoveCapabilityDialog.tsx`.

**Rationale**:

- The dialog encapsulates: (a) fetching candidate components, (b) the
  filter-as-you-type textbox state, (c) the disabled-state empty check,
  (d) the confirm-and-submit flow with `If-Match` handling, (e) error
  surfacing for `412 ROW_VERSION_MISMATCH`, archived target, and
  cross-tenant target. Inlining this into `CapabilityDetailDrawer.tsx`
  would push the drawer past the "single responsibility" line.
- Reusable shape — if a future feature adds bulk reparent (NG-4 today),
  the same component can be embedded in a different parent without
  rework.
- Matches the existing dashboard convention (one component per `*.tsx`
  file under `features/csp-inherited-components/`).

**Alternatives considered**:

| Alternative | Rejected because |
|---|---|
| Inline the dialog markup in `CapabilityDetailDrawer.tsx` | Bloats the drawer; mixes "view capability" and "perform action" responsibilities. |
| Generic `TargetComponentPicker.tsx` shared across features | Premature abstraction — there is one consumer today. |
| Render the dialog as a route (`/move/:capabilityId`) | Breaks the in-drawer flow; loses the drawer scroll position; requires deep-link handling for a one-shot action. |

**Consequence**: One new file. The drawer imports it and shows it on
"Move to another component…" click. The dialog owns the network calls
to `ListCspInheritedComponents` and `ReparentCapability`.

---

## R5 — Test strategy

**Decision**: Four-layer test pyramid, each enumerated in
[plan.md § Project Structure](./plan.md#source-code-repository-root):

| Layer | Project | Coverage |
|---|---|---|
| C# unit | `tests/Ato.Copilot.Tests.Unit/Tenancy/` | `CapabilityHistoryService` invariants (tenant filter, immutability), `CspInheritedComponentService.ReparentCapabilityAsync` (status reset, `mappedBy` preservation, `rowVersion` bump, history row written, transaction atomicity), `CreateCapabilityAsync` manual-add default + `markMappedImmediately` override. |
| C# integration | `tests/Ato.Copilot.Tests.Integration/Csp/` | `POST .../move` (200 OK, 412 stale ETag, 404 archived target, 404 cross-tenant target / not 403 per Edge Cases, 400 missing `If-Match`), `GET .../history` (tenant isolation, reverse-chronological order, empty-history returns 200 + `items:[]`, pagination clamp 1–200). |
| TS unit / component | `src/Ato.Copilot.Dashboard/src/__tests__/features/csp-inherited-components/` | `MoveCapabilityDialog` target filtering + filter-as-you-type + disabled empty state; `CapabilityDetailDrawer.move` flow; `CapabilityDetailDrawer.history` rendering; `ComponentDetailDrawer.advanced` disclosure + confirm dialog; `ComponentDetailDrawer.markMapped` checkbox default + override; picker review-count chip visibility N>0 vs N=0. |
| Manual | per [quickstart.md](./quickstart.md) | CSP-Admin simulated context end-to-end: + Add NeedsReview, mark mapped immediately override, move capability across components, history visible in drawer, Advanced disclosure for Remap, picker chip visible. |

**Rationale**: Constitution § VI requires TDD — every new method opens
with a failing test. The four-layer split keeps each layer focused: C#
unit tests pin pure-domain invariants without a DB; integration tests
pin wire contracts and tenant isolation through `WebApplicationFactory`;
TS component tests pin UI behavior in isolation from the backend;
manual verification exercises the integrated flow under the simulated
CSP-Admin context.

**Alternatives considered**: A "fewer, broader" approach (only manual +
one integration test per FR) was rejected because Constitution § VI is
non-negotiable about failing-test-first per code path.

---

## R6 — Cross-references to Feature 048 (Tenant Isolation)

**Decision**: Every read query and every server-side validation re-uses
the tenant-resolution helpers introduced by Feature 048. No new
authorization primitive is added.

**Verified facts** (read on current `main`):

- `CspInheritedCapability` already carries `TenantId` and queries are
  already filtered by `TenantId` in `CspInheritedComponentService`.
- The CSP-Admin role check is performed at the endpoint layer via the
  existing `RequireCspAdmin()` helper (or equivalent — confirmed by
  the existing `POST .../publish` and `POST .../remap` endpoints).
- The `If-Match` / `412 ROW_VERSION_MISMATCH` envelope is implemented
  uniformly across CSP capability endpoints.

**Reused primitives**:

| Primitive | Source | Use in 050 |
|---|---|---|
| Tenant scope on every read | Feature 048 § FR-T6 | Capability history reads + target-component lookups. |
| CSP-Admin role gate | Feature 048 § FR-T9 | `POST .../move`, `GET .../history`, extended `POST .../capabilities` create. |
| `If-Match` / `ETag` / 412 | Feature 048 § FR-T11 | `POST .../move` requires `If-Match`. |
| Capability listing pagination shape | Feature 048 § FR-T17 (verified in `CspInheritedComponentEndpoints.cs` lines 96–175) | Reused by `MoveCapabilityDialog` and by the new history endpoint. |

**Consequence**: No new infra. The new endpoints sit on top of the
existing Feature 048 gates exactly as `POST .../remap` and
`PATCH .../capabilities/{id}` do today.

---

## R7 — History endpoint pagination contract (Clarification Q1)

**Decision**: `GET /api/csp/inherited-components/{componentId}/capabilities/{capabilityId}/history?page=1&pageSize=50`

- `page` query parameter; integer ≥ 1; default `1`.
- `pageSize` query parameter; integer; default `50`; clamped to the
  inclusive range `1–200` (matches every other CSP list endpoint;
  verified in `CspInheritedComponentEndpoints.cs:121`:
  `Math.Clamp(pageSize ?? 50, 1, 200)`).
- Response body: `{ items, page, pageSize, total }` where `items` is
  the history rows ordered by `OccurredAt DESC` (most-recent first).
- Empty history returns HTTP 200 with `items: []`, **not** 404.

**Rationale**:

- House-style consistency. The four other CSP list endpoints (`ListCspInheritedComponents`,
  `GetCapabilities`, etc.) all use this shape. The drawer's network
  layer (`api.ts`) already knows how to consume it.
- Bounded response. Audit logs grow unbounded over a capability's
  lifetime. Even with the assumption of < 20 events typical, a long-
  lived hot capability could accumulate hundreds; capping at 200 per
  page prevents megabyte payloads.
- Empty-as-200 reflects the truth: the capability exists; it just has
  no history yet. A 404 would conflate "capability not found" with
  "capability found but new".

**Alternatives considered**: see [spec.md § Clarifications Session
2026-05-22 Q1](./spec.md).

---

## R8 — Move-dialog picker fetch strategy (Clarification Q2)

**Decision**: Single eager call to
`GET /api/csp/inherited-components?page=1&pageSize=200&statusFilter=non-archived`
(server filter), then a client-side `filter-as-you-type` textbox.

**Rationale**:

- Tenants realistically have tens to low hundreds of CSP-inherited
  components, not thousands.
- One round-trip + DOM list of 200 is well within browser performance
  budgets at default heights; renders < 16 ms.
- Client-side substring filter is the lowest-latency interaction for
  a "I know the name" pick.
- Avoids adding a new server-side `q` search parameter to
  `ListCspInheritedComponents` (out of scope, would touch shared
  endpoint contract).
- Safety valve: if `total > 200` in the response, the dialog shows a
  visible **"showing first 200; refine your component catalog"**
  notice instead of silently truncating.

**Alternatives considered**: see [spec.md § Clarifications Session
2026-05-22 Q2](./spec.md).

---

## R9 — History retention semantics (Clarification Q3)

**Decision**: `CapabilityHistoryEvent` rows survive their parent
`CspInheritedCapability` row. The only operation that cascades them away
is **tenant offboarding**.

**Rationale**:

- Audit trail's whole purpose is to outlive state changes. "Who
  approved this row?" must answerable even after the row is gone.
- Archive (event type `Archived`) is a state change, not a delete;
  archive-cascade would defeat the entire feature.
- No application endpoint exposes a capability hard-delete, so the
  only legitimate row removal is tenant offboarding (already
  implemented in Feature 048's tenant-removal flow).
- An out-of-band hard delete (direct SQL) is a database operation,
  not an application operation — history rows MUST be left in place
  so an auditor can still see what the deleted capability had decided.

**Database-level implementation**:

- `CapabilityHistoryEvent.CapabilityId` is a logical FK to
  `CspInheritedCapability.Id` but **MUST NOT** be declared as a
  cascading FK in EF Core. Declare as `Restrict` (or no FK at all —
  document the logical relationship in the entity comment).
- `CapabilityHistoryEvent.TenantId` is a logical FK to `Tenant.Id`
  with `Cascade` delete behavior so that tenant offboarding removes
  the rows.

**Alternatives considered**: see [spec.md § Clarifications Session
2026-05-22 Q3](./spec.md).

---

## R10 — Move-action disabled-state (Clarification Q4)

**Decision**: The "Move to another component…" affordance on
`CapabilityDetailDrawer` is rendered disabled with an inline tooltip
when no eligible target component exists in the tenant. The dialog is
**not openable** in this state; the user never sees an empty picker.

**Rationale**:

- Empty picker looks like a bug.
- The eligibility check is cheap — render-time, based on the
  component-count signal the drawer already has access to.
- The tooltip explains *why* the action is disabled, which guides the
  user toward the productive action (create a second component first).

**Implementation note**: The drawer either (a) reads from the existing
component-list state if it's already loaded for the breadcrumb /
navigation, or (b) lazily fetches `ListCspInheritedComponents?page=1&pageSize=2`
on first render to determine if at least one other non-archived
component exists. Phase 1 contracts will pin which.

**Alternatives considered**: see [spec.md § Clarifications Session
2026-05-22 Q4](./spec.md).

---

## R11 — Remap audit semantics (Clarification Q5)

**Decision**: A Remap run writes **one** `CapabilityHistoryEvent` per
**changed** capability and **zero** events for preserved rows. All
events from the same run share a `remapRunId` value (a GUID generated
at Remap-run start) in `metadataJson`. The `actorOid` on every event
is the CSP-Admin who clicked Continue in the Advanced disclosure
confirm dialog (FR-008); the AI pipeline does not have its own actor
identity.

**Rationale**:

- "Changed" is the truthful audit category. Writing events for
  preserved rows would pollute the trail with no-ops and make
  "show me everything that changed in run X" require post-filtering.
- The `remapRunId` correlator gives auditors a single index to ask
  "what did run X do?" — equivalent to a transaction id but scoped
  to the Remap operation.
- Anchoring `actorOid` to the human who clicked Continue makes the
  audit trail answer the right question: *who authorized this AI
  run?* (not "the AI did it"). The Advanced disclosure is the human
  approval gate.

**Event-type mapping inside a Remap run**:

| Capability outcome | Event type written | `metadataJson` shape |
|---|---|---|
| New capability created by Remap | `Created` | `{ remapRunId, source: "Remap" }` |
| Existing AI-mapped capability's fields changed | `Edited` | `{ remapRunId, source: "Remap", diff: <change-summary> }` |
| Existing AI-mapped capability no longer matched by AI | `Archived` | `{ remapRunId, source: "Remap" }` |
| Existing `mappedBy = User` capability preserved | *(no event)* | n/a |
| Existing AI capability AI-output identical | *(no event)* | n/a |

**Alternatives considered**: see [spec.md § Clarifications Session
2026-05-22 Q5](./spec.md).

---

## Summary

| ID | Topic | Decision | Source |
|---|---|---|---|
| R1 | History-write timing | Synchronous, same transaction | 2026-05-21 design conversation |
| R2 | Migration vs. `EnsureCreated` | Both: `OnModelCreating` + new migration | Verified in `DatabaseInitializationService` |
| R3 | Endpoint shape | `POST .../move`, `GET .../history` | Existing CSP endpoint precedent |
| R4 | Move dialog component shape | Standalone `MoveCapabilityDialog.tsx` | Dashboard convention |
| R5 | Test strategy | 4-layer pyramid (C# unit, C# integration, TS component, manual) | Constitution § VI |
| R6 | Feature 048 cross-references | Reuse all existing primitives | Verified in `CspInheritedComponentEndpoints.cs` |
| R7 | History pagination contract | `page`/`pageSize` 1–200, default 50; empty-as-200 | Clarification Q1 |
| R8 | Picker fetch strategy | Single call `pageSize=200`, client-side filter | Clarification Q2 |
| R9 | History retention | Survives capability; tenant-offboarding cascades only | Clarification Q3 |
| R10 | Move action disabled-state | Render-time disable + tooltip | Clarification Q4 |
| R11 | Remap audit semantics | One event per changed row + `remapRunId` correlator | Clarification Q5 |

All decisions traceable to either a clarification Q/A or a verified fact
about current `main`. No open research items remain.
