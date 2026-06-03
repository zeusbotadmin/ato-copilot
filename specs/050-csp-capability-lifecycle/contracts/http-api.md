# Phase 1 — HTTP API Contract: CSP-Inherited Capability Lifecycle

**Feature**: 050-csp-capability-lifecycle
**Plan**: [../plan.md](../plan.md)
**Data model**: [../data-model.md](../data-model.md)
**Spec**: [../spec.md](../spec.md)
**Date**: 2026-05-22

This document pins the wire contract for three HTTP touchpoints. All
endpoints live under the existing
`/api/csp/inherited-components/{componentId}` group registered in
[CspInheritedComponentEndpoints.cs](../../../src/Ato.Copilot.Mcp/Endpoints/Csp/CspInheritedComponentEndpoints.cs)
and reuse the same response envelope helpers (`Success(sw, ...)`,
`ValidationError(sw, ...)`, `NotFound(sw)`, `ForbiddenNotCspAdmin(sw)`,
`Error(412, code, message)`).

| # | Method | Path | Status |
|---|---|---|---|
| 1 | `POST` | `/api/csp/inherited-components/{componentId}/capabilities` | **EXTENDED** (existing endpoint; new optional body field) |
| 2 | `POST` | `/api/csp/inherited-components/{componentId}/capabilities/{capabilityId}/move` | **NEW** |
| 3 | `GET` | `/api/csp/inherited-components/{componentId}/capabilities/{capabilityId}/history` | **NEW** |

All three endpoints require:

- Authenticated principal (existing `[Authorize]` policy on the group).
- `tenantCtx.IsCspAdmin == true`. Failure → `ForbiddenNotCspAdmin(sw)`
  (HTTP 403 with envelope `{ code: "FORBIDDEN_NOT_CSP_ADMIN", ... }`).
- Single-tenant deployment short-circuit (`ShouldShortCircuitSingleTenant`)
  applies; failure → 503 envelope.

---

## 1. `POST .../capabilities` — Extended (FR-001)

### 1.1 What changes

The existing endpoint already accepts a JSON body with `name`,
`description`, `mappedNistControlIds`. This feature adds **one** optional
field: `markMappedImmediately` (bool, default `false`).

### 1.2 Request

`POST /api/csp/inherited-components/{componentId}/capabilities`
`Content-Type: application/json`
`Authorization: Bearer ...` (CSP-Admin role required)

```json
{
  "name": "Tenant-level RBAC enforcement",
  "description": "Azure RBAC role assignments...",
  "mappedNistControlIds": ["AC-2", "AC-2(1)"],
  "markMappedImmediately": false
}
```

#### 1.2.1 Request schema

| Field | Type | Required | Default | Notes |
|---|---|---|---|---|
| `name` | string | yes | — | 1–256 chars after `Trim()`. |
| `description` | string | yes | — | 1–2000 chars after `Trim()`. |
| `mappedNistControlIds` | string[] | yes | — | Non-empty; values validated against the NIST 800-53 control catalog server-side (existing behavior). |
| `markMappedImmediately` | bool | no | `false` | **NEW**. If `true`, capability is created with `Status = Mapped`, `ReviewedBy = <caller oid>`, `ReviewedAt = now`, `ReviewerNote = "Mapped on create by creator."` |

### 1.3 Behavior

The default behavior (no `markMappedImmediately`, or `markMappedImmediately = false`) is **the new default**: capabilities are created with `Status = NeedsReview` and **not** auto-mapped (FR-001). This is a behavior change from prior pre-050 builds where manually-added capabilities defaulted to `Mapped`.

When `markMappedImmediately = true`:

1. Inside the same transaction:
   1. Insert capability with `Status = NeedsReview`, `MappedBy = User`, `CreatedBy = <caller oid>`.
   2. Update the just-inserted capability to `Status = Mapped`, `ReviewedBy = <caller oid>`, `ReviewedAt = now`, `ReviewerNote = "Mapped on create by creator."`.
   3. Insert **two** `CapabilityHistoryEvent` rows (one `Created` with metadata `{ markedMappedImmediately: true }`, one `Reviewed` with metadata `{ reviewerNote: "Mapped on create by creator." }`).
