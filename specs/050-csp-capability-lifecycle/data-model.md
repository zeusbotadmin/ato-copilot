# Phase 1 — Data Model: CSP-Inherited Capability Lifecycle

**Feature**: 050-csp-capability-lifecycle
**Plan**: [plan.md](./plan.md)
**Research**: [research.md](./research.md)
**Spec**: [spec.md](./spec.md)
**Date**: 2026-05-22

This feature adds **one** new entity (`CapabilityHistoryEvent`) and one
new EF Core migration. It makes **zero** schema changes to existing
tables. The reparent operation reuses the existing
`CspInheritedCapability.CspInheritedComponentId` FK column.

---

## 1. New entity: `CapabilityHistoryEvent`

### 1.1 Purpose

Append-only audit-trail row recording a single state-changing operation
on a CSP-inherited capability. Drives FR-004 (audit trail entity), FR-005
(drawer History section), FR-014 (pagination contract), FR-015
(retention), and FR-016 (Remap audit semantics).

### 1.2 Fields

| Field | C# Type | DB Type (SQL Server) | DB Type (SQLite) | Required | Constraints |
|---|---|---|---|---|---|
| `Id` | `Guid` | `uniqueidentifier` | `TEXT (GUID)` | yes | `[Key]`; primary key. |
| `CapabilityId` | `Guid` | `uniqueidentifier` | `TEXT (GUID)` | yes | Logical FK to `CspInheritedCapability.Id`. **NOT a cascading FK** (per R9 — history outlives capability). |
| `TenantId` | `Guid` | `uniqueidentifier` | `TEXT (GUID)` | yes | Logical FK to `Tenant.Id`. **Cascade delete** (tenant offboarding removes rows). |
| `EventType` | `CapabilityHistoryEventType` (enum) | `nvarchar(32)` | `TEXT` | yes | Persisted as string. Values: `Created`, `Edited`, `Reviewed`, `Moved`, `Archived`, `Unarchived`. |
| `ActorOid` | `string` | `nvarchar(254)` | `TEXT` | yes | Caller's `oid` claim. Same shape as `CspInheritedCapability.ReviewedBy`. `[MaxLength(254)]`. |
| `OccurredAt` | `DateTimeOffset` | `datetimeoffset` | `TEXT (ISO-8601)` | yes | Server-side UTC timestamp at the moment the row was written. Default = `DateTimeOffset.UtcNow`. |
| `Summary` | `string` | `nvarchar(500)` | `TEXT` | yes | `[Required, MaxLength(500)]`. Human-readable description. |
| `MetadataJson` | `string` | `nvarchar(2000)` | `TEXT` | no | Structured payload per event type. Null when no structured metadata. `[MaxLength(2000)]`. |

**Total fields**: 8.

### 1.3 `CapabilityHistoryEventType` enum

```csharp
namespace Ato.Copilot.Core.Models.Tenancy;

public enum CapabilityHistoryEventType
{
    Created = 0,
    Edited = 1,
    Reviewed = 2,
    Moved = 3,
    Archived = 4,
    Unarchived = 5,
}
```

Persisted as **string** in EF Core via `HasConversion<string>()` to
match the existing `CspInheritedCapabilityStatus` and `MappedBy` enum
serialization convention. This makes raw-SQL audit queries readable
without a value lookup.

### 1.4 `MetadataJson` shape per event type

| Event type | Shape | Example |
|---|---|---|
| `Created` (manual-add) | `null` *or* `{ "markedMappedImmediately": true }` when the override was used | `{ "markedMappedImmediately": true }` |
| `Created` (Remap) | `{ "remapRunId": "<guid>", "source": "Remap" }` | `{ "remapRunId": "1d3...", "source": "Remap" }` |
| `Created` (Import / AI pipeline initial load) | `{ "source": "Import" }` | `{ "source": "Import" }` |
| `Edited` (manual) | `null` *or* `{ "fields": ["name", "description"] }` for diff hint | `{ "fields": ["mappedNistControlIds"] }` |
| `Edited` (Remap) | `{ "remapRunId": "<guid>", "source": "Remap" }` | `{ "remapRunId": "1d3...", "source": "Remap" }` |
| `Reviewed` | `{ "reviewerNote": "<note>" }` when a reviewer note was given; else `null` | `{ "reviewerNote": "Approved after manual NIST cross-check." }` |
| `Moved` | `{ "fromComponentId": "<guid>", "toComponentId": "<guid>" }` | `{ "fromComponentId": "a1b...", "toComponentId": "c2d..." }` |
| `Archived` (manual) | `null` | `null` |
| `Archived` (Remap) | `{ "remapRunId": "<guid>", "source": "Remap" }` | `{ "remapRunId": "1d3...", "source": "Remap" }` |
| `Unarchived` | `null` | `null` |

