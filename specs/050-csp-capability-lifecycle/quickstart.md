# Quickstart: CSP-Inherited Capability Lifecycle

**Feature**: 050-csp-capability-lifecycle
**Plan**: [plan.md](./plan.md)
**Spec**: [spec.md](./spec.md)
**Date**: 2026-05-22

Hands-on verification recipe for an engineer who has just pulled the
feature branch. Walks through every new behavior end-to-end in the
local development environment, using the simulated CSP-Admin tenant
context already wired up by [Feature 048](../048-tenant-isolation/).

This is **not** a test suite — `tests/Ato.Copilot.Tests.Unit/`,
`tests/Ato.Copilot.Tests.Integration/`, and the dashboard's
`__tests__/` are the automated coverage. This file is a manual
sanity-check the human runs once after pulling.

---

## 1. Prerequisites

| Requirement | Version | How to check |
|---|---|---|
| .NET SDK | 9.0.x (pinned by [`global.json`](../../global.json)) | `dotnet --version` |
| Node | 20.x LTS | `node --version` |
| npm | 10.x | `npm --version` |
| jq (optional, for curl pipelines) | any | `jq --version` |

Repo bootstrap (if not already done):

```bash
./scripts/bootstrap.sh
```

---

## 2. Build & test

From the repo root:

```bash
dotnet build Ato.Copilot.sln
dotnet test  Ato.Copilot.sln \
    --filter "FullyQualifiedName~CapabilityHistory|FullyQualifiedName~Reparent"
```

The filter narrows the run to this feature's new tests. Expected: all
new tests pass. Run the full suite once before declaring done:

```bash
dotnet test Ato.Copilot.sln
```

Dashboard:

```bash
cd src/Ato.Copilot.Dashboard
npm ci
npm run typecheck
npm test -- --run features/csp-inherited-components
cd -
```

VS Code + M365 extension type-check parity (Constitution § Local
Type-Checking Parity — required even though this feature does not
touch them):

```bash
cd extensions/vscode && npm ci && npm run compile && cd -
cd extensions/m365  && npm ci && npm run build   && cd -
```

---

## 3. Local environment

### 3.1 Start the MCP server + Web Chat + dashboard

The simplest path is the full stack:

```bash
docker compose -f docker-compose.mcp.yml up --build
```

This brings up SQL Server, MCP server on `http://localhost:5181`, Web
Chat on `http://localhost:5180`, and the dashboard on
`http://localhost:5173`.

For a lightweight SQLite-only flow (skips Docker):

```bash
# Backend (MCP + Web Chat) — SQLite dev DB auto-created
dotnet run --project src/Ato.Copilot.Mcp

# Dashboard (separate terminal)
cd src/Ato.Copilot.Dashboard
npm run dev
```

### 3.2 Simulated CSP-Admin tenant context

For SQLite/dev runs without real Entra ID, the existing CAC simulation
mode (Feature 027) impersonates the CSP-Admin role. Default simulated
context:

```text
tenantId: 00000000-0000-0000-0000-000000000001   # CSP tenant
oid:      00000000-0000-0000-0000-000000000002   # CSP-Admin user
role:     CSP.Admin
```

No additional setup — the simulated identity is wired up automatically
when `Authentication:Mode = Simulation` (the dev default).

### 3.3 Seed the CSP catalog

If the CSP profile + components don't exist yet, seed them via the
existing wizard or the seed script:

```bash
./scripts/seed-systems.sh
```

After seeding, navigate to
`http://localhost:5173/csp-inherited-components` and verify at least
two CSP-inherited components are visible. The reparent demo needs at
least two non-archived components in the same tenant.

---

## 4. Verification scenarios

Each scenario starts from a freshly-seeded state and exercises one or
two FRs. Run them in order.

### 4.1 Manual-add default behavior (FR-001)

1. Open the dashboard at `http://localhost:5173/csp-inherited-components`.
2. Click a non-archived component to open its drawer.
3. Click **+ Add Capability**.
4. Fill in:
   - Name: `Sample capability A`
   - Description: `Created by quickstart.md`
   - Mapped controls: `AC-2`
5. Leave the **"Skip review and mark this capability Mapped now"**
   checkbox **unchecked**.
6. Click **Create**.

**Expect**:

- New row appears in the component's capability list.
- Status pill reads **NeedsReview** (not Mapped).
- Mapped-by reads **User**.
- Opening the new capability's drawer shows zero reviewer metadata.
- The History tab on the new capability's drawer shows exactly **one**
  row: `Created` event, your OID as actor, summary "Capability
  manually created.", metadata pill absent.