2. Commit.

When `markMappedImmediately = false` (or absent):

1. Inside the same transaction:
   1. Insert capability with `Status = NeedsReview`, `MappedBy = User`, `CreatedBy = <caller oid>`.
   2. Insert **one** `CapabilityHistoryEvent` row (`Created` with metadata `null`).
2. Commit.

### 1.4 Response — 200 OK

```json
{
  "ok": true,
  "data": {
    "id": "a1b2c3d4-...",
    "name": "Tenant-level RBAC enforcement",
    "description": "Azure RBAC role assignments...",
    "mappedNistControlIds": ["AC-2", "AC-2(1)"],
    "mappingConfidence": null,
    "status": "NeedsReview",
    "mappingFailureReason": null,
    "mappedBy": "User",
    "createdAt": "2026-05-22T14:32:11.420Z",
    "createdBy": "user-oid",
    "reviewedAt": null,
    "reviewedBy": null,
    "reviewerNote": null,
    "rowVersion": "AAAAAAAAB+E="
  },
  "elapsedMs": 23
}
```

When `markMappedImmediately = true`:

```json
{
  "ok": true,
  "data": {
    "...": "...",
    "status": "Mapped",
    "reviewedAt": "2026-05-22T14:32:11.450Z",
    "reviewedBy": "user-oid",
    "reviewerNote": "Mapped on create by creator.",
    "rowVersion": "AAAAAAAAB+I="
  }
}
```

### 1.5 Error responses

| Status | Code | Trigger |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Missing/empty `name`, `description`, or `mappedNistControlIds`; unknown NIST control id. |
| 403 | `FORBIDDEN_NOT_CSP_ADMIN` | Caller is not CSP-Admin. |
| 404 | `NOT_FOUND` | `componentId` does not exist in caller's tenant OR exists but is Archived. (Existence-leak guard.) |
| 503 | `MULTI_TENANT_DISABLED` | Single-tenant deployment short-circuit. |

### 1.6 Back-compat

A pre-050 caller that omits `markMappedImmediately` gets the **new
default** (`Status = NeedsReview`). This is a deliberate breaking
behavior change documented in the spec (FR-001). The dashboard's
"Add Capability" form will surface the new override checkbox; CLI / MCP
callers that relied on auto-mapping MUST be updated to pass
`markMappedImmediately: true`.

---

## 2. `POST .../capabilities/{capabilityId}/move` — NEW (FR-002, FR-012)

### 2.1 Purpose

Reparent a capability from its current component to a different,
non-archived component **in the same tenant**. Resets the capability's
review status. Writes one `Moved` history event. Optimistic concurrency
via `If-Match`.

### 2.2 Request

`POST /api/csp/inherited-components/{componentId}/capabilities/{capabilityId}/move`
`Content-Type: application/json`
`If-Match: AAAAAAAAB+E=` (base64-encoded current `RowVersion`)
`Authorization: Bearer ...` (CSP-Admin role required)

```json
{ "targetComponentId": "f6e5d4c3-..." }
```

#### 2.2.1 Request schema

| Field | Type | Required | Notes |
|---|---|---|---|
| `targetComponentId` | Guid | yes | Must be ≠ `componentId`. Must exist in caller's tenant. Must not be `Archived`. |

#### 2.2.2 Headers

| Header | Required | Notes |
|---|---|---|
| `If-Match` | yes | Base64-encoded current `RowVersion` of the capability. Absent or unparsable → `VALIDATION_ERROR`. Mismatched → 412. |

### 2.3 Behavior

1. Resolve caller's tenant and assert CSP-Admin. Fail → 403 or 503.
2. Validate `If-Match` is present and base64-decodable. Fail →
   `VALIDATION_ERROR` (HTTP 400). The header is **required** for this
   endpoint (unlike `UpdateCapabilityAsync` which falls through to
   last-write-wins).
