# Phase 1: HTTP API Contracts — Unified RMF Role Assignments

**Branch**: `049-unified-rmf-role-assignments`
**Date**: 2026-05-19

This document defines every HTTP contract changed or added by this feature. All endpoints follow the existing MCP envelope schema (Constitution § UX Standards): `{ status, data, metadata, warnings?, error? }`.

---

## Endpoint Inventory

| Method | Path | Source | Change |
|---|---|---|---|
| GET | `/api/dashboard/systems/{systemId}/roles` | Legacy ([DashboardEndpoints.cs:247-330](../../../src/Ato.Copilot.Mcp/Endpoints/DashboardEndpoints.cs)) | MODIFY — add `Deprecation` + `Sunset` headers, increment `legacy_role_endpoint_call_total` |
| POST | `/api/dashboard/systems/{systemId}/roles` | Legacy | MODIFY — atomic write-through to unified model; deprecation headers; counters |
| DELETE | `/api/dashboard/systems/{systemId}/roles/{role}/{personId}` | Legacy | MODIFY — atomic delete-through; deprecation headers; counters |
| POST | `/api/roles/system/{systemId}` | NEW | Unified per-system role write surface (FR-027 RBAC enforced) |
| DELETE | `/api/roles/system/{systemId}/{role}/{personId}` | NEW | Unified per-system override removal |
| GET | `/api/roles/system/{systemId}` | NEW | Unified per-system role read (returns full 7-role state with inherited indicators) |
| POST | `/api/roles/organization` | NEW | Unified Org-level role write (delegates to existing `OrganizationRoleAssignmentService` with RBAC + SoD + fan-out enqueue wired in) |
| DELETE | `/api/roles/organization/{role}/{personId}` | NEW | Unified Org-level role removal (cascades soft-removal to inherited rows per FR-007) |

Note: the legacy `GET` and `DELETE` paths on `/api/dashboard/systems/{systemId}/roles` already exist; the **column-by-column behavior** is unchanged for callers — only the response headers and the side-effect counters are new.

---

## Legacy Endpoints — modifications

### `POST /api/dashboard/systems/{systemId}/roles`

**Path params**: `systemId` (string GUID)
**Request body** (unchanged from current):

```json
{
  "role": "MissionOwner",
  "personId": "11111111-2222-3333-4444-555555555555"
}
```

**Authentication**: any authenticated tenant member (unchanged during 90-day deprecation window per FR-018).

**Response 200 OK** (envelope; same shape as before, with new `warnings` array):

```json
{
  "status": "success",
  "data": {
    "id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    "systemId": "...",
    "role": "MissionOwner",
    "personId": "11111111-...",
    "createdAt": "2026-05-19T15:42:00Z"
  },
  "metadata": {
    "toolName": "DashboardEndpoints.UpsertRole",
    "executionTimeMs": 28,
    "timestamp": "2026-05-19T15:42:00Z"
  },
  "warnings": [
    {
      "code": "SOD_VIOLATION",
      "message": "Person 'Jane Doe' already holds the ISSM role for this tenant. Assigning to AuthorizingOfficial creates a separation-of-duties conflict.",
      "roleConflict": ["Issm", "AuthorizingOfficial"],
      "dodiReference": "DoDI 8510.01 Enclosure 3 § 4.b",
      "suggestedAction": "Reassign one of the conflicting roles to a different person, or document the deviation per RMF tailoring guidance."
    }
  ]
}
```

The `warnings` array is **omitted** when empty (NOT `[]` — the property is absent entirely to keep envelopes lean).

**Response 400 Bad Request** — invalid role string (the response that motivated FR-012):

```json
{
  "status": "error",
  "error": {
    "code": "INVALID_ROLE",
    "message": "Unknown role 'misionowner'.",
    "suggestion": "Valid values: AuthorizingOfficial, Issm, Isso, Sca, SystemOwner, MissionOwner, Administrator."
  },
  "metadata": { ... }
}
```

All 7 valid values MUST appear in the `suggestion` string (FR-012, SC-007).

**Response 5xx** — atomicity failure path (FR-018, SC-010): when the `SaveChangesAsync` rolls back because either the legacy `Add` or the unified `Add` failed, the endpoint returns `503 Service Unavailable` with envelope error code `ROLE_WRITE_THROUGH_FAILED`. **No reader can observe either row.** The integration test `LegacyWriteThroughAtomicityTests` verifies that pre-fault and post-fault row counts in all three role-assignment tables are equal.

**Response headers** (NEW on every response, including 4xx and 5xx):

```http
Deprecation: true
Sunset: Wed, 17 Aug 2026 00:00:00 GMT
Link: </api/roles/system/{systemId}>; rel="successor-version"
```