### 4.2 Manual-add with override (FR-001)

1. Repeat steps 1–4 of 4.1 with name `Sample capability B`.
2. **Check** the "Skip review and mark this capability Mapped now"
   checkbox.
3. Click **Create**.

**Expect**:

- New row appears.
- Status pill reads **Mapped**.
- Reviewer = your OID; ReviewerNote = "Mapped on create by creator."
- History tab shows exactly **two** rows in reverse-chronological
  order:
  1. `Reviewed` — "Reviewed and approved at creation time." +
     reviewerNote quoted block.
  2. `Created` — "Capability manually created." + "Auto-mapped on
     create" pill.

### 4.3 Reparent a capability (FR-002, FR-003, FR-012)

1. Open the drawer for `Sample capability A` (from 4.1).
2. Click **Move to another component…**.
3. Verify the dialog opens.
4. Verify the original component is **not** in the list.
5. Type the first few letters of a different component's name into the
   filter. Verify the list narrows.
6. Pick a target. Click **Confirm**.

**Expect**:

- Dialog closes.
- The capability now appears under the target component, with status
  **NeedsReview** (the move resets review state).
- Drawer's **MappingFailureReason** reads "Moved to a new component;
  re-review required."
- History tab on the moved capability has **two** rows now:
  1. `Moved` — "Moved from '<old>' to '<new>'."; metadata shows
     fromComponentId / toComponentId.
  2. `Created` — unchanged from 4.1.

### 4.4 Reparent — stale row version handling (FR-012)

This scenario exercises the 412 path. Easiest local repro:

1. Open `Sample capability B` (from 4.2) in two browser tabs.
2. In Tab 1, edit the description and save. (RowVersion bumps.)
3. In Tab 2 (with stale RowVersion still in the page state), click
   **Move to another component…** and confirm.

**Expect**:

- Inline error in the dialog: "Capability was modified by another user;
  reload and retry." (412 `ROW_VERSION_MISMATCH`).
- Inline "Reload capability" link that re-fetches and dismisses the
  dialog when clicked.

### 4.5 Reparent — disabled state (FR-003 extension, R10)

1. Archive every CSP-inherited component **except** the one currently
   holding `Sample capability B` (use the existing
   archive-component action; this is a destructive step in your local
   environment — restore from seed afterwards).
2. Open `Sample capability B`'s drawer.

**Expect**:

- **Move to another component…** button is rendered disabled.
- Hovering shows the tooltip: "No other CSP-inherited component exists
  yet. Create one first."
- Clicking does nothing (no dialog).

Restore: re-run `./scripts/seed-systems.sh` or unarchive the
components.

### 4.6 History pagination (FR-014)

To exercise pagination without 50+ manual edits, run the integration
test that pre-seeds 25 history rows:

```bash
dotnet test tests/Ato.Copilot.Tests.Integration \
    --filter "FullyQualifiedName~ListCapabilityHistory_Paginates"
```

Manual verification (only if you want to see it in the UI):

1. On the dashboard, with `Sample capability A`, change `pageSize` in
   the History tab's footer from 50 to 25.
2. Verify the page indicator reads `1 / 1` (you have ≤ 25 events).
3. Programmatically curl the endpoint with an over-size `pageSize`:

```bash
curl -sS "http://localhost:5181/api/csp/inherited-components/$COMPONENT_ID/capabilities/$CAPABILITY_ID/history?pageSize=999" \
    -H "Authorization: Bearer $TOKEN" \
    | jq '.data.pageSize'
```

**Expect**: response shows `200` (clamped).

### 4.7 History retention across capability archive (FR-015)

1. Pick any capability with at least one history row.
2. Archive it (via the existing archive-capability action).
3. Verify the capability now shows status **Archived** in the parent
   component's drawer.
4. Open the archived capability's drawer (CSP-Admin can still see
   archived rows).

**Expect**:

- History tab still loads.
- All previous history rows remain.
- A new `Archived` row sits at the top.

### 4.8 History retention through tenant offboarding (FR-015)

This is a destructive operation; run only if you intend to re-seed.

1. Note the count of `CapabilityHistoryEvents` rows in the dev DB:

```bash
sqlite3 src/Ato.Copilot.Mcp/atocopilot.db \
    "SELECT COUNT(*) FROM CapabilityHistoryEvents;"
```

2. Trigger tenant offboarding for the CSP tenant via the Feature 048
   tenant-remove flow. (Out of scope to script here — use the existing
   admin UI or CLI.)
