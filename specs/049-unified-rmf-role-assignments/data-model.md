# Phase 1: Data Model — Unified RMF Role Assignments

**Branch**: `049-unified-rmf-role-assignments`
**Date**: 2026-05-19
**Status**: Final

## Summary

This feature introduces **no new tables and no new migrations**. The only schema-level change is appending three values to the existing `OrganizationRole` enum. All four entities touched by the feature are already in the schema today; their column definitions, indexes, and `[TenantScoped]` attributes are unchanged.

## Entity Inventory

| Entity | Touch | Schema change? | File |
|---|---|---|---|
| `OrganizationRole` (enum) | Extend | 3 values appended at the end | `src/Ato.Copilot.Core/Models/Onboarding/OrganizationRoleAssignment.cs:43-52` |
| `OrganizationRoleAssignment` | No change | No | `src/Ato.Copilot.Core/Models/Onboarding/OrganizationRoleAssignment.cs:10-41` |
| `SystemRoleAssignment` | No change | No | `src/Ato.Copilot.Core/Models/Onboarding/SystemRoleAssignment.cs:12-47` |
| `RmfRoleAssignment` | No change (frozen) | No | `src/Ato.Copilot.Core/Models/Compliance/ComplianceModels.cs` |
| `Person` | No change | No | `src/Ato.Copilot.Core/Models/Onboarding/Person.cs` |
| `RmfRole` (enum) | Frozen | No | `src/Ato.Copilot.Core/Models/Compliance/ComplianceModels.cs:556-572` |

## 1. `OrganizationRole` enum — extension

**File**: `src/Ato.Copilot.Core/Models/Onboarding/OrganizationRoleAssignment.cs`

### Current state (verified)

```csharp
public enum OrganizationRole
{
    Issm,           // ordinal 0
    Isso,           // ordinal 1
    Administrator,  // ordinal 2
    Assessor,       // ordinal 3
}
```

### Proposed state

```csharp
public enum OrganizationRole
{
    Issm,                  // ordinal 0  (unchanged)
    Isso,                  // ordinal 1  (unchanged)
    Administrator,         // ordinal 2  (unchanged)
    Assessor,              // ordinal 3  (unchanged)
    MissionOwner,          // ordinal 4  (NEW)
    AuthorizingOfficial,   // ordinal 5  (NEW)
    SystemOwner,           // ordinal 6  (NEW)
}
```

### Validation rules