The `Sunset` date is `LaunchDate + 90 days` per FR-019 / [Clarifications Q1](../spec.md#session-2026-05-19). Format is RFC 7231 IMF-fixdate.

**Side effects** (in addition to existing legacy-row write):

1. Increment `legacy_role_endpoint_call_total{tenant_id=..., method=POST}` (FR-019).
2. Stage the equivalent `SystemRoleAssignment` row (`IsInherited=false`, override semantics) on the same `DbContext`; commit with the same `SaveChangesAsync` (FR-018).
3. If the write would have been denied under FR-027's matrix had it been the new endpoint, increment `legacy_role_endpoint_bypass_total{tenant_id=..., target_role=...}` (FR-018).
4. Invoke `ISoDConflictDetector.DetectAsync` in the same transaction; any warnings flow into the response envelope's `warnings` array.

### `DELETE /api/dashboard/systems/{systemId}/roles/{role}/{personId}`

Behavior mirror image of POST: atomic delete from both `RmfRoleAssignment` and `SystemRoleAssignment` (the per-system row, if any, with `IsInherited=false`). Deprecation headers emitted. The unified inherited row (if any) is **not** touched — it belongs to the Org-level row.

### `GET /api/dashboard/systems/{systemId}/roles`

Response shape unchanged. Behavior unchanged (still reads legacy table). Deprecation headers emitted and `legacy_role_endpoint_call_total` incremented on every call. The dashboard client is migrated to the new `GET /api/roles/system/{systemId}` in US2; the legacy GET continues to serve unmodified callers (MCP tool, external integrations).

---

## New Unified Endpoints

### `POST /api/roles/system/{systemId}` — create / update a per-system override

**Path params**: `systemId` (string GUID)
**Request body**:

```json
{
  "role": "MissionOwner",
  "personId": "11111111-2222-3333-4444-555555555555"
}
```

**Authentication**: authenticated tenant member.
**Authorization**: FR-027 matrix evaluated server-side. Caller's effective role is the highest `RmfRole` they hold for the tenant.

**Response 200 OK** — same envelope shape as the legacy POST. The created `SystemRoleAssignment` row has `IsInherited=false`, `SourceOrganizationRoleAssignmentId=null`.

**Response 403 Forbidden** — FR-027 denial:

```json
{
  "status": "error",
  "error": {
    "code": "RBAC_ROLE_ASSIGN_DENIED",
    "message": "Caller's effective role 'Isso' is not authorized to assign target role 'AuthorizingOfficial'.",
    "callerEffectiveRole": "Isso",
    "targetRole": "AuthorizingOfficial"
  },
  "metadata": { ... }
}
```

The 403 response is what the SC-009 generator-based test asserts on every disallowed cell.

**Response 400 Bad Request** — same shape as legacy invalid-role response (FR-012).

**Response headers**: none of the deprecation headers; this is the new surface.

**Side effects**:

1. Single-row `SystemRoleAssignment` insert with `IsInherited=false`.
2. SoD detection in the same transaction; warnings in response envelope.
3. Increment `sod_violation_warning_total{...}` per FR-026 audit-counter.

### `DELETE /api/roles/system/{systemId}/{role}/{personId}` — remove a per-system override

**Authorization**: same FR-027 matrix evaluated for the role being removed.

**Response 200 OK** — `SystemRoleAssignment.RemovedAt` set to now; row's `IsInherited=false` precondition checked (you can't `DELETE` an inherited row via this endpoint — to do that, soft-remove the Org-level row).

**Response 409 Conflict** — when the targeted row is inherited (`IsInherited=true`):

```json
{
  "status": "error",
  "error": {
    "code": "ROLE_INHERITED_NOT_REMOVABLE",
    "message": "The MissionOwner assignment for this system is inherited from the organization. To remove it, soft-remove the organization-level assignment instead.",
    "orgRoleId": "ffffffff-..."
  }
}
```

After removal, the next `GET /api/roles/system/{systemId}` returns the inherited-or-Org-fallback row (FR-011).

### `GET /api/roles/system/{systemId}` — read full per-system role state

**Response 200 OK**:

```json
{
  "status": "success",
  "data": {
    "systemId": "...",
    "roles": [
      { "role": "AuthorizingOfficial", "person": { "id": "...", "displayName": "Alice Adams" }, "source": "inherited", "orgRoleId": "..." },
      { "role": "Issm",                "person": { "id": "...", "displayName": "Bob Baker"   }, "source": "override" },
      { "role": "Isso",                "person": { "id": "...", "displayName": "Carol Chen"  }, "source": "inherited", "orgRoleId": "..." },
      { "role": "Sca",                 "person": null, "source": "not-assigned" },
      { "role": "SystemOwner",         "person": { "id": "...", "displayName": "Dan Davis"   }, "source": "legacy" },
      { "role": "MissionOwner",        "person": { "id": "...", "displayName": "Eve Evans"   }, "source": "inherited", "orgRoleId": "..." },
      { "role": "Administrator",       "person": { "id": "...", "displayName": "Frank Foley" }, "source": "inherited", "orgRoleId": "..." }
    ]
  },
  "metadata": { ... }
}
```