**Serialization rule**: store as JSON string (`JsonSerializer.Serialize`
with default options). Read back into `JsonDocument` lazily by the
endpoint — never deserialize into a strongly-typed model in the
controller, since the shape is event-type-dependent.

### 1.5 Validation rules

- `Summary` MUST be non-empty (`Required`) and ≤ 500 chars.
- `ActorOid` MUST be non-empty and ≤ 254 chars (matches `oid` claim
  spec).
- `MetadataJson` MUST be either `null` or a valid JSON document parsable
  by `JsonDocument.Parse`. Invalid JSON is a server bug — the writer
  enforces shape, never the caller.
- `OccurredAt` MUST be UTC. Enforced by writing
  `DateTimeOffset.UtcNow` only; never accepting a client-supplied
  timestamp.
- `EventType` MUST be one of the six enum values. Invalid values are
  rejected by EF Core conversion.

### 1.6 Immutability

There is **no `UpdateAsync` or `DeleteAsync` operation** on
`ICapabilityHistoryService`. The service exposes only `AppendAsync` and
`ListAsync` (R5 test strategy enforces this with a unit test that asserts
the interface has no mutating method other than `AppendAsync`).

A direct-SQL UPDATE / DELETE on the table is an out-of-band operation;
the application layer never emits one.

### 1.7 Retention (FR-015, R9)

- **Capability archive (event `Archived`)** → state change; history rows
  remain. New `Archived` row added.
- **Capability unarchive (event `Unarchived`)** → state change; history
  rows remain. New `Unarchived` row added.
- **Capability hard delete (out-of-band only)** → history rows
  **survive**. The history-list endpoint continues to return them so an
  auditor can still see what the deleted capability did.
- **Tenant offboarding** → cascades all rows away as part of the
  existing Feature 048 tenant-removal flow.

**EF Core FK declaration** (in `OnModelCreating`):

```csharp
modelBuilder.Entity<CapabilityHistoryEvent>()
    .HasOne<CspInheritedCapability>()
    .WithMany()
    .HasForeignKey(e => e.CapabilityId)
    .OnDelete(DeleteBehavior.NoAction);   // ← survives capability delete

modelBuilder.Entity<CapabilityHistoryEvent>()
    .HasOne<Tenant>()
    .WithMany()
    .HasForeignKey(e => e.TenantId)
    .OnDelete(DeleteBehavior.Cascade);    // ← cascades on tenant offboard
```

Note: SQL Server forbids multiple cascade paths when both FKs cascade.
By declaring `CapabilityId` as `NoAction` and `TenantId` as `Cascade`,
we sidestep that constraint and get the semantics we want.

### 1.8 Indexes

| Index | Columns | Rationale |
|---|---|---|
| `IX_CapabilityHistoryEvents_Tenant_Capability_Occurred` | `(TenantId, CapabilityId, OccurredAt DESC)` | Primary read pattern — list events for one capability scoped to caller's tenant in reverse chronological order. Supports the `GET .../history` endpoint without a table scan. |
| `IX_CapabilityHistoryEvents_Tenant_Occurred` | `(TenantId, OccurredAt DESC)` | Optional future-use: tenant-wide audit feed. NOT created in this feature; documented here so it isn't forgotten. **Not in migration.** |
| `IX_CapabilityHistoryEvents_RemapRun` | computed on `JSON_VALUE(MetadataJson, '$.remapRunId')` (SQL Server only) | Optional future-use: "show me all events from Remap run X". NOT created in this feature. **Not in migration.** |

Only the first index ships in this migration. The two optional indexes
are documented in case a follow-on feature surfaces those query
patterns.

### 1.9 Storage estimates

- Row size (SQL Server): ~ 400 bytes typical (4× GUID = 64 B, enum
  string ≤ 32 B, actor oid ≈ 100 B, summary ≈ 80 B, metadata ≈ 80 B,
  occurredAt = 10 B, overhead).