3. Open a transaction.
4. Load the target component (`targetComponentId`) scoped to caller's
   tenant. If missing, archived, or in a different tenant → return
   `NotFound(sw)` (HTTP 404). The "different tenant" case is treated as
   404 **not** 403 per spec edge-case ("cross-tenant move is not
   allowed; caller sees 404"), to avoid leaking existence.
5. Load the capability scoped to caller's tenant. If missing → 404.
6. If `targetComponentId == capability.CspInheritedComponentId` →
   `VALIDATION_ERROR` (HTTP 400) with message
   `"Target component is the capability's current component."`.
7. UPDATE capability:
   - `CspInheritedComponentId = targetComponentId`
   - `Status = NeedsReview`
   - `ReviewedBy = null`, `ReviewedAt = null`, `ReviewerNote = null`
     (the move invalidates the prior review)
   - `MappingFailureReason = "Moved to a new component; re-review required."`
   - `RowVersion` is bumped by EF Core
   - SQL `WHERE Id = ... AND RowVersion = <ifMatch>`
8. If `SaveChangesAsync` reports 0 rows affected
   (`DbUpdateConcurrencyException`) → 412
   `ROW_VERSION_MISMATCH`.
9. INSERT `CapabilityHistoryEvent`:
   - `EventType = Moved`
   - `Summary = "Moved from <fromComponentName> to <toComponentName>."`
   - `MetadataJson = { fromComponentId, toComponentId }` (no names —
     names can drift; ids are stable)
10. Commit.
11. Return 200 with the updated capability DTO (same shape as
    `UpdateCapabilityAsync` returns).

**Field preservation rule** (FR-002):

The following fields MUST be preserved verbatim across the move:
`Id`, `Name`, `Description`, `MappedNistControlIds`,
`MappingConfidence`, `MappedBy`, `CreatedAt`, `CreatedBy`.

### 2.4 Response — 200 OK

```json
{
  "ok": true,
  "data": {
    "id": "a1b2c3d4-...",
    "cspInheritedComponentId": "f6e5d4c3-...",
    "name": "Tenant-level RBAC enforcement",
    "description": "...",
    "mappedNistControlIds": ["AC-2", "AC-2(1)"],
    "mappingConfidence": 0.87,
    "status": "NeedsReview",
    "mappingFailureReason": "Moved to a new component; re-review required.",
    "mappedBy": "AI",
    "createdAt": "2026-04-10T08:11:02.000Z",
    "createdBy": "system",
    "reviewedAt": null,
    "reviewedBy": null,
    "reviewerNote": null,
    "rowVersion": "AAAAAAAACAA="
  },
  "elapsedMs": 31
}
```

### 2.5 Error responses

| Status | Code | Trigger |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Missing `targetComponentId`; `If-Match` header missing or unparsable; `targetComponentId == componentId`. |
| 403 | `FORBIDDEN_NOT_CSP_ADMIN` | Caller is not CSP-Admin. |
| 404 | `NOT_FOUND` | `componentId` not found / archived in caller's tenant; OR `capabilityId` not found in caller's tenant; OR `targetComponentId` not found / archived in caller's tenant; OR `targetComponentId` belongs to a different tenant (existence-leak guard). |
| 412 | `ROW_VERSION_MISMATCH` | Capability was modified by another user; reload and retry. |
| 503 | `MULTI_TENANT_DISABLED` | Single-tenant deployment short-circuit. |

### 2.6 Idempotency

This endpoint is **not idempotent**. A repeat call with the same
`If-Match` header will 412 on the second attempt (RowVersion has
changed). A repeat call with the freshly returned `rowVersion` against
the **same** `targetComponentId` will succeed and emit another `Moved`
event with `fromComponentId == toComponentId`, which the service
rejects per § 2.3 step 6 — so in practice the second call is a 400.

### 2.7 OpenAPI fragment

```yaml
post:
  operationId: ReparentCapability
  summary: Reparent a capability to a different CSP-inherited component.
  parameters:
    - in: path
      name: componentId
      required: true
      schema: { type: string, format: uuid }
    - in: path
      name: capabilityId
      required: true
      schema: { type: string, format: uuid }
    - in: header
      name: If-Match
      required: true
      schema: { type: string }
      description: Base64-encoded current RowVersion of the capability.
  requestBody:
    required: true
    content:
      application/json:
        schema:
          type: object
          required: [targetComponentId]
          properties:
            targetComponentId: { type: string, format: uuid }
  responses:
    '200': { $ref: '#/components/responses/CapabilityDto' }
    '400': { $ref: '#/components/responses/ValidationError' }
    '403': { $ref: '#/components/responses/Forbidden' }
    '404': { $ref: '#/components/responses/NotFound' }
    '412': { $ref: '#/components/responses/RowVersionMismatch' }
    '503': { $ref: '#/components/responses/MultiTenantDisabled' }
```

---

## 3. `GET .../capabilities/{capabilityId}/history` — NEW (FR-004, FR-005, FR-014)

### 3.1 Purpose

Return a paginated, reverse-chronological list of audit events for one
capability scoped to the caller's tenant.

### 3.2 Request

`GET /api/csp/inherited-components/{componentId}/capabilities/{capabilityId}/history?page=1&pageSize=50`
`Authorization: Bearer ...` (CSP-Admin role required)

#### 3.2.1 Query parameters

| Param | Type | Default | Constraint |
|---|---|---|---|
| `page` | int | `1` | `Math.Max(1, page ?? 1)` |
| `pageSize` | int | `50` | `Math.Clamp(pageSize ?? 50, 1, 200)` |

Out-of-range values are silently clamped (not rejected), matching the
existing CSP list endpoints' behavior verified in
[CspInheritedComponentEndpoints.cs:121](../../../src/Ato.Copilot.Mcp/Endpoints/Csp/CspInheritedComponentEndpoints.cs).

### 3.3 Behavior

1. Resolve caller's tenant and assert CSP-Admin. Fail → 403 or 503.
2. Verify the capability exists in caller's tenant. If not → 404
   (existence-leak guard).
3. Query `CapabilityHistoryEvents` filtered by
   `TenantId == caller.tenantId AND CapabilityId == capabilityId`,
   ordered by `OccurredAt DESC, Id DESC` (the secondary sort makes
   pagination stable when two events share the same timestamp).
4. `total = COUNT(*)`; `items = ToList(skip, take)`.
5. Empty result → return 200 with `items: []` (NOT 404).

### 3.4 Response — 200 OK

```json
{
  "ok": true,
  "data": {
    "items": [
      {
        "id": "e9f0a1b2-...",
        "eventType": "Moved",
        "actorOid": "user-oid",
        "occurredAt": "2026-05-22T14:50:11.420Z",
        "summary": "Moved from 'Compute' to 'Identity'.",
        "metadata": {
          "fromComponentId": "a1b2c3d4-...",
          "toComponentId": "f6e5d4c3-..."
        }
      },
      {
        "id": "d8e9f0a1-...",
        "eventType": "Reviewed",
        "actorOid": "user-oid",
        "occurredAt": "2026-05-22T14:32:11.450Z",
        "summary": "Reviewed and approved.",
        "metadata": {
          "reviewerNote": "Mapped on create by creator."
        }
      },
      {
        "id": "c7d8e9f0-...",
        "eventType": "Created",
        "actorOid": "user-oid",
        "occurredAt": "2026-05-22T14:32:11.420Z",
        "summary": "Capability manually created.",
        "metadata": {
          "markedMappedImmediately": true
        }
      }
    ],
    "page": 1,
    "pageSize": 50,
    "total": 3
  },
  "elapsedMs": 18
}
```

#### 3.4.1 Item schema

| Field | Type | Notes |
|---|---|---|
| `id` | Guid | Event row id. |
| `eventType` | string | One of `Created`, `Edited`, `Reviewed`, `Moved`, `Archived`, `Unarchived`. |
| `actorOid` | string | OID claim of the user who triggered the event. |
| `occurredAt` | ISO-8601 UTC | Server-side write timestamp. |
| `summary` | string | Human-readable description (≤ 500 chars). |
| `metadata` | object \| null | Structured payload per event type — see data-model.md § 1.4. **Parsed from JSON server-side** and returned as an object, not a string. |

### 3.5 Error responses

| Status | Code | Trigger |
|---|---|---|
| 403 | `FORBIDDEN_NOT_CSP_ADMIN` | Caller is not CSP-Admin. |
| 404 | `NOT_FOUND` | `componentId` not found / archived in caller's tenant; OR `capabilityId` not found in caller's tenant. |
| 503 | `MULTI_TENANT_DISABLED` | Single-tenant deployment short-circuit. |

**Empty history is NOT a 404.** The capability exists; it just has no
recorded events yet. The endpoint returns 200 with `items: []`.

### 3.6 Caching

The endpoint sets `Cache-Control: no-store`. History rows can be
written between two calls; clients must re-fetch on each open of the
drawer's History tab.

### 3.7 OpenAPI fragment

```yaml
get:
  operationId: ListCapabilityHistory
  summary: List audit-trail events for a CSP-inherited capability.
  parameters:
    - in: path
      name: componentId
      required: true
      schema: { type: string, format: uuid }
    - in: path
      name: capabilityId
      required: true
      schema: { type: string, format: uuid }
    - in: query
      name: page
      schema: { type: integer, minimum: 1, default: 1 }
    - in: query
      name: pageSize
      schema: { type: integer, minimum: 1, maximum: 200, default: 50 }
  responses:
    '200':
      content:
        application/json:
          schema:
            type: object
            required: [ok, data, elapsedMs]
            properties:
              ok: { type: boolean, const: true }
              elapsedMs: { type: integer }
              data:
                type: object
                required: [items, page, pageSize, total]
                properties:
                  page: { type: integer }
                  pageSize: { type: integer }
                  total: { type: integer }
                  items:
                    type: array
                    items:
                      type: object
                      required: [id, eventType, actorOid, occurredAt, summary]
                      properties:
                        id: { type: string, format: uuid }
                        eventType:
                          type: string
                          enum: [Created, Edited, Reviewed, Moved, Archived, Unarchived]
                        actorOid: { type: string }
                        occurredAt: { type: string, format: date-time }
                        summary: { type: string, maxLength: 500 }
                        metadata: { type: object, nullable: true, additionalProperties: true }
    '403': { $ref: '#/components/responses/Forbidden' }
    '404': { $ref: '#/components/responses/NotFound' }
    '503': { $ref: '#/components/responses/MultiTenantDisabled' }
```

---

## 4. Envelope reference

All three endpoints use the existing MCP success envelope:

```json
{
  "ok": true,
  "data": <endpoint-specific>,
  "elapsedMs": <int>
}
```

And the existing error envelope:

```json
{
  "ok": false,
  "code": "<UPPER_SNAKE_CASE>",
  "message": "<human-readable>",
  "elapsedMs": <int>
}
```

The `code` values introduced or reused by this feature:

| Code | Status | Source |
|---|---|---|
| `VALIDATION_ERROR` | 400 | Existing helper `ValidationError(sw, msg)`. |
| `FORBIDDEN_NOT_CSP_ADMIN` | 403 | Existing helper `ForbiddenNotCspAdmin(sw)`. |
| `NOT_FOUND` | 404 | Existing helper `NotFound(sw)`. |
| `ROW_VERSION_MISMATCH` | 412 | Existing pattern (`PatchAsync`, `UpdateCapabilityAsync`). |
| `MULTI_TENANT_DISABLED` | 503 | Existing helper `ShouldShortCircuitSingleTenant`. |

**No new error codes are introduced by this feature.**

---

## 5. Cross-reference matrix

| FR | Endpoint(s) | Section |
|---|---|---|
| FR-001 (manual-add default) | § 1 | § 1.3 |
| FR-002 (reparent endpoint) | § 2 | § 2 (whole) |
| FR-005 (history visible) | § 3 | § 3 (whole) |
| FR-012 (optimistic concurrency) | § 2 | § 2.3 step 7–8 |
| FR-013 (tenant isolation) | §§ 1, 2, 3 | every endpoint asserts tenant scope before any other check |
| FR-014 (history pagination contract) | § 3 | § 3.2.1, § 3.3 step 5 |
| FR-016 (Remap audit semantics) | n/a (internal — see internal-services.md) | — |
