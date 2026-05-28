# Phase 1 — Frontend Types Contract: CSP-Inherited Capability Lifecycle

**Feature**: 050-csp-capability-lifecycle
**Plan**: [../plan.md](../plan.md)
**HTTP API**: [./http-api.md](./http-api.md)
**Spec**: [../spec.md](../spec.md)
**Date**: 2026-05-22

This document pins the TypeScript types and React component prop
shapes for the dashboard side of the feature. All types live under
`src/Ato.Copilot.Dashboard/src/features/csp-inherited-components/`,
co-located with the components that consume them. **No backend code
references these types** — they are the dashboard's view of the wire
contract pinned in [http-api.md](./http-api.md).

| File | Purpose |
|---|---|
| `types/capabilityHistory.ts` | Wire types matching `GET .../history` response |
| `types/capabilityMove.ts` | Wire types matching `POST .../move` request/response |
| `MoveCapabilityDialog.tsx` | Reparent flow (Q2, Q4) |
| `CapabilityDetailDrawer.tsx` | Owns the History tab and the "Move…" button (Q4) |
| `CapabilityCreateForm.tsx` | The "+ Add Capability" form with the `markMappedImmediately` checkbox |
| `ComponentDetailDrawer.tsx` | Hosts the Advanced disclosure for Remap (FR-008) |

---

## 1. Wire types

### 1.1 `types/capabilityHistory.ts` (NEW)

```typescript
/**
 * Wire-level types for the capability history endpoint.
 * Mirrors http-api.md § 3 response shape.
 *
 * Feature 050 FR-004 / FR-005 / FR-014.
 */

export type CapabilityHistoryEventType =
  | 'Created'
  | 'Edited'
  | 'Reviewed'
  | 'Moved'
  | 'Archived'
  | 'Unarchived';

/**
 * Metadata payload — shape varies by eventType. Consumers MUST
 * pattern-match on eventType before reading fields.
 *
 * See data-model.md § 1.4 for the full shape table.
 */
export type CapabilityHistoryEventMetadata =
  | null
  | {
      // 'Moved'
      fromComponentId?: string;
      toComponentId?: string;
      // 'Created' / 'Edited' / 'Archived' (when triggered by Remap)
      remapRunId?: string;
      source?: 'Remap' | 'Import';
      // 'Created' (manual add with override)
      markedMappedImmediately?: boolean;
      // 'Reviewed'
      reviewerNote?: string;
      // 'Edited' (manual)
      fields?: ReadonlyArray<string>;
    };

export interface CapabilityHistoryEvent {
  /** Unique row id. */
  id: string;
  /** Lifecycle event category. */
  eventType: CapabilityHistoryEventType;
  /** OID claim of the user who triggered the event. */
  actorOid: string;
  /** ISO-8601 UTC timestamp the row was written server-side. */
  occurredAt: string;
  /** Human-readable description, ≤ 500 chars. */
  summary: string;
  /** Structured payload per eventType; nullable. */
  metadata: CapabilityHistoryEventMetadata;
}

/**
 * Paginated response from `GET .../history`. Mirrors the standard
 * dashboard `{ items, page, pageSize, total }` envelope.
 */
export interface CapabilityHistoryPage {
  items: ReadonlyArray<CapabilityHistoryEvent>;
  page: number;
  pageSize: number;
  total: number;
}

/**
 * Query parameters for `GET .../history`. Both fields default-and-clamp
 * server-side per http-api.md § 3.2.1; clients SHOULD still send them
 * for predictable behavior.
 */
export interface ListCapabilityHistoryParams {
  /** 1-based page index; default 1, min 1. */
  page?: number;
  /** Default 50; clamped to [1, 200] server-side. */
  pageSize?: number;
}
```

### 1.2 `types/capabilityMove.ts` (NEW)

