# Phase 1: Frontend Type Contracts — Unified RMF Role Assignments

**Branch**: `049-unified-rmf-role-assignments`
**Date**: 2026-05-19

This document defines the TypeScript types and component contracts the dashboard introduces or modifies. Every type lives under `src/Ato.Copilot.Dashboard/src/types/` or beside its consuming component. `tsc --noEmit` parity is required (Constitution § Local Type-Checking Parity).

---

## 1. Domain types — `src/Ato.Copilot.Dashboard/src/types/roles.ts` (NEW)

```ts
export const RMF_ROLES = [
  'AuthorizingOfficial',
  'Issm',
  'Isso',
  'Sca',
  'SystemOwner',
  'MissionOwner',
  'Administrator',
] as const;

export type RmfRole = typeof RMF_ROLES[number];

export const RBAC_ASSIGNABLE_BY: Record<RmfRole, readonly RmfRole[]> = {
  Administrator:        ['AuthorizingOfficial', 'Issm', 'Isso', 'Sca', 'SystemOwner', 'MissionOwner', 'Administrator'],
  Issm:                 ['Issm', 'Isso', 'Sca', 'SystemOwner', 'MissionOwner', 'Administrator'],
  Isso:                 ['MissionOwner', 'SystemOwner'],
  AuthorizingOfficial:  [],
  Sca:                  [],
  SystemOwner:          [],
  MissionOwner:         [],
} as const;

export type RoleAssignmentSource =
  | 'not-assigned'
  | 'override'
  | 'inherited'
  | 'org-fallback'
  | 'legacy';

export type ResolvedRoleAssignment = {
  role: RmfRole;
  person: { id: string; displayName: string } | null;
  source: RoleAssignmentSource;
  orgRoleId?: string;
};

export type SystemRolesResponse = {
  systemId: string;
  roles: ResolvedRoleAssignment[];   // exactly 7, one per RmfRole
};

export type SoDWarning = {
  code: 'SOD_VIOLATION';
  message: string;
  roleConflict: [RmfRole, RmfRole];
  dodiReference: string;
  suggestedAction: string;
};

export type AssignmentResult = {
  status: 'success' | 'error';
  data?: ResolvedRoleAssignment;
  warnings?: SoDWarning[];     // omitted (undefined) when empty
  error?: {
    code: 'RBAC_ROLE_ASSIGN_DENIED'
        | 'ROLE_INHERITED_NOT_REMOVABLE'
        | 'ROLE_WRITE_THROUGH_FAILED'
        | 'INVALID_ROLE';
    message: string;
    suggestion?: string;
    callerEffectiveRole?: RmfRole;
    targetRole?: RmfRole;
    orgRoleId?: string;
  };
};
```

The client-side `RBAC_ASSIGNABLE_BY` table is for **affordance hiding only** (do not show buttons that the server would 403). The server is the only enforcement point per FR-027 / SC-009.

---

## 2. `AssignRoleDialog` — shared component (R8 from research.md)

**File**: `src/Ato.Copilot.Dashboard/src/components/roles/AssignRoleDialog.tsx` (NEW)

```ts
import type { RmfRole, AssignmentResult } from '@/types/roles';

export type AssignRoleScope =
  | { kind: 'organization' }                                   // Org-level write
  | { kind: 'system'; registeredSystemId: string };            // Per-system override write

export interface AssignRoleDialogProps {
  /** Controlled visibility. */
  open: boolean;

  /** Called when the dialog should close (Cancel, ESC, click-outside, or success). */
  onClose: () => void;

  /** Determines which API endpoint the dialog posts to. */
  scope: AssignRoleScope;

  /** Pre-selected role; if undefined, the role dropdown is enabled. */
  initialRole?: RmfRole;

  /** If true, the role dropdown is disabled (used by the mission-owner banner CTA). */
  lockRole?: boolean;

  /**
   * Caller's effective role (server-resolved on page load and passed down).
   * Drives client-side affordance hiding ONLY — the server is the source of truth.
   */
  callerEffectiveRole: RmfRole | null;

  /**
   * Bootstrap-mode flag. When true, the dialog calls the wizard's bootstrap
   * variant of the endpoint (which sets isBootstrapSession=true server-side).
   * Used ONLY by Wizard Step 2 on its first Org-role write per session.
   */
  bootstrap?: boolean;

  /** Called when the server confirms the write. */
  onAssigned: (result: AssignmentResult) => void;
}
```