- **Ordinal stability** — existing rows whose `Role` column values are 0–3 keep their semantics. New values append at ordinals 4–6. No data migration required.
- **Storage — `OrganizationRoleAssignment.Role`**: configured `HasConversion<string>().HasMaxLength(32).IsRequired()` at [`AtoCopilotContext.cs:3594`](../../../src/Ato.Copilot.Core/Data/Context/AtoCopilotContext.cs#L3594). The column is `nvarchar(32)`. The new strings `"MissionOwner"` (12 chars), `"AuthorizingOfficial"` (19 chars), and `"SystemOwner"` (11 chars) all fit comfortably; no column length change is required.
- **Storage — `SystemRoleAssignment.Role`**: no explicit `OnModelCreating` block (verified — only `DbSet<SystemRoleAssignment>` is declared at [`AtoCopilotContext.cs:451`](../../../src/Ato.Copilot.Core/Data/Context/AtoCopilotContext.cs#L451)); the property therefore uses EF Core's default convention, which serializes enums as `int`. Appending ordinals 4–6 does not change the column type. NOTE: this means the two role-assignment tables store the SAME enum in DIFFERENT formats (string vs. int). The application layer reads both as the typed `OrganizationRole` enum, so the C# `IUnifiedRoleReader` is unaffected; raw-SQL audit queries that hit both tables must convert accordingly.
- **Serialization** — `RmfRole.MissionOwner` (in the legacy enum) and `OrganizationRole.MissionOwner` (in the extended enum) serialize to the same JSON string `"MissionOwner"`. Cross-enum lookup tables MUST use the string form, never the ordinal (the two enums have different ordinals for the shared values).

### Test fixtures driven by this change

- `RoleAuthorizationServiceTests.cs` — Theory data enumerates all 7 `RmfRole` values × 7 caller roles = 49 cells. The 3 new values are first-class members.
- `SoDConflictDetectorTests.cs` — pairs use the new `MissionOwner`/`AuthorizingOfficial`/`SystemOwner` values.
- `UnifiedRoleReaderTests.cs` — exercises all 7 roles through the precedence chain.

### Cross-enum mapping (`OrganizationRole` ↔ `RmfRole`)

Both enums use **identical names** for the 7-value union after this change:

| `OrganizationRole` | `RmfRole` (frozen) | Map function |
|---|---|---|
| `Issm` | `Issm` | identity |
| `Isso` | `Isso` | identity |
| `Administrator` | (not in `RmfRole`) | sentinel; not mapped |
| `Assessor` | `Sca` | mapped (`"Assessor"` ↔ `"Sca"` is an intentional name divergence retained from Feature 047) |
| `MissionOwner` | `MissionOwner` | identity |
| `AuthorizingOfficial` | `AuthorizingOfficial` | identity |
| `SystemOwner` | `SystemOwner` | identity |

The map is encoded in a `static class OrganizationRoleToRmfRoleMap` helper in `Ato.Copilot.Core/Services/Roles/`. The `Administrator` → `RmfRole` direction returns `null` (Administrator is an Org-scope-only role; it has no RMF-document equivalent and does not appear in OSCAL party exports). The `Assessor` ↔ `Sca` mapping is the **only** non-identity edge.

## 2. `OrganizationRoleAssignment` — no schema change

**File**: `src/Ato.Copilot.Core/Models/Onboarding/OrganizationRoleAssignment.cs`

Fields used by this feature (all pre-existing):

| Field | Type | Used for |
|---|---|---|
| `Id` | `Guid` | Primary key |
| `TenantId` | `Guid` | Tenant scope (FR-004) |
| `Role` | `OrganizationRole` | The role being filled (extended enum) |
| `PersonId` | `Guid` | FK to `Person` |
| `IsPrimary` | `bool` | The "primary" assignment that inherits down (Assumptions § Cardinality) |
| `RemovedAt` | `DateTimeOffset?` | Soft-removal (FR-007) |
| `CreatedAt` / `UpdatedAt` / `CreatedBy` / `UpdatedBy` | audit | Standard audit |

**State transitions** (already implemented; documented for clarity):

```text
not-exist ──(write)──▶ active (IsPrimary computed by service: true if no prior active row for {TenantId, Role}, else false)
active ──(update)──▶ active (PersonId or IsPrimary changed)
active ──(soft-remove)──▶ removed (RemovedAt set; inherited child rows in SystemRoleAssignment also soft-removed per FR-007)
removed ──(re-write same {TenantId, Role, PersonId})──▶ new active row created (do not resurrect; preserves audit trail)
```

## 3. `SystemRoleAssignment` — no schema change

**File**: `src/Ato.Copilot.Core/Models/Onboarding/SystemRoleAssignment.cs`

Fields used by this feature (all pre-existing):

| Field | Type | Used for |
|---|---|---|
| `Id` | `Guid` | Primary key |
| `TenantId` | `Guid` | Tenant scope |
| `RegisteredSystemId` | `string` | FK to `RegisteredSystem` (string GUID, per existing convention) |
| `Role` | `OrganizationRole` | The role on this system (extended enum) |
| `PersonId` | `Guid` | FK to `Person` |
| `IsInherited` | `bool` | `true` = inherited from Org; `false` = per-system override |
| `SourceOrganizationRoleAssignmentId` | `Guid?` | When inherited, the source row's PK |
| `RemovedAt` | `DateTimeOffset?` | Soft-removal |

**State transitions**:

```text
                                              ┌─ inherited ──(org-row soft-removed)──▶ removed
                                              │
not-exist ──(system created or org-row fan-out)─┤
                                              │
                                              └─ inherited ──(user creates override)──▶ override (new row; old inherited row soft-removed)

override ──(user removes override)──▶ removed
removed ──(org-row still active)──▶ reader falls through to Org-level row (no automatic re-materialization)
removed ──(worker startup sweep)──▶ inherited (re-created idempotently if no override exists)
```

The state diagram intentionally has two paths to "inherited" — synchronous creation (FR-005, when the system is registered) and asynchronous fan-out (FR-028, when the Org-level row is added after systems exist). Both paths land on the same row shape.

## 4. `RmfRoleAssignment` — frozen

**File**: `src/Ato.Copilot.Core/Models/Compliance/ComplianceModels.cs`

No fields touched. No new fields added. Legacy reader keeps reading it; the write-through (FR-018) inserts an equivalent row alongside the new-model row in the same transaction.

The `RmfRole` enum at line 556 is **frozen** (FR-020). Its values, ordinals, and serialized names MUST NOT change. The integration test `LegacyEnumSerializationTests` (existing, will be re-run as part of CI for this feature) asserts the JSON-serialized form of every `RmfRole` value.

## 5. `Person` — no change

Used as the `PersonId` FK target on all role-assignment rows. The reader returns the navigation-property `Person` joined onto the row so the dashboard can show the assignee's display name without an N+1.

## Read-time precedence (FR-003, encoded by `IUnifiedRoleReader`)

For a query of role `R` on system `S` in tenant `T`:

```text
1. SystemRoleAssignment where TenantId=T and RegisteredSystemId=S and Role=R
       and RemovedAt is null and IsInherited=false                  ── per-system override (highest)
2. SystemRoleAssignment where TenantId=T and RegisteredSystemId=S and Role=R
       and RemovedAt is null and IsInherited=true                   ── materialized inherited row
3. OrganizationRoleAssignment where TenantId=T and Role=R
       and RemovedAt is null
       and (IsPrimary=true or no IsPrimary=true row exists)         ── Org-level fallback (clears banner even before fan-out)
4. RmfRoleAssignment where TenantId=T and SystemId=S and Role=R
       and RemovedAt is null                                        ── legacy fallback (last resort)
5. "not assigned"                                                   ── nothing matched
```

Step 3 is the key invariant for FR-029: the Org-level row alone is sufficient to satisfy `missionOwnerAssigned = true`. The materialized inherited row at step 2 exists only for OSCAL-export, audit clarity, and to give the Roles panel an "Edit" target for converting inheritance to an override.

## Tenant Isolation

Every read at every step above carries the `TenantId` predicate. The existing `[TenantScoped]` attribute on all three role-assignment entities ensures EF Core's global query filter cannot be bypassed by a missing `where`. No new isolation infrastructure is introduced (Feature 048 already provides it). Integration test `TenantIsolationRolesTests` covers SC-006.

## Constraints

- **No data backfill** — existing tenants whose `RmfRoleAssignment` table has rows continue to work via step 4 of the precedence chain. They never need a migration.
- **No row count growth from this feature for existing tenants** until they (a) name a new Org-level Mission Owner / AO / System Owner or (b) the worker fan-out runs for them when they do. Inherited rows are bounded at `(active OrgRows) × (active Systems)` per tenant.
- **Worker idempotency invariant** — `select 1 from SystemRoleAssignment where SourceOrganizationRoleAssignmentId = @id and RegisteredSystemId = @sid and RemovedAt is null` is the existence check before insert.

## Schema Diff (visualization)

```text
OrganizationRole enum (C#):
  Issm                    [unchanged]
  Isso                    [unchanged]
  Administrator           [unchanged]
  Assessor                [unchanged]
+ MissionOwner            [NEW]
+ AuthorizingOfficial     [NEW]
+ SystemOwner             [NEW]

DB columns:
  OrganizationRoleAssignments.Role      nvarchar(32)   [unchanged — new values fit]
  SystemRoleAssignments.Role            int            [unchanged — new ordinals fit]
  RmfRoleAssignments.Role               (frozen)       [unchanged — RmfRole enum frozen]

Tables: no change.
Indexes: no change.
FKs: no change.
Migration files: none. (EnsureCreatedAsync in dev; prod tenants already have the column at the correct shape.)
```

This is the **entirety** of the data-model change for Feature 049.
