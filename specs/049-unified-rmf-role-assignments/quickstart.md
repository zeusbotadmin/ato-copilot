# Phase 1: Quickstart — Local Verification of Unified RMF Role Assignments

**Branch**: `049-unified-rmf-role-assignments`
**Date**: 2026-05-19
**Audience**: an engineer who has the repo cloned and just wants to **prove the four user stories work end-to-end on their workstation**.

This recipe does not require any cloud resources, any Azure subscription, or any CAC card. SQLite is the dev database. Mock auth is used.

---

## Prerequisites

```bash
# Versions are pinned by global.json + package.json. Verify:
dotnet --version          # 9.0.x
node --version            # 20.x or 22.x
docker --version          # 24.x or newer
```

If you don't have them, run `scripts/bootstrap.sh` (macOS/Linux) or `scripts/bootstrap.ps1` (Windows).

---

## 0. Make sure the branch is current

```bash
cd /Volumes/Internal/repos/ato-copilot
git checkout 049-unified-rmf-role-assignments
git pull --ff-only
```

---

## 1. Build + test (TDD parity)

The failing tests are checked in first per Constitution §VI. Before implementation lands, the tests SHOULD fail. After implementation lands, they MUST pass.

```bash
dotnet build Ato.Copilot.sln
dotnet test  Ato.Copilot.sln --filter "FullyQualifiedName~Roles"
```

Expected after implementation:

- `UnifiedRoleReaderTests` — 100% pass (precedence chain + 7-role state).
- `RoleAuthorizationServiceTests` — 49 theory rows + 7 bootstrap rows pass.
- `SoDConflictDetectorTests` — DoDI 8510.01 pair detection passes.
- `OrganizationRoleFanoutQueueTests` — bounded-channel contract passes.
- `OrganizationRoleFanoutWorkerTests` — convergence + idempotency passes.
- `LegacyWriteThroughAtomicityTests` — fault-injection rollback passes.
- `TenantIsolationRolesTests` — cross-tenant query isolation passes.
- `RoleAuthorizationMatrixCoverageTests` — generator-based HTTP coverage passes.

TypeScript parity:

```bash
cd src/Ato.Copilot.Dashboard
npm ci
npm run typecheck         # tsc --noEmit
npm run build             # vite build (also runs tsc)
```

Both MUST succeed before commit (Constitution § Local Type-Checking Parity).

---

## 2. Stand up the full stack

```bash
cd /Volumes/Internal/repos/ato-copilot
docker compose -f docker-compose.mcp.yml up --build
```

This starts:

- MCP server (`ato-copilot-mcp`) on `http://localhost:5294`
- Web Chat (`ato-copilot-chat`) on `http://localhost:5295`
- Dashboard (`ato-copilot-dashboard`) on `http://localhost:5173`
- SQL Server 2022 (containerized) on `localhost,1433`

Wait for `OrganizationRoleFanoutWorker: started; running reconciliation sweep` in the MCP container logs — that confirms the new hosted service is wired in.

---

## 3. Seed a tenant + system (no roles assigned)

```bash
# Seed script provisions tenant "Acme DoD" and one RegisteredSystem,
# but DOES NOT assign any Org-level or per-system role.
./scripts/seed-systems.sh
```

Verify in the dashboard:

1. Open `http://localhost:5173`.
2. Pick the seeded system from the system list.
3. Observe the **System Profile** page shows the orange "Mission Owner is not yet assigned" banner.

This is the bug Feature 049 closes: even after Wizard Step 2 names a Mission Owner at the Org level, this banner stays orange today because it reads from the legacy table only.

---

## 4. User Story 1 walkthrough — wizard write clears banner

**Goal (US1, SC-001)**: assigning a Mission Owner in Wizard Step 2 makes the banner clear on the same system within 1 second.

1. Click the user menu → **Run onboarding wizard**.
2. Step 1: pick any organization.
3. Step 2: assign **Mission Owner** = a seeded person. Submit the step.
4. Switch back to the System Profile tab (same browser; cookie auth retains the session).
5. Refresh.

Expected: banner is **green / hidden** within ≤ 1 second. The `GET /api/roles/system/{systemId}` response payload now includes:

```json
{ "role": "MissionOwner", "person": { "displayName": "..." }, "source": "org-fallback" }
```

— `org-fallback` because the worker has not yet materialized the inherited row, but **the banner clears anyway** because step 3 of the precedence chain fired (FR-029).

Within ≤ 10 seconds, refetch and observe `source` flip from `"org-fallback"` to `"inherited"`. That's the worker fan-out completing (FR-028, SC-011).

---

## 5. User Story 2 walkthrough — system list + roles panel

**Goal (US2, SC-002)**: every system shows the assignee for every RMF role, with the inheritance source visible.