`source` enum: `"override" | "inherited" | "org-fallback" | "legacy" | "not-assigned"`. The `org-fallback` value signals an Org-level row exists but no inherited `SystemRoleAssignment` row has been materialized yet (worker has not run for this system). The Dashboard UI treats `inherited` and `org-fallback` identically for display — the only difference is whether an "Edit" affordance exists for converting to an override (only `inherited` shows it; `org-fallback` shows "Pending" with a tooltip).

**Authorization**: authenticated tenant member (read-only; the FR-027 matrix governs writes, not reads).

### `POST /api/roles/organization` — create / update an Org-level role

**Request body**:

```json
{
  "role": "MissionOwner",
  "personId": "...",
  "isPrimary": true
}
```

**Authorization**: FR-027 matrix.

**Response 200 OK**:

```json
{
  "status": "success",
  "data": {
    "id": "ffffffff-...",
    "tenantId": "...",
    "role": "MissionOwner",
    "personId": "...",
    "isPrimary": true,
    "createdAt": "2026-05-19T15:42:00Z"
  },
  "metadata": { ... }
}
```

**Side effects**:

1. Persist the new `OrganizationRoleAssignment` row.
2. SoD detection; warnings in envelope.
3. **Enqueue a `PropagationIntent` on `IOrganizationRoleFanoutQueue`** (FR-028). The endpoint returns immediately without waiting for fan-out completion. Banner clearing for end-users is already observable from step 3 of the precedence chain in [data-model.md § Read-time precedence](../data-model.md#read-time-precedence-fr-003-encoded-by-iunifiedrolereader).

### `DELETE /api/roles/organization/{role}/{personId}` — soft-remove an Org-level role

**Authorization**: FR-027 matrix.

**Side effects**:

1. Soft-remove the `OrganizationRoleAssignment` row (set `RemovedAt`).
2. Soft-remove all `SystemRoleAssignment` rows with `IsInherited=true` and matching `SourceOrganizationRoleAssignmentId` (FR-007). Per-system override rows (`IsInherited=false`) are preserved.
3. The cascade is performed inline in the same `DbContext` transaction (it's deterministic and tenant-bounded; no worker enqueue needed). For a tenant with >1000 systems, this is bounded by an index seek on `SourceOrganizationRoleAssignmentId`.

---

## Common Envelope Fields

### `warnings[]` shape (new array, FR-026)

```ts
type Warning = {
  code: 'SOD_VIOLATION';                    // closed enum for now
  message: string;                          // human-readable
  roleConflict: [RmfRole, RmfRole];         // the two conflicting roles
  dodiReference: string;                    // e.g. "DoDI 8510.01 Enclosure 3 § 4.b"
  suggestedAction: string;                  // optional remediation hint
};
```

The `warnings` array is OMITTED (not `[]`) when empty.

### `error` shape (existing, extended)

```ts
type ErrorEnvelope = {
  code: string;
  message: string;
  suggestion?: string;             // for INVALID_ROLE etc.
  callerEffectiveRole?: RmfRole;   // for RBAC_ROLE_ASSIGN_DENIED
  targetRole?: RmfRole;            // for RBAC_ROLE_ASSIGN_DENIED
  orgRoleId?: string;              // for ROLE_INHERITED_NOT_REMOVABLE
};
```

New error codes introduced by this feature:

| Code | HTTP status | Endpoints |
|---|---|---|
| `RBAC_ROLE_ASSIGN_DENIED` | 403 | All new POST/DELETE under `/api/roles/*` |
| `ROLE_INHERITED_NOT_REMOVABLE` | 409 | `DELETE /api/roles/system/{systemId}/{role}/{personId}` |
| `ROLE_WRITE_THROUGH_FAILED` | 503 | Legacy `POST` / `DELETE` when atomicity rollback occurred |
| `INVALID_ROLE` | 400 | All POST endpoints (existing code, suggestion text updated per FR-012) |

---

## Endpoint × FR / SC traceability

| Endpoint | Drives FRs | Drives SCs |
|---|---|---|
| `POST /api/dashboard/systems/{systemId}/roles` (legacy) | FR-018, FR-019, FR-026 | SC-004, SC-007, SC-010 |
| `DELETE /api/dashboard/systems/{systemId}/roles/...` (legacy) | FR-018, FR-019 | SC-004, SC-010 |
| `GET /api/dashboard/systems/{systemId}/roles` (legacy) | FR-019 | SC-004 |
| `POST /api/roles/system/{systemId}` | FR-010, FR-026, FR-027 | SC-003, SC-007, SC-009 |
| `DELETE /api/roles/system/{systemId}/...` | FR-011, FR-027 | SC-009 |
| `GET /api/roles/system/{systemId}` | FR-002, FR-003, FR-004, FR-008 | SC-001, SC-002, SC-006 |
| `POST /api/roles/organization` | FR-001, FR-005, FR-006, FR-026, FR-027, FR-028 | SC-001, SC-002, SC-008, SC-009, SC-011 |
| `DELETE /api/roles/organization/...` | FR-007, FR-027 | SC-009 |