```typescript
/**
 * Wire-level types for the capability reparent endpoint.
 * Mirrors http-api.md § 2 request/response shape.
 *
 * Feature 050 FR-002 / FR-012.
 */

import type { CspInheritedCapability } from './capability';
// ↑ existing type; not modified by this feature.

export interface ReparentCapabilityRequest {
  /** Destination component id; MUST be in caller's tenant and not Archived. */
  targetComponentId: string;
}

export interface ReparentCapabilityHeaders {
  /** Base64-encoded current RowVersion of the capability. Required. */
  'If-Match': string;
}

export interface ReparentCapabilityResponse {
  /** Updated capability DTO, identical shape to `PATCH .../capabilities/{id}`. */
  data: CspInheritedCapability;
}
```

---

## 2. Existing types touched

### 2.1 `types/capability.ts` — `CspInheritedCapability` shape

**Not changed.** The reparent endpoint returns the same DTO already
returned by `PATCH .../capabilities/{id}` and the history endpoint does
not return capability rows. The existing type covers both.

### 2.2 `types/cspComponent.ts` — component list item

**Not changed.** The move dialog consumes `ListCspInheritedComponents`
exactly as the components list page does today. The optional response
field `total` (already present) is the signal for "> 200 components"
notice rendering.

---

## 3. Component prop contracts

### 3.1 `MoveCapabilityDialog.tsx` (NEW)

```typescript
import type { CspInheritedCapability } from '../types/capability';
import type { CspInheritedComponent } from '../types/cspComponent';

export interface MoveCapabilityDialogProps {
  /** Capability being moved; provides `id`, `cspInheritedComponentId`, `rowVersion`. */
  capability: CspInheritedCapability;

  /** Fires after a successful move; parent re-fetches and closes the dialog. */
  onMoved: (next: CspInheritedCapability) => void;

  /** Fires on cancel/dismiss. */
  onCancel: () => void;
}
```

#### 3.1.1 Internal state

```typescript
interface MoveCapabilityDialogState {
  /** All non-archived components in tenant (single eager fetch, pageSize=200). */
  candidates: ReadonlyArray<CspInheritedComponent> | null;
  /** Total non-archived components in tenant (Q2 "> 200" guard). */
  candidatesTotal: number;
  /** Loading flag for the candidate fetch. */
  isLoading: boolean;
  /** Error from the candidate fetch (rendered inline). */
  candidatesError: string | null;
  /** Filter-as-you-type substring (Q2). */
  filter: string;
  /** Currently selected target component id; null until user picks. */
  selectedId: string | null;
  /** Submitting (Confirm) flag. */
  isSubmitting: boolean;
  /** Error from the move call (rendered inline + actionable: stale → reload hint). */
  submitError: { code: string; message: string } | null;
}
```

#### 3.1.2 Behavior contract (Q2, Q4)

- **Mount**: fire `GET /api/csp/inherited-components?page=1&pageSize=200&status=Published`
  (the server filter to exclude Draft + Archived; the existing endpoint
  defaults to Published for non-CSP-Admin callers but the dialog is
  always opened by a CSP-Admin, so the explicit filter is required).
- **Eligible-target filter**: exclude `capability.cspInheritedComponentId` from
  the candidate list. The "same component" target is unselectable.
- **Filter-as-you-type**: case-insensitive substring match against
  `name`. Client-side; no extra network calls.
- **"> 200 visible" notice**: render a non-blocking banner when
  `candidatesTotal > 200` informing the user that only the first 200
  components are visible and to refine the catalog if their target is
  not listed.
- **Empty post-filter state**: render an empty-list inline message
  ("No matching components"). The dialog stays open; the user can
  adjust the filter.
- **Confirm button**: disabled until `selectedId !== null`. On click,
  POST to `/api/csp/inherited-components/{currentComponentId}/capabilities/{capabilityId}/move`
  with `If-Match: capability.rowVersion` header and body
  `{ targetComponentId: selectedId }`.