**Consumers (3, all of which use the same dialog instance via the same props shape per FR-014)**:

1. Mission Owner banner (inline JSX in `pages/SystemDetail.tsx`) — opens with `scope={kind: 'organization'}`, `initialRole='MissionOwner'`, `lockRole=true`.
2. `RoleAssignmentPanel` (existing per-system card) — opens with `scope={kind: 'system', ...}` and no pre-selected role.
3. `Step2RoleAssignments` (existing wizard step) — opens with `scope={kind: 'organization'}`, `bootstrap=true` on first invocation.

**Acceptance test**: `AssignRoleDialog.test.tsx` covers (1) RBAC-driven role dropdown filtering, (2) SoD warning render when `warnings` arrives, (3) `bootstrap` flag wiring.

---

## 3. Mission Owner banner — modification (inline JSX, not a standalone component)

**File**: `src/Ato.Copilot.Dashboard/src/pages/SystemDetail.tsx` (existing) — the banner is rendered inline (no `MissionOwnerBanner.tsx` component file exists per the audit at 2026-05-19).

**Behavior change** (FR-029):

The inline banner block at the top of `SystemDetail.tsx` currently reads `mission.owner.assigned` from a legacy hook against the legacy endpoint. It is migrated to call `GET /api/roles/system/{systemId}` (via `rolesApi.getSystemRoles`) and resolve presence/absence via:

```ts
const missionOwnerEntry = data.roles.find(r => r.role === 'MissionOwner');
const missionOwnerAssigned =
  missionOwnerEntry !== undefined &&
  missionOwnerEntry.source !== 'not-assigned' &&
  missionOwnerEntry.person !== null;
```

This evaluates to `true` whenever **any** of override / inherited / org-fallback / legacy is the source — the banner clears immediately on Org-level assign even before the worker materializes inherited rows (FR-029, SC-001).

**Modification surface**:
- Replace the existing assignment-source hook with a `useQuery(['system-roles', systemId], () => rolesApi.getSystemRoles(systemId))` call.
- Replace the previously inert "Assign Mission Owner" CTA with an `onClick` that opens the shared `AssignRoleDialog` (`scope={kind: 'organization'}`, `initialRole='MissionOwner'`, `lockRole=true`).
- No new component file is introduced. No new props (the banner is local JSX inside `SystemDetail.tsx`).

---

## 4. `RoleAssignmentPanel` — modification of the existing per-system card

**File**: `src/Ato.Copilot.Dashboard/src/components/cards/RoleAssignmentPanel.tsx` (existing — MODIFY; the audit at 2026-05-19 confirmed this card already exists under `components/cards/`)

```ts
export interface RoleAssignmentPanelProps {
  registeredSystemId: string;
  /** Caller's effective role for affordance hiding — fetched once per page mount via `rolesApi.getEffectiveRole()`. */
  callerEffectiveRole: RmfRole | null;
}
```

**Renders** a 7-row table (one row per `RmfRole`) showing:

- Role name (badge-styled).
- Assignee display name (or `—` for `not-assigned`).
- Source badge: `Override`, `Inherited`, `Pending` (`org-fallback`), `Legacy`, or empty.
- Action affordances:
  - `Inherited` row → "Override" button (opens `AssignRoleDialog` in system scope).
  - `Override` row → "Remove override" button (calls `DELETE /api/roles/system/{systemId}/{role}/{personId}`; after success, falls back to inherited or org-fallback row in the next refetch).
  - `not-assigned` row → "Assign" button (filtered by `RBAC_ASSIGNABLE_BY[callerEffectiveRole]`).

**Data fetching** — the panel fetches via React Query: `useQuery(['system-roles', registeredSystemId], () => rolesApi.getSystemRoles(registeredSystemId))`.