1. From the system list, open the seeded system.
2. Scroll to the new **Roles** panel (below System Details).
3. Observe a 7-row table:

   | Role | Assignee | Source |
   |---|---|---|
   | AuthorizingOfficial | — | — |
   | Issm | — | — |
   | Isso | — | — |
   | Sca | — | — |
   | SystemOwner | — | — |
   | **MissionOwner** | **(name)** | **Inherited** |
   | Administrator | — | — |

4. Click "Override" on the MissionOwner row. The shared `AssignRoleDialog` opens, pre-selected to MissionOwner.
5. Pick a different person; submit.
6. Row flips to `Source: Override` with the new assignee.
7. Click "Remove override". Row reverts to `Inherited` with the original Org-level person.

---

## 6. User Story 3 walkthrough — RBAC denial (FR-027 / SC-009)

**Goal (US3)**: a caller with role `Isso` cannot assign `AuthorizingOfficial`.

```bash
# Toggle the mock-auth dev cookie to impersonate an Isso-only user:
curl -X POST http://localhost:5294/dev/auth/impersonate \
  -H 'Content-Type: application/json' \
  -d '{"email":"isso@acme.test","effectiveRole":"Isso"}'
```

Reload the Dashboard. The "Assign" button on the AuthorizingOfficial row is greyed out (client-side affordance hide). To prove server-side enforcement, send the request directly:

```bash
curl -i -X POST http://localhost:5294/api/roles/organization \
  -H 'Content-Type: application/json' \
  --cookie-jar /tmp/cookies.txt --cookie /tmp/cookies.txt \
  -d '{"role":"AuthorizingOfficial","personId":"...","isPrimary":true}'
```

Expected response:

```http
HTTP/1.1 403 Forbidden
{
  "status": "error",
  "error": {
    "code": "RBAC_ROLE_ASSIGN_DENIED",
    "message": "Caller's effective role 'Isso' is not authorized to assign target role 'AuthorizingOfficial'.",
    "callerEffectiveRole": "Isso",
    "targetRole": "AuthorizingOfficial"
  }
}
```

---

## 7. User Story 4 walkthrough — legacy endpoint deprecation + write-through atomicity (FR-018 / FR-019 / SC-004 / SC-010)

**Goal (US4)**: the legacy `/api/dashboard/systems/{systemId}/roles` endpoint stays open during the 90-day window, emits deprecation headers, and writes atomically to both the legacy and unified tables.

```bash
SYSTEM_ID=$(./scripts/seed-systems.sh --print-system-id)
curl -i -X POST "http://localhost:5294/api/dashboard/systems/$SYSTEM_ID/roles" \
  -H 'Content-Type: application/json' \
  -d '{"role":"Issm","personId":"..."}'
```

Expected headers on the response:

```http
HTTP/1.1 200 OK
Deprecation: true
Sunset: <RFC 7231 date 90 days after LaunchDate>
Link: </api/roles/system/{systemId}>; rel="successor-version"
```

Verify both tables received the row:

```bash
# Inside the SQL container:
docker compose -f docker-compose.mcp.yml exec sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P 'YourStrong!Pass' -C -d AtoCopilot -Q \
  "select 'legacy' as src, count(*) from RmfRoleAssignments where SystemId='$SYSTEM_ID' and Role=1
   union all
   select 'unified', count(*) from SystemRoleAssignments where RegisteredSystemId='$SYSTEM_ID' and Role=0 and IsInherited=0"
```

Both counts MUST equal 1 (the write-through invariant from FR-018).

For atomicity rollback (SC-010), the manual test script invokes the same endpoint with `Ato-Copilot-Fault-Injection: rollback-unified` header. The integration test `LegacyWriteThroughAtomicityTests` does the same; row counts in both tables MUST remain equal across 1,000 fault-injected calls.

---

## 8. Telemetry verification

Tail the MCP container logs and look for:

- `legacy_role_endpoint_call_total` increments on every legacy POST / DELETE / GET.
- `org_role_propagation_duration_seconds` histogram emits per worker iteration with `systems_bucket` label.
- `sod_violation_warning_total` increments when the SoD pairs match.

```bash
docker compose -f docker-compose.mcp.yml logs ato-copilot-mcp --tail=200 | grep -E 'legacy_role_endpoint|org_role_propagation|sod_violation'
```

Per Constitution observability requirements, all four instruments are sourced from the shared `Meter("Ato.Copilot")` and surface through whatever exporter the container is configured with (defaults to console/OTLP in dev).

---

## 9. Manual test script handoff

The manual end-to-end script for Feature 049 lives at:

```text
docs/persona-test-cases/feature-049-unified-rmf-role-assignments.md
```

It enumerates US1–US4 with the same shell steps as this quickstart, plus the persona-by-persona walkthroughs required by Feature 020 conventions.

---

## Tear-down

```bash
docker compose -f docker-compose.mcp.yml down -v
```

`-v` removes the SQL volume — required if you want a pristine `OrganizationRole` enum-extension test, because EF Core's `EnsureCreatedAsync` only runs on an empty database.