- **Error handling**:
  - 412 `ROW_VERSION_MISMATCH` → render the message + a "Reload
    capability" link that calls `onCancel()` and triggers the parent
    drawer to refetch.
  - 404 `NOT_FOUND` (target archived between dialog open and confirm)
    → render the message + suggest refresh.
  - 400 `VALIDATION_ERROR` → render the message verbatim.
- **Success**: call `onMoved(updatedCapability)`; parent closes dialog
  and shows a transient success toast.

### 3.2 `CapabilityDetailDrawer.tsx` — extensions

#### 3.2.1 Move action button (Q4)

```typescript
/**
 * Renders the "Move to another component…" affordance.
 *
 * Disabled state (Q4): rendered disabled with a tooltip when no
 * eligible target component exists in tenant. The tooltip reads:
 *   "No other CSP-inherited component exists yet. Create one first."
 */
interface MoveActionProps {
  capability: CspInheritedCapability;
  /** True when at least one other non-archived component exists. */
  hasEligibleTarget: boolean;
  /** Opens the MoveCapabilityDialog. */
  onClickMove: () => void;
}
```

The `hasEligibleTarget` boolean is computed by the drawer at mount via
a lightweight `GET /api/csp/inherited-components?page=1&pageSize=2&status=Published`
fetch (whose `total` is what matters; the response items are
discarded). The drawer caches this for the session. The fetch is fired
exactly once when the drawer opens; subsequent capability views in the
same drawer instance reuse the cached value.

#### 3.2.2 History tab

```typescript
interface CapabilityHistoryTabProps {
  capability: CspInheritedCapability;
}

/**
 * Owns a paginated table view of CapabilityHistoryEvent rows for the
 * capability. Re-fetches on tab activation and on `capability.rowVersion`
 * change (so a successful Move refreshes the trail). Empty state
 * renders "No history yet." (NOT an error — empty is 200 OK per
 * http-api.md § 3.5).
 */
```

Internal state:

```typescript
interface CapabilityHistoryTabState {
  page: number;            // 1-based; default 1
  pageSize: number;        // default 50; user-selectable from {25, 50, 100, 200}
  data: CapabilityHistoryPage | null;
  isLoading: boolean;
  error: string | null;
}
```

Row rendering rules (FR-005):

| eventType | Icon | Summary | Metadata preview |
|---|---|---|---|
| `Created` | + | `summary` verbatim | If `metadata.markedMappedImmediately` → "Auto-mapped on create" pill. If `metadata.source === 'Remap'` → "Remap" pill with `remapRunId` short form. |
| `Edited` | ✎ | `summary` verbatim | `metadata.fields` rendered as comma-separated list, if present. |
| `Reviewed` | ✓ | `summary` verbatim | `metadata.reviewerNote` rendered in a quoted block, if present. |
| `Moved` | → | `summary` verbatim ("Moved from 'A' to 'B'.") | `metadata.fromComponentId` and `toComponentId` rendered as component links (lookups via tenant-cached component list). |
| `Archived` | 🗑 | `summary` verbatim | If `metadata.source === 'Remap'` → "Remap" pill. |
| `Unarchived` | ↺ | `summary` verbatim | none. |

The drawer never decodes `metadataJson` itself — the backend already
parsed it into an object per http-api.md § 3.4.1.

### 3.3 `CapabilityCreateForm.tsx` — extension (FR-001)

```typescript
interface CapabilityCreateFormProps {
  componentId: string;
  onCreated: (next: CspInheritedCapability) => void;
  onCancel: () => void;
}

interface CapabilityCreateFormState {
  name: string;
  description: string;
  mappedNistControlIds: ReadonlyArray<string>;
  /**
   * Default false. Renders as an unchecked checkbox labeled:
   * "Skip review and mark this capability Mapped now."
   * Tooltip: "Use this when you are mapping the capability as you create it."
   */
  markMappedImmediately: boolean;
  isSubmitting: boolean;
  error: string | null;
}
```

Submit payload exactly mirrors http-api.md § 1.2:

```typescript
{
  name,
  description,
  mappedNistControlIds,
  markMappedImmediately,   // ← always included; default false
}
```

### 3.4 `ComponentDetailDrawer.tsx` — Advanced disclosure (FR-008)

```typescript
interface AdvancedRemapDisclosureProps {
  component: CspInheritedComponent;
  /** Fires after the user confirms Remap from the disclosure dialog. */
  onConfirm: () => Promise<void>;
}

interface AdvancedRemapDisclosureState {
  /** Collapsed by default — chevron toggles. */
  isExpanded: boolean;
  /** Once isExpanded is true, the Remap button is enabled. */
  isConfirmDialogOpen: boolean;
  /** "I understand" checkbox state — Continue enabled when true. */
  acknowledged: boolean;
  /** Optional reviewer note attached to the Remap (passed through to history rows). */
  reviewerNote: string;
}
```

Behavior:

- Collapsed by default.
- Expanding reveals the Remap CTA + an explanatory paragraph.
- Clicking Remap opens a modal: "Re-running AI mapping will overwrite
  AI-produced capabilities and reset their NeedsReview status. User-mapped
  capabilities are preserved. Continue?" — requires the `acknowledged`
  checkbox + a Continue button click. Cancel dismisses with no action.
- On Continue, calls the existing `POST .../remap` endpoint with the
  acknowledged + reviewerNote payload. The endpoint writes the per-
  changed-capability history rows server-side per FR-016 / R11.

### 3.5 Component picker review-count chip (FR-009)

The existing component list page already renders a row per component.
This feature adds **one** new chip rendered next to the component
status pill:

```typescript
interface ComponentRowProps {
  component: CspInheritedComponent;
  /** Count of NeedsReview capabilities under this component. */
  needsReviewCount: number;
}
```

Chip rendering:

- `needsReviewCount > 0` → render a yellow chip `"N awaiting review"`.
- `needsReviewCount === 0` → render **no** chip (suppressed entirely).

The counts come from `ListCspInheritedComponents` (an extra
server-aggregated field `needsReviewCount`) — that endpoint extension
is **already** in scope of Feature 048 and is referenced here for
context only. No new wire field is introduced by Feature 050.

---

## 4. API client surface (`api.ts`)

New functions added to the dashboard's CSP-inherited components API
module:

```typescript
/**
 * GET /api/csp/inherited-components/{componentId}/capabilities/{capabilityId}/history
 * Feature 050 FR-005 / FR-014.
 */
export async function listCapabilityHistory(
  componentId: string,
  capabilityId: string,
  params: ListCapabilityHistoryParams = {},
): Promise<CapabilityHistoryPage>;

/**
 * POST /api/csp/inherited-components/{componentId}/capabilities/{capabilityId}/move
 * Feature 050 FR-002 / FR-012.
 *
 * @param ifMatch base64-encoded current rowVersion.
 */
export async function reparentCapability(
  componentId: string,
  capabilityId: string,
  body: ReparentCapabilityRequest,
  ifMatch: string,
): Promise<CspInheritedCapability>;
```

The existing `addCapability` function gains one parameter:

```typescript
/**
 * POST /api/csp/inherited-components/{componentId}/capabilities
 * Feature 050 FR-001 — `markMappedImmediately` added (defaults false).
 */
export async function addCapability(
  componentId: string,
  body: {
    name: string;
    description: string;
    mappedNistControlIds: ReadonlyArray<string>;
    markMappedImmediately?: boolean;   // ← NEW
  },
): Promise<CspInheritedCapability>;
```

All three functions throw the dashboard's existing
`ApiError` on non-2xx, propagating the `code` and `message` envelope
fields for callers to pattern-match.

---

## 5. Error code → UI mapping

The dashboard surfaces backend error codes through a shared mapping
table (already in place for other CSP endpoints). The codes touched
by this feature, all of which are pre-existing codes:

| `code` | Surface in dialog/drawer | Recovery hint |
|---|---|---|
| `VALIDATION_ERROR` | inline error below the form/dialog | render `message` verbatim |
| `FORBIDDEN_NOT_CSP_ADMIN` | non-recoverable; should not happen if the drawer is rendered | render generic "You are not authorized." (UI shouldn't have shown the action) |
| `NOT_FOUND` | inline error in dialog (target archived between open and confirm) | "Refresh and try again." |
| `ROW_VERSION_MISMATCH` | inline error in dialog | "Capability changed on the server. Reload to see the latest." link to refetch |
| `MULTI_TENANT_DISABLED` | global toast | "Multi-tenant features are disabled in this deployment." (should not occur if user has access) |

---

## 6. Accessibility (FR-005 / FR-009)

- `MoveCapabilityDialog`: focus moves into the filter textbox on open;
  Escape closes; Tab cycles inside the dialog. ARIA `role="dialog"`,
  `aria-labelledby` on the dialog title, `aria-describedby` on the
  body. Disabled "Move…" button on the drawer carries `aria-disabled`
  + a tooltip that is also a `title` attribute (so screen readers
  announce the reason).
- History rows: each row is a `role="listitem"` inside a
  `role="list"`. Timestamp is a `<time datetime="...">` element.
- Review-count chip: rendered with `aria-label="N capabilities
  awaiting review"`.

---

## 7. Testing surface (TypeScript layer; R5)

Each `.tsx` file gets a co-located test file under
`src/Ato.Copilot.Dashboard/src/__tests__/features/csp-inherited-components/`:

| Test file | Asserts |
|---|---|
| `MoveCapabilityDialog.test.tsx` | candidate fetch eager (one network call); filter-as-you-type narrows visible rows; current component excluded; "> 200" banner appears at `total > 200`; Confirm disabled until selection; Confirm sends `If-Match` header; 412 surfaces inline; success calls `onMoved`. |
| `CapabilityDetailDrawer.test.tsx` | Move button disabled with tooltip when no eligible target; enabled otherwise; History tab fetches on activation; empty history renders "No history yet."; pagination changes refire fetch; each event type renders the correct icon + metadata preview. |
| `CapabilityCreateForm.test.tsx` | `markMappedImmediately` checkbox default unchecked; payload always includes the field; UI label + tooltip present. |
| `ComponentDetailDrawer.advanced.test.tsx` | Advanced disclosure collapsed by default; expanding reveals Remap button; Continue gated on acknowledgement checkbox; Continue forwards reviewerNote to the network call. |
| `ComponentRow.chip.test.tsx` | Chip visible only when `needsReviewCount > 0`; aria-label correct. |

---

## 8. Cross-reference matrix

| FR | TS file(s) | Section |
|---|---|---|
| FR-001 (manual-add default + override) | `CapabilityCreateForm.tsx`, `api.ts` | § 3.3, § 4 |
| FR-002 (reparent endpoint client) | `MoveCapabilityDialog.tsx`, `api.ts` | § 3.1, § 4 |
| FR-003 (reparent UI + picker + disabled state) | `MoveCapabilityDialog.tsx`, `CapabilityDetailDrawer.tsx` | § 3.1, § 3.2.1 |
| FR-005 (history surface) | `CapabilityDetailDrawer.tsx` | § 3.2.2 |
| FR-008 (Advanced disclosure for Remap) | `ComponentDetailDrawer.tsx` | § 3.4 |
| FR-009 (review-count chip) | `ComponentRow` (existing) | § 3.5 |
| FR-012 (concurrency UI) | `MoveCapabilityDialog.tsx` | § 3.1.2 "412 → reload" |
| FR-014 (pagination contract on client) | `types/capabilityHistory.ts`, `api.ts` | § 1.1, § 4 |
| FR-016 (Remap audit visualization) | `CapabilityDetailDrawer.tsx` history rows | § 3.2.2 metadata-preview rules |