**Wiring (T040a)**: the existing card was previously orphaned (no import). T040a adds the import and render in `SystemDetail.tsx`, threads `callerEffectiveRole` resolved via `rolesApi.getEffectiveRole()`.

---

## 5. `WizardStep2RolesState` — extended state shape

**File**: `src/Ato.Copilot.Dashboard/src/features/onboarding/steps/Step2RoleAssignments.tsx` (existing; state extension — the audit at 2026-05-19 confirmed the wizard step lives under `features/onboarding/steps/`, NOT under `components/Onboarding/`)

```ts
type WizardStep2RolesState = {
  /** Persisted across page reloads via the wizard's existing session-state mechanism (FR-016). */
  draftAssignments: Array<{
    role: RmfRole;            // 7-role universe
    personId: string | null;
    isPrimary: boolean;
  }>;

  /** True on first visit; false on resume (used to drive the bootstrap flag on `AssignRoleDialog`). */
  firstVisit: boolean;

  /** Warnings accumulated across all writes in this session. */
  sodWarnings: SoDWarning[];
};
```

**FR-016 conformance**: when the user closes the browser mid-wizard, the `draftAssignments` are persisted by the existing wizard-state service. On resume, the same step renders the same draft state. Test surface: `WizardStep2Roles.resume.test.tsx`.

---

## 6. API client functions

**File**: `src/Ato.Copilot.Dashboard/src/lib/api/roles.ts` (NEW)

```ts
import { apiClient } from '@/lib/api/client';
import type { SystemRolesResponse, AssignmentResult, RmfRole } from '@/types/roles';

export const rolesApi = {
  getSystemRoles: (registeredSystemId: string) =>
    apiClient.get<SystemRolesResponse>(`/api/roles/system/${registeredSystemId}`),

  assignSystemRole: (registeredSystemId: string, role: RmfRole, personId: string) =>
    apiClient.post<AssignmentResult>(`/api/roles/system/${registeredSystemId}`, { role, personId }),

  removeSystemRole: (registeredSystemId: string, role: RmfRole, personId: string) =>
    apiClient.delete<AssignmentResult>(`/api/roles/system/${registeredSystemId}/${role}/${personId}`),

  assignOrgRole: (role: RmfRole, personId: string, isPrimary: boolean, bootstrap = false) =>
    apiClient.post<AssignmentResult>(`/api/roles/organization`, { role, personId, isPrimary, bootstrap }),

  removeOrgRole: (role: RmfRole, personId: string) =>
    apiClient.delete<AssignmentResult>(`/api/roles/organization/${role}/${personId}`),

  /** Server-resolved caller effective role per FR-027 / SC-009 (added per Feature 049 analysis G2). */
  getEffectiveRole: () =>
    apiClient.get<{ effectiveRole: RmfRole | null }>(`/api/roles/effective`),
};
```

Errors thrown by `apiClient` retain their envelope (the existing `apiClient` already unwraps `data` on 2xx and re-throws `AssignmentResult`-shaped errors on 4xx/5xx). Consumers `try/catch` and read `error.code` for branching.

---

## Type contract → FR / SC traceability

| Type / Component | Drives FRs | Drives SCs |
|---|---|---|
| `RmfRole` / `RBAC_ASSIGNABLE_BY` | FR-013, FR-027 | (UI affordance only — SC-009 enforced server-side) |
| `AssignRoleDialog` | FR-014, FR-015 | SC-005 |
| Mission Owner banner (inline in `SystemDetail.tsx`) | FR-008, FR-029 | SC-001 |
| `RoleAssignmentPanel` (modified) | FR-002, FR-010, FR-011, FR-013 | SC-002, SC-003 |
| `WizardStep2RolesState` (`Step2RoleAssignments.tsx`) | FR-014, FR-016 | SC-002, SC-005 |
| `rolesApi` | FR-002, FR-010, FR-011 | SC-001, SC-002 |
| `rolesApi.getEffectiveRole` | FR-027 | SC-009 |