3. Re-query:

```bash
sqlite3 src/Ato.Copilot.Mcp/atocopilot.db \
    "SELECT COUNT(*) FROM CapabilityHistoryEvents;"
```

**Expect**: row count drops to 0 (cascade delete fired).

Then re-seed: `./scripts/seed-systems.sh`.

### 4.9 Remap audit semantics (FR-016, R11)

1. Pick a CSP-inherited component with at least two AI-mapped
   capabilities and at least one user-mapped capability.
2. Open the component's drawer.
3. Expand the **Advanced** disclosure (collapsed by default per FR-008).
4. Click **Remap**.
5. Confirm via the acknowledgement checkbox + Continue.

**Expect**:

- The Remap completes successfully.
- For each AI capability whose AI output **changed**: a new `Edited`
  history row appears under that capability with metadata showing
  `remapRunId` (a GUID; the same GUID is shared by every changed
  capability in this run) and `source = "Remap"`.
- For each AI capability whose AI output is **identical** to its
  prior state: **no** new history row.
- For each user-mapped capability: **no** new history row (preserved
  per FR-007 + Q5).
- For each AI capability that AI removed: a new `Archived` history row
  with `remapRunId` + `source = "Remap"`.
- For each newly-discovered capability: a new `Created` history row
  with `remapRunId` + `source = "Remap"`.

To pull the run's audit trail from the CLI:

```bash
sqlite3 src/Ato.Copilot.Mcp/atocopilot.db \
    "SELECT EventType, ActorOid, OccurredAt, Summary, MetadataJson
     FROM CapabilityHistoryEvents
     WHERE MetadataJson LIKE '%remapRunId%'
     ORDER BY OccurredAt DESC LIMIT 50;"
```

All rows from this run share the same `remapRunId` GUID. The `ActorOid`
column shows your CSP-Admin OID — the AI pipeline does not have its own
identity (FR-016 / Q5).

### 4.10 Picker review-count chip (FR-009)

1. Ensure at least one component has a capability in `NeedsReview`
   state. (Scenario 4.1 creates one.)
2. Open the dashboard's CSP-inherited components list page.

**Expect**:

- The component holding the NeedsReview capability shows a yellow chip
  reading "**1 awaiting review**" (or N if you have more).
- Components with zero NeedsReview capabilities show **no** chip
  (suppressed, not "0 awaiting review").

---

## 5. Smoke tests (curl)

For an engineer who prefers raw HTTP. All commands assume:

```bash
export BASE=http://localhost:5181
export TOKEN=$(./scripts/dev-token.sh CSP.Admin)   # if available
export COMPONENT_ID=...                            # from seed
export CAPABILITY_ID=...                           # from seed
```

### 5.1 Create capability (default — NeedsReview)

```bash
curl -sS -X POST \
    "$BASE/api/csp/inherited-components/$COMPONENT_ID/capabilities" \
    -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" \
    -d '{
        "name": "smoke-default",
        "description": "Created via curl",
        "mappedNistControlIds": ["AC-2"]
    }' | jq '.data | {status, mappedBy, reviewedBy}'
```

Expect: `status: "NeedsReview"`, `mappedBy: "User"`, `reviewedBy: null`.

### 5.2 Create capability (override — Mapped immediately)

```bash
curl -sS -X POST \
    "$BASE/api/csp/inherited-components/$COMPONENT_ID/capabilities" \
    -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" \
    -d '{
        "name": "smoke-override",
        "description": "Created via curl",
        "mappedNistControlIds": ["AC-2"],
        "markMappedImmediately": true
    }' | jq '.data | {status, mappedBy, reviewedBy, reviewerNote}'
```

Expect: `status: "Mapped"`, `mappedBy: "User"`, `reviewedBy` = your
OID, `reviewerNote: "Mapped on create by creator."`.

### 5.3 Reparent

```bash
# First, fetch current rowVersion
ROW_VERSION=$(curl -sS \
    "$BASE/api/csp/inherited-components/$COMPONENT_ID/capabilities/$CAPABILITY_ID" \
    -H "Authorization: Bearer $TOKEN" \
    | jq -r '.data.rowVersion')

curl -sS -X POST \
    "$BASE/api/csp/inherited-components/$COMPONENT_ID/capabilities/$CAPABILITY_ID/move" \
    -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" \
    -H "If-Match: $ROW_VERSION" \
    -d "{\"targetComponentId\": \"$TARGET_ID\"}" | jq '.data | {cspInheritedComponentId, status}'
```