- Per capability, < 20 events typical → ~ 8 KB per capability.
- 1 M rows per tenant ceiling (per Assumptions section) → ~ 400 MB
  per tenant. Comfortably within SQL Server budgets; no partitioning
  needed.

### 1.10 Entity file location

```text
src/Ato.Copilot.Core/Models/Tenancy/
├── CapabilityHistoryEvent.cs        # NEW
└── CapabilityHistoryEventType.cs    # NEW
```

The entity is in the `Tenancy` namespace because it sits alongside
`CspInheritedCapability` and shares the tenant-scoping convention.

### 1.11 Reference C# definition (illustrative; tasks.md will pin)

```csharp
using System.ComponentModel.DataAnnotations;

namespace Ato.Copilot.Core.Models.Tenancy;

/// <summary>
/// Append-only audit-trail row recording a single state-changing
/// operation on a <see cref="CspInheritedCapability"/>. Feature 050
/// FR-004 / FR-014 / FR-015 / FR-016.
/// </summary>
/// <remarks>
/// History rows are tenant-scoped (the CSP tenant performing the
/// operation). They survive capability hard-delete (logical FK with
/// <c>NoAction</c>) and are removed only by tenant offboarding (FK
/// with <c>Cascade</c>).
/// </remarks>
public class CapabilityHistoryEvent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CapabilityId { get; set; }

    public Guid TenantId { get; set; }

    [Required]
    public CapabilityHistoryEventType EventType { get; set; }

    [Required, MaxLength(254)]
    public string ActorOid { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    [Required, MaxLength(500)]
    public string Summary { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? MetadataJson { get; set; }
}
```

---

## 2. Existing entity touched: none

`CspInheritedCapability` is **NOT modified**.

The reparent operation:

- **Reads** the current `CspInheritedComponentId` and `RowVersion`.
- **Writes** the new `CspInheritedComponentId`, sets `Status =
  NeedsReview`, and EF Core bumps `RowVersion` automatically (because
  the column has `[Timestamp]`).
- Preserves `Id`, `CreatedAt`, `CreatedBy`, `MappedBy`,
  `MappedNistControlIds`, `MappingConfidence`, `ReviewedBy`,
  `ReviewedAt`, `ReviewerNote`, `MappingFailureReason`.

No new columns. No constraint changes.

---

## 3. Existing entity touched: `Tenant`

**No changes**. The new `CapabilityHistoryEvent.TenantId` FK references
`Tenant.Id` as declared in Feature 048. No new column on `Tenant`.

---

## 4. EF Core migration

### 4.1 Migration name

`<timestamp>_AddCapabilityHistoryEvents`

Generated by `dotnet ef migrations add AddCapabilityHistoryEvents -p
src/Ato.Copilot.Core` once the entity + `OnModelCreating` are in place.

### 4.2 Expected up-migration shape (SQL Server)

```sql
CREATE TABLE [CapabilityHistoryEvents] (
    [Id]            uniqueidentifier  NOT NULL,
    [CapabilityId]  uniqueidentifier  NOT NULL,
    [TenantId]      uniqueidentifier  NOT NULL,
    [EventType]     nvarchar(32)      NOT NULL,
    [ActorOid]      nvarchar(254)     NOT NULL,
    [OccurredAt]    datetimeoffset    NOT NULL,
    [Summary]       nvarchar(500)     NOT NULL,
    [MetadataJson]  nvarchar(2000)    NULL,
    CONSTRAINT [PK_CapabilityHistoryEvents] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_CapabilityHistoryEvents_Tenants_TenantId]
        FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id])
        ON DELETE CASCADE
);

CREATE INDEX [IX_CapabilityHistoryEvents_Tenant_Capability_Occurred]
    ON [CapabilityHistoryEvents]
        ([TenantId] ASC, [CapabilityId] ASC, [OccurredAt] DESC);
```

The `CapabilityId` FK is **not declared at the DB level** (only the
logical relationship in EF Core via `HasOne...WithMany`) so that a
direct-SQL hard delete of a capability does not throw. The application
layer never emits that delete.

### 4.3 Down-migration

`DROP INDEX IX_CapabilityHistoryEvents_Tenant_Capability_Occurred;
DROP TABLE CapabilityHistoryEvents;` — generated automatically.

### 4.4 SQLite (`EnsureCreatedAsync`) compatibility