Expect: `cspInheritedComponentId` = `$TARGET_ID`, `status:
"NeedsReview"`.

### 5.4 List history (default page)

```bash
curl -sS \
    "$BASE/api/csp/inherited-components/$COMPONENT_ID/capabilities/$CAPABILITY_ID/history" \
    -H "Authorization: Bearer $TOKEN" \
    | jq '.data | {page, pageSize, total, items: (.items | length)}'
```

Expect: `page: 1, pageSize: 50, total: N, items: N` (where N ≤ 50).

### 5.5 List history — empty capability

A brand-new capability immediately after creation has its `Created`
row in history. To see truly empty history, hit a non-existent
capability id — that returns 404, not empty-200. To exercise the
empty-200 path, the integration test seeds a capability directly via
EF Core without going through `AddCapabilityAsync` (which would write
the row).

### 5.6 List history — pageSize clamp

```bash
curl -sS \
    "$BASE/api/csp/inherited-components/$COMPONENT_ID/capabilities/$CAPABILITY_ID/history?pageSize=999" \
    -H "Authorization: Bearer $TOKEN" \
    | jq '.data.pageSize'
```

Expect: `200`.

---

## 6. Constitution gates to re-verify before declaring done

Per [.specify/memory/constitution.md](../../.specify/memory/constitution.md)
and the Constitution Check in [plan.md](./plan.md):

- [ ] §VI TDD — every new method/component has a failing test
      committed before its implementation (check `git log --oneline`
      for the failing-test commits).
- [ ] § Security: Zero-Trust — endpoints enforce `IsCspAdmin` server-side;
      verified via the existing `ForbiddenNotCspAdmin` helper.
- [ ] § Security: Tenant Isolation — every read filters by `TenantId`;
      verified by the new integration test
      `ReparentCapability_RejectsCrossTenantTarget`.
- [ ] § Local Type-Checking Parity — `tsc --noEmit` clean on
      `extensions/vscode`, `extensions/m365`, and
      `src/Ato.Copilot.Dashboard`.
- [ ] § DevOps GitHub Issue Discipline — Feature 050's GitHub issue
      exists; each user story has a sub-issue with parent linkage.
- [ ] § Complexity Justification — Complexity Tracking table in
      plan.md is empty (no deviations from §§II/III). Confirmed.

---

## 7. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `CapabilityHistoryEvents` table missing in dev DB | Stale SQLite DB pre-dating the new entity | Stop the server, delete `src/Ato.Copilot.Mcp/atocopilot.db`, restart. `EnsureCreatedAsync` recreates with the new table. |
| `Could not load type CapabilityHistoryEvent` at runtime | Build artifacts out of date | `dotnet clean && dotnet build` |
| 412 every time on Reparent | RowVersion not threaded through to dialog correctly | Check `MoveCapabilityDialog`'s `capability.rowVersion` prop is fresh from the latest drawer fetch, not a cached older copy |
| Move dialog shows zero candidates | Tenant has only one non-archived component | Expected per § 4.5; create or unarchive another component |
| "> 200 components" banner appears in dev | Seed script over-populated the catalog | Reset SQLite DB or use the filter textbox |
| History tab shows 404 | Capability id mismatch with URL | URL must use the **current** parent component id (after a move, that's the new component) |

---

## 8. What this feature does **not** do

For the record — these were considered and explicitly out of scope.
Pointers to the actual decisions:

| Out of scope | Why | Where decided |
|---|---|---|
| Bulk reparent (move N capabilities at once) | Single-row UX simpler; no demand yet | [spec.md § Out of Scope (NG-4)](./spec.md) |
| History row hard-delete | Audit logs must outlive state | [research.md § R9](./research.md) |
| Manual `RemapRuns` table | `remapRunId` correlator in JSON suffices | [data-model.md § 8](./data-model.md) |
| Hosted-tenant access to CSP history | History is CSP-tenant-scoped | [research.md § R9](./research.md) |
| Custom "moved at" column on capability | Last `Moved` event answers it; column would denormalize | [data-model.md § 2](./data-model.md) (existing entity not modified) |

---

## 9. Cleanup

To return the local environment to seed state after running every
scenario above:

```bash
# Stop the server
# Then:
rm -f src/Ato.Copilot.Mcp/atocopilot.db
./scripts/seed-systems.sh
```

For Docker:

```bash
docker compose -f docker-compose.mcp.yml down -v
docker compose -f docker-compose.mcp.yml up --build
./scripts/seed-systems.sh
```