`EnsureCreatedAsync` derives the schema from `OnModelCreating`, so the
entity declaration in section 1.7 above produces the equivalent SQLite
table on a fresh dev DB. Existing dev DBs may need a manual
`EnsureSchemaAdditions/` module — optional, documented in R2 but not
shipped in this feature.

---

## 5. `AtoCopilotContext` additions

```csharp
public DbSet<CapabilityHistoryEvent> CapabilityHistoryEvents { get; set; }
    = null!;
```

Plus the `OnModelCreating` block from section 1.7 plus an enum
conversion:

```csharp
modelBuilder.Entity<CapabilityHistoryEvent>()
    .Property(e => e.EventType)
    .HasConversion<string>()
    .HasMaxLength(32);

modelBuilder.Entity<CapabilityHistoryEvent>()
    .HasIndex(e => new { e.TenantId, e.CapabilityId, e.OccurredAt })
    .HasDatabaseName("IX_CapabilityHistoryEvents_Tenant_Capability_Occurred")
    .IsDescending(false, false, true);
```

---

## 6. State transitions

This feature does not introduce a state machine of its own. It
**records** state transitions of the existing `CspInheritedCapability`:

```text
                       ┌─────────────────────────────────────┐
                       │ Existing CspInheritedCapability     │
                       │ state model (Feature 048):          │
                       │   NeedsReview ──Reviewed──▶ Mapped  │
                       │   Mapped     ──Archived──▶ Archived │
                       │   Archived   ──Unarchived─▶ NeedsReview │
                       └─────────────────────────────────────┘
                                       │
                                       ▼ every transition writes
                       ┌─────────────────────────────────────┐
                       │ CapabilityHistoryEvent (new)        │
                       │   Created   when row first persisted │
                       │   Edited    on PATCH name/desc/etc   │
                       │   Reviewed  on review-completion     │
                       │   Moved     on reparent (NEW in 050) │
                       │   Archived  on archive               │
                       │   Unarchived on unarchive            │
                       └─────────────────────────────────────┘
```

### 6.1 Reparent-specific transition (NEW in 050)

```text
Before: capability.CspInheritedComponentId = A, status = X
        capability.RowVersion = v_old

Inputs: targetComponentId = B
        ifMatch = v_old

  ┌─────────────────────────────────────────────────────────┐
  │ Transaction begins                                       │
  │   ① Verify target B exists, not Archived, same tenant   │
  │      └ fail → 404 (cross-tenant or archived target)      │
  │   ② UPDATE capability                                    │
  │       SET CspInheritedComponentId = B,                  │
  │           Status = NeedsReview,                          │
  │           RowVersion = <new>                             │
  │       WHERE Id = capability.Id                           │
  │         AND RowVersion = v_old                           │
  │      └ 0 rows affected → 412 ROW_VERSION_MISMATCH        │
  │   ③ INSERT history (EventType=Moved,                     │
  │                     MetadataJson={fromA,toB})            │
  │   ④ Commit                                               │
  └─────────────────────────────────────────────────────────┘

After:  capability.CspInheritedComponentId = B, status = NeedsReview
        capability.RowVersion = v_new
        +1 CapabilityHistoryEvent row (Moved)
```

### 6.2 Manual-add transition (extended in 050)

```text
Inputs: name, description, mappedNistControlIds,
        markMappedImmediately (default false)

  ┌─────────────────────────────────────────────────────────┐
  │ Transaction begins                                       │
  │   ① INSERT capability                                    │
  │       Status = NeedsReview (default)                     │
  │       MappedBy = User                                    │
  │       CreatedBy = <caller oid>                           │
  │   ② IF markMappedImmediately = true:                     │
  │       UPDATE capability                                  │
  │         SET Status = Mapped,                             │
  │             ReviewedBy = <caller oid>,                   │
  │             ReviewedAt = now,                            │
  │             ReviewerNote = "Mapped on create by creator."│
  │   ③ INSERT history (EventType=Created,                   │
  │                     MetadataJson={markedMappedImmediately│
  │                                   = true} if override)   │
  │   ④ IF markMappedImmediately = true:                     │
  │       INSERT history (EventType=Reviewed,                │
  │                       MetadataJson={reviewerNote="..."}) │
  │   ⑤ Commit                                               │
  └─────────────────────────────────────────────────────────┘

Result: capability persisted in correct state;
        either 1 or 2 history rows written atomically.
```

### 6.3 Remap-run transitions (R11)

Each capability touched by Remap classifies into one bucket:

```text
                  ┌─ AI created a new capability ──▶ INSERT capability;
                  │                                  INSERT history (Created,
                  │                                  metadata={remapRunId, source=Remap})
                  │
                  ├─ AI changed an existing AI row ▶ UPDATE capability;
   Remap run ─────┤                                  INSERT history (Edited,
                  │                                  metadata={remapRunId, source=Remap})
                  │
                  ├─ AI removed an existing AI row ▶ UPDATE capability SET Status=Archived;
                  │                                  INSERT history (Archived,
                  │                                  metadata={remapRunId, source=Remap})
                  │
                  ├─ Existing mappedBy=User row ───▶ NO mutation, NO history row
                  │   (preserved)
                  │
                  └─ Existing AI row, AI output ──▶ NO mutation, NO history row
                      identical to current
```

All operations in a single Remap run share one **`remapRunId` GUID**
generated at run start (in `RemapAsync`), threaded through every
`AppendAsync` call.

---

## 7. Test data fixtures (R5)

### 7.1 Unit-test fixtures

| Fixture | Purpose | Lives in |
|---|---|---|
| Three CSP-inherited capabilities under one component, one `Mapped` + one `NeedsReview` + one `Archived` | Default test bed for service unit tests | `CapabilityFixtureFactory` (new helper if needed; otherwise inline) |
| Two non-archived sibling components in same tenant + one archived component | Reparent target eligibility tests | inline in test class |
| Two non-archived components in tenant A + one in tenant B | Tenant-isolation negative tests | inline |
| One capability with 25 pre-existing history rows | History pagination tests | inline |

### 7.2 Integration-test fixtures

`WebApplicationFactory<Program>` with the SQLite in-memory provider
seeded by the same fixtures plus a CSP-Admin simulated context
(`tenant=00000000-...-001`, `oid=00000000-...-002`).

### 7.3 Frontend test fixtures

MSW (or equivalent) mocks for `ListCspInheritedComponents` and the new
endpoints. No real network calls in TS component tests.

---

## 8. Out-of-scope schema work

The following were **considered and rejected** for this feature:

- **A separate `RemapRuns` table** with a FK from history rows to runs.
  Rejected: the `remapRunId` correlator in `metadataJson` is sufficient
  for current audit needs; a dedicated table is premature (YAGNI per
  §III).
- **A `version` column on `CapabilityHistoryEvent`** for schema
  evolution of `metadataJson`. Rejected: JSON is already
  self-describing; if we ever change shape we can branch on event-type
  field presence.
- **A `GlobalReference` attribute on `CapabilityHistoryEvent`**.
  Rejected: history is tenant-scoped to the CSP tenant that performed
  the action. Hosted tenants do not need to see who in CSP edited the
  catalog; they only inherit current state.
- **A FK at the DB level from `CapabilityHistoryEvent.CapabilityId` to
  `CspInheritedCapability.Id`**. Rejected: would block out-of-band
  capability hard delete and break R9's retention promise.
- **Soft-delete column on `CapabilityHistoryEvent`**. Rejected: history
  is immutable; soft-delete is meaningless for an append-only log.

---

## 9. Cross-reference matrix

| FR | data-model.md section | research.md decision |
|---|---|---|
| FR-001 (manual-add default) | § 6.2 | R1 |
| FR-002 (reparent endpoint) | § 6.1 | R3 |
| FR-003 (reparent UI) | n/a (frontend) | R4, R8, R10 |
| FR-004 (audit trail entity) | § 1 (whole) | R1 |
| FR-005 (audit trail surface) | n/a (frontend) | R1, R7 |
| FR-006/7/8 (Remap relocation) | n/a (frontend) | — |
| FR-009 (picker review-count) | n/a (frontend) | — |
| FR-010 (self-review allowed) | n/a (no schema impact) | — |
| FR-011 (per-component queue) | n/a (no schema impact) | — |
| FR-012 (optimistic concurrency) | § 6.1 (line ②) | R3 |
| FR-013 (tenant isolation) | § 1.7 (cascade) + § 1.8 (composite index leads with TenantId) | R6 |
| FR-014 (history endpoint contract) | § 1.8 (index) | R7 |
| FR-015 (history retention) | § 1.7 (FK behavior) | R9 |
| FR-016 (Remap audit semantics) | § 1.4 (metadata shape) + § 6.3 (transitions) | R11 |
