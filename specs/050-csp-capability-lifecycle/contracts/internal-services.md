# Phase 1 — Internal Service Contracts: CSP-Inherited Capability Lifecycle

**Feature**: 050-csp-capability-lifecycle
**Plan**: [../plan.md](../plan.md)
**Data model**: [../data-model.md](../data-model.md)
**Spec**: [../spec.md](../spec.md)
**Date**: 2026-05-22

This document pins the C# service contracts behind the HTTP endpoints
documented in [http-api.md](./http-api.md). The contracts live in:

- `src/Ato.Copilot.Core/Interfaces/Tenancy/ICspInheritedComponentService.cs`
  — **EXTENDED** (`AddCapabilityAsync` signature change; new
  `ReparentCapabilityAsync` method)
- `src/Ato.Copilot.Core/Interfaces/Tenancy/ICapabilityHistoryService.cs`
  — **NEW**

Implementations:

- `src/Ato.Copilot.Core/Services/Tenancy/CspInheritedComponentService.cs`
  — **EXTENDED**
- `src/Ato.Copilot.Core/Services/Tenancy/CapabilityHistoryService.cs`
  — **NEW**

DI registration in `Ato.Copilot.Core.DependencyInjection`:

- `services.AddScoped<ICapabilityHistoryService, CapabilityHistoryService>();`
- `CspInheritedComponentService` already registered — no change to its
  registration line.

---

## 1. `ICapabilityHistoryService` (new)

### 1.1 Purpose

Single chokepoint for writing and reading `CapabilityHistoryEvent`
rows. Encapsulates the tenant-scoping invariant
(`TenantId == caller.tenantId` on every read), the immutability
invariant (no update / delete methods), and the JSON-shape rules from
[../data-model.md § 1.4](../data-model.md).

### 1.2 Interface

```csharp
using Ato.Copilot.Core.Models.Tenancy;

namespace Ato.Copilot.Core.Interfaces.Tenancy;

/// <summary>
/// Append-only writer + paginated reader for
/// <see cref="CapabilityHistoryEvent"/> rows. Feature 050 FR-004 / FR-005 /
/// FR-014 / FR-015 / FR-016.
/// </summary>
/// <remarks>
/// All callers MUST be scoped to a CSP-Admin tenant context. The service
/// is intentionally devoid of update / delete methods — history rows are
/// immutable once written (FR-004).
/// </remarks>
public interface ICapabilityHistoryService
{
    /// <summary>
    /// Append a new history row inside the caller's ambient transaction.
    /// Caller is responsible for opening / committing the transaction;
    /// this method calls <c>AddAsync</c> only — it does NOT call
    /// <c>SaveChangesAsync</c>. This is what makes the write atomic with
    /// the state change that triggered it (R1).
    /// </summary>
    /// <param name="db">
    /// The <c>AtoCopilotContext</c> instance hosting the open transaction.
    /// The service does not own the lifetime.
    /// </param>
    /// <param name="capabilityId">FK to the parent capability.</param>
    /// <param name="tenantId">CSP tenant performing the operation.</param>
    /// <param name="eventType">One of six lifecycle events.</param>
    /// <param name="actorOid">Caller's <c>oid</c> claim.</param>
    /// <param name="summary">Human-readable description, ≤ 500 chars.</param>
    /// <param name="metadata">
    /// Optional structured payload, serialized to JSON. Pass <c>null</c>
    /// when no metadata applies. Shape rules per data-model.md § 1.4.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly-created event row (not yet persisted).</returns>
    Task<CapabilityHistoryEvent> AppendAsync(
        AtoCopilotContext db,
        Guid capabilityId,
        Guid tenantId,
        CapabilityHistoryEventType eventType,
        string actorOid,
        string summary,
        object? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// List events for one capability scoped to one tenant, ordered by
    /// <see cref="CapabilityHistoryEvent.OccurredAt"/> descending then
    /// <see cref="CapabilityHistoryEvent.Id"/> descending for stable
    /// pagination.
    /// </summary>
    /// <param name="capabilityId">Capability whose history to fetch.</param>
    /// <param name="tenantId">Caller's tenant (filter, FR-013).</param>
    /// <param name="page">1-based page index. Clamped to <c>≥ 1</c>.</param>
    /// <param name="pageSize">
    /// Page size. Clamped to <c>[1, 200]</c>. Default 50 enforced at the
    /// endpoint layer, not here.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Page of events + total count.</returns>
    Task<CapabilityHistoryPage> ListAsync(
        Guid capabilityId,
        Guid tenantId,
        int page,
        int pageSize,
        CancellationToken ct = default);
}

/// <summary>
/// Page-of-events DTO returned by <see cref="ICapabilityHistoryService.ListAsync"/>.
/// </summary>
public sealed record CapabilityHistoryPage(
    IReadOnlyList<CapabilityHistoryEvent> Items,
    int Page,
    int PageSize,
    int Total);
```

### 1.3 Contract notes

- **`AppendAsync` does NOT call `SaveChangesAsync`** (R1). The caller —
  always `CspInheritedComponentService` or a Remap pipeline — owns the
  enclosing transaction so the audit row and the state change commit
  atomically. Tests will assert this contract by mocking `db` and
  asserting only `AddAsync` is called.
- **`AppendAsync` performs no role check**. It is a pure writer; role
  and tenant gates live at the endpoint layer. The service is `internal`
  in spirit (consumed only by `CspInheritedComponentService` and the
  Remap pipeline) but is declared `public` so unit tests in
  `Ato.Copilot.Tests.Unit` can construct it directly.
- **`metadata` serialization**: `JsonSerializer.Serialize(metadata,
  options)` with the default `JsonSerializerOptions` already used by
  the codebase (camelCase, ignore-null). `null` metadata → `null`
  column; **not** `"null"` string.
- **`ListAsync` MUST filter by `TenantId`** as the first predicate (per
  FR-013 / R6). The composite index
  `(TenantId, CapabilityId, OccurredAt DESC)` is leading-`TenantId` to
  match this query.
- **`ListAsync` returns 200 with empty `items`** on no-match. There is
  no `NotFound` thrown — the endpoint layer is responsible for
  verifying capability existence before calling this method.
- **No `DeleteAsync` / `UpdateAsync` exposed**. A unit test enumerates
  the interface methods and asserts the set is exactly
  `{ AppendAsync, ListAsync }`.

### 1.4 Implementation outline

```csharp
public sealed class CapabilityHistoryService : ICapabilityHistoryService
{
    private readonly ILogger<CapabilityHistoryService> _log;
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public CapabilityHistoryService(ILogger<CapabilityHistoryService> log)
        => _log = log;

    public async Task<CapabilityHistoryEvent> AppendAsync(
        AtoCopilotContext db,
        Guid capabilityId,
        Guid tenantId,
        CapabilityHistoryEventType eventType,
        string actorOid,
        string summary,
        object? metadata = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorOid);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        if (summary.Length > 500)
            throw new ArgumentException("Summary exceeds 500 chars.", nameof(summary));

        var evt = new CapabilityHistoryEvent
        {
            Id = Guid.NewGuid(),
            CapabilityId = capabilityId,
            TenantId = tenantId,
            EventType = eventType,
            ActorOid = actorOid,
            OccurredAt = DateTimeOffset.UtcNow,
            Summary = summary,
            MetadataJson = metadata is null
                ? null
                : JsonSerializer.Serialize(metadata, _json),
        };

        await db.CapabilityHistoryEvents.AddAsync(evt, ct).ConfigureAwait(false);
        // Intentionally no SaveChangesAsync — caller's transaction owns it.
        return evt;
    }

    public async Task<CapabilityHistoryPage> ListAsync(
        Guid capabilityId,
        Guid tenantId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var p = Math.Max(1, page);
        var ps = Math.Clamp(pageSize, 1, 200);

        using var db = _contextFactory.CreateDbContext();   // ← actually IDbContextFactory<AtoCopilotContext>
        var query = db.CapabilityHistoryEvents
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId
                     && e.CapabilityId == capabilityId);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Skip((p - 1) * ps)
            .Take(ps)
            .ToListAsync(ct).ConfigureAwait(false);

        return new CapabilityHistoryPage(items, p, ps, total);
    }
}
```

> Implementation note: `ListAsync` actually takes
> `IDbContextFactory<AtoCopilotContext>` in the constructor (per the
> existing pattern). The snippet above elides the constructor wiring for
> brevity — the real implementation matches `CspInheritedComponentService`.

---

## 2. `ICspInheritedComponentService` — extensions

### 2.1 `AddCapabilityAsync` — signature change (FR-001)

#### 2.1.1 Before

```csharp
Task<CspInheritedCapability> AddCapabilityAsync(
    Guid componentId,
    string name,
    string description,
    IReadOnlyList<string> mappedNistControlIds,
    string actor,
    CancellationToken ct = default);
```

#### 2.1.2 After

```csharp
/// <summary>
/// Manually add a capability to an existing
/// <see cref="CspInheritedComponent"/>. Default behavior (FR-001 in
/// Feature 050) is to persist the new row as
/// <see cref="CspInheritedCapabilityStatus.NeedsReview"/> with
/// <see cref="MappedBy.User"/> so the creator can choose to review later
/// (allowed under FR-010 self-review). Pass
/// <paramref name="markMappedImmediately"/> = <c>true</c> to opt back
/// into the legacy auto-map-on-create behavior — the row is stamped
/// with <see cref="CspInheritedCapabilityStatus.Mapped"/>, reviewer
/// metadata is set to the creator, and TWO history rows
/// (<see cref="CapabilityHistoryEventType.Created"/> +
/// <see cref="CapabilityHistoryEventType.Reviewed"/>) are written in
/// the same transaction. Throws <see cref="KeyNotFoundException"/> if
/// <paramref name="componentId"/> does not exist.
/// </summary>
Task<CspInheritedCapability> AddCapabilityAsync(
    Guid componentId,
    string name,
    string description,
    IReadOnlyList<string> mappedNistControlIds,
    string actor,
    bool markMappedImmediately = false,
    CancellationToken ct = default);
```

The new parameter has a default value so existing call sites compile
unchanged; existing callers get the **new** default (NeedsReview).

#### 2.1.3 Implementation outline

```csharp
public async Task<CspInheritedCapability> AddCapabilityAsync(
    Guid componentId,
    string name,
    string description,
    IReadOnlyList<string> mappedNistControlIds,
    string actor,
    bool markMappedImmediately = false,
    CancellationToken ct = default)
{
    // ... existing validation: name, description, mappedNistControlIds,
    //     componentId existence (KeyNotFoundException if missing) ...

    var tenantId = _tenantContext.TenantId;
    var nowUtc = DateTimeOffset.UtcNow;

    await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
    await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

    var capability = new CspInheritedCapability
    {
        Id = Guid.NewGuid(),
        CspInheritedComponentId = componentId,
        Name = name.Trim(),
        Description = description.Trim(),
        MappedNistControlIds = mappedNistControlIds.ToList(),
        MappedBy = MappedBy.User,
        MappingConfidence = null,
        Status = CspInheritedCapabilityStatus.NeedsReview,
        CreatedAt = nowUtc,
        CreatedBy = actor,
    };

    if (markMappedImmediately)
    {
        capability.Status = CspInheritedCapabilityStatus.Mapped;
        capability.ReviewedAt = nowUtc;
        capability.ReviewedBy = actor;
        capability.ReviewerNote = "Mapped on create by creator.";
    }

    await db.CspInheritedCapabilities.AddAsync(capability, ct).ConfigureAwait(false);

    // History row #1: Created
    await _history.AppendAsync(
        db, capability.Id, tenantId,
        CapabilityHistoryEventType.Created,
        actorOid: actor,
        summary: "Capability manually created.",
        metadata: markMappedImmediately ? new { markedMappedImmediately = true } : null,
        ct).ConfigureAwait(false);

    // History row #2: Reviewed (only when override)
    if (markMappedImmediately)
    {
        await _history.AppendAsync(
            db, capability.Id, tenantId,
            CapabilityHistoryEventType.Reviewed,
            actorOid: actor,
            summary: "Reviewed and approved at creation time.",
            metadata: new { reviewerNote = "Mapped on create by creator." },
            ct).ConfigureAwait(false);
    }

    await db.SaveChangesAsync(ct).ConfigureAwait(false);
    await tx.CommitAsync(ct).ConfigureAwait(false);
    return capability;
}
```

### 2.2 `ReparentCapabilityAsync` — NEW (FR-002, FR-012)

#### 2.2.1 Signature

```csharp
/// <summary>
/// Reparent a single <see cref="CspInheritedCapability"/> from its
/// current <see cref="CspInheritedComponent"/> to
/// <paramref name="targetComponentId"/>, scoped to the caller's
/// tenant. Resets <see cref="CspInheritedCapability.Status"/> to
/// <see cref="CspInheritedCapabilityStatus.NeedsReview"/>, clears
/// reviewer metadata, and writes one
/// <see cref="CapabilityHistoryEventType.Moved"/> audit event in the
/// same transaction. Preserves <c>Name</c>, <c>Description</c>,
/// <c>MappedNistControlIds</c>, <c>MappingConfidence</c>,
/// <c>MappedBy</c>, <c>CreatedAt</c>, <c>CreatedBy</c>.
/// </summary>
/// <param name="componentId">Current parent component.</param>
/// <param name="capabilityId">Capability to move.</param>
/// <param name="targetComponentId">
/// Destination component. MUST be ≠ <paramref name="componentId"/>,
/// MUST exist in the caller's tenant, and MUST NOT be Archived.
/// </param>
/// <param name="rowVersion">
/// Caller-supplied current <c>RowVersion</c>. Required (per
/// http-api.md § 2.2.2). A stale stamp triggers
/// <see cref="DbUpdateConcurrencyException"/>.
/// </param>
/// <param name="actor">Caller's <c>oid</c> claim.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>The reparented capability with refreshed <c>RowVersion</c>.</returns>
/// <exception cref="KeyNotFoundException">
/// Thrown when <paramref name="componentId"/>, <paramref name="capabilityId"/>,
/// or <paramref name="targetComponentId"/> cannot be resolved in the
/// caller's tenant — OR when the target is Archived. The endpoint
/// surface maps this to HTTP 404 to avoid leaking existence.
/// </exception>
/// <exception cref="ArgumentException">
/// Thrown when <paramref name="targetComponentId"/> equals
/// <paramref name="componentId"/>.
/// </exception>
/// <exception cref="DbUpdateConcurrencyException">
/// Thrown on stale <paramref name="rowVersion"/>. Endpoint maps to HTTP 412.
/// </exception>
Task<CspInheritedCapability> ReparentCapabilityAsync(
    Guid componentId,
    Guid capabilityId,
    Guid targetComponentId,
    byte[] rowVersion,
    string actor,
    CancellationToken ct = default);
```

Note: `rowVersion` is **non-nullable** for this method. The endpoint
already validates the `If-Match` header is present (http-api.md § 2.5
`VALIDATION_ERROR` row), so the service can assume non-null. This is
deliberately stricter than `UpdateCapabilityAsync`'s nullable
`rowVersion` — reparent is too destructive to allow last-write-wins.

#### 2.2.2 Implementation outline

```csharp
public async Task<CspInheritedCapability> ReparentCapabilityAsync(
    Guid componentId,
    Guid capabilityId,
    Guid targetComponentId,
    byte[] rowVersion,
    string actor,
    CancellationToken ct = default)
{
    ArgumentNullException.ThrowIfNull(rowVersion);
    if (targetComponentId == componentId)
        throw new ArgumentException(
            "Target component is the capability's current component.",
            nameof(targetComponentId));

    var tenantId = _tenantContext.TenantId;
    await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
    await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

    // 1. Target eligibility (single query — tenant-scoped, not-archived).
    var target = await db.CspInheritedComponents
        .AsNoTracking()
        .Where(c => c.Id == targetComponentId
                 && c.TenantId == tenantId
                 && c.Status != CspInheritedComponentStatus.Archived)
        .Select(c => new { c.Id, c.Name })
        .SingleOrDefaultAsync(ct).ConfigureAwait(false)
        ?? throw new KeyNotFoundException(
            $"Target component {targetComponentId} not found or archived.");

    // 2. Source capability (tenant-scoped via component).
    var capability = await db.CspInheritedCapabilities
        .Include(c => c.CspInheritedComponent)
        .Where(c => c.Id == capabilityId
                 && c.CspInheritedComponentId == componentId
                 && c.CspInheritedComponent.TenantId == tenantId)
        .SingleOrDefaultAsync(ct).ConfigureAwait(false)
        ?? throw new KeyNotFoundException(
            $"Capability {capabilityId} not found under component {componentId}.");

    var fromComponentName = capability.CspInheritedComponent.Name;
    db.Entry(capability).Property(c => c.RowVersion).OriginalValue = rowVersion;

    // 3. Apply reparent.
    capability.CspInheritedComponentId = targetComponentId;
    capability.Status = CspInheritedCapabilityStatus.NeedsReview;
    capability.ReviewedAt = null;
    capability.ReviewedBy = null;
    capability.ReviewerNote = null;
    capability.MappingFailureReason =
        "Moved to a new component; re-review required.";

    // 4. History row.
    await _history.AppendAsync(
        db, capability.Id, tenantId,
        CapabilityHistoryEventType.Moved,
        actorOid: actor,
        summary: $"Moved from '{fromComponentName}' to '{target.Name}'.",
        metadata: new
        {
            fromComponentId = componentId,
            toComponentId = targetComponentId,
        },
        ct).ConfigureAwait(false);

    // 5. Commit — DbUpdateConcurrencyException bubbles to endpoint as 412.
    await db.SaveChangesAsync(ct).ConfigureAwait(false);
    await tx.CommitAsync(ct).ConfigureAwait(false);

    return capability;
}
```

### 2.3 Existing methods — history-write changes

The following existing methods on `ICspInheritedComponentService` MUST
be extended to write a history row in the same transaction as their
state change. **No signature changes.**

| Method | Event type written | Summary template | Metadata |
|---|---|---|---|
| `UpdateCapabilityAsync` | `Edited` | `"Capability edited."` | `{ fields: ["name", "description", "mappedNistControlIds"].Filter(changed) }` |
| `ReviewCapabilityAsync` | `Reviewed` | `"Reviewed and approved."` | `reviewerNote is null ? null : new { reviewerNote }` |
| `ArchiveCapabilityAsync` | `Archived` | `"Capability archived."` | `null` |
| `RemapAsync` (per-changed-capability inside the loop) | per R11 — see § 2.4 | per R11 | per R11 |

The newly-introduced `AddCapabilityAsync` already writes its event(s)
per § 2.1.3 above.

For each method:

1. Open a transaction (existing methods may not currently open one — if
   not, wrap the existing single-SaveChanges into a transaction).
2. Apply the state change exactly as today.
3. Call `_history.AppendAsync(db, ..., ct)` before `SaveChangesAsync`.
4. `SaveChangesAsync` + commit.

### 2.4 `RemapAsync` — extended (FR-016, R11)

#### 2.4.1 Behavioral contract

Inside the existing `RemapAsync` body:

1. Generate one `remapRunId = Guid.NewGuid()` at method entry, before any
   capability is touched. Hold it in a local variable for the duration
   of the run.
2. For each capability in the pipeline result:
   - **AI created new** → insert capability + append `Created` event with
     metadata `{ remapRunId, source = "Remap" }`.
   - **AI changed existing AI row** → update capability + append `Edited`
     event with metadata `{ remapRunId, source = "Remap" }`.
   - **AI removed existing AI row** → set `Status = Archived` + append
     `Archived` event with metadata `{ remapRunId, source = "Remap" }`.
   - **Existing `MappedBy = User` row preserved** → **no history row.**
   - **Existing AI row identical to AI re-output** → **no history row.**
3. All history rows in step 2 are written via `_history.AppendAsync`
   inside the same transaction as the capability state changes.
4. The `actor` passed to `AppendAsync` is the
   `actor` parameter on `RemapAsync` — the CSP-Admin OID who clicked
   Continue in the Advanced disclosure (FR-008 + Q5).

#### 2.4.2 Signature

**Unchanged.** The `remapRunId` is an internal correlator, not part of
the service contract. The HTTP endpoint does not see it.

### 2.5 `CapabilityHistoryService` is **NOT** wired into any other CSP method

Other CSP-inherited component methods (`CreateAsync`, `UpdateAsync`,
`PublishAsync`, `ArchiveAsync` on the *component* itself) write to a
component-level audit log if Feature 048 already has one, or nothing
otherwise. They do **not** call `ICapabilityHistoryService` — this
service is strictly for **capability**-level events.

---

## 3. Wiring & DI

### 3.1 `Ato.Copilot.Core.DependencyInjection`

```csharp
services.AddScoped<ICapabilityHistoryService, CapabilityHistoryService>();
// CspInheritedComponentService registration UNCHANGED — its constructor
// gains one new injected parameter (ICapabilityHistoryService) which
// DI resolves automatically.
```

### 3.2 `CspInheritedComponentService` constructor

The constructor signature gains one parameter:

```csharp
public CspInheritedComponentService(
    IDbContextFactory<AtoCopilotContext> contextFactory,
    ITenantContext tenantContext,
    ICapabilityHistoryService history,    // ← NEW
    ICspProfileService profileService,
    ICapabilityMappingService mappingService,
    INistControlCatalog nistCatalog,
    ILogger<CspInheritedComponentService> logger)
{
    _contextFactory = contextFactory;
    _tenantContext = tenantContext;
    _history = history;                   // ← NEW
    _profileService = profileService;
    _mappingService = mappingService;
    _nistCatalog = nistCatalog;
    _logger = logger;
}
```

The exact constructor parameter list above is illustrative — the real
class may have a different existing parameter set. The contract is:
"one new `ICapabilityHistoryService` parameter added."

---

## 4. Concurrency & transaction discipline

| Method | Transaction | History writes |
|---|---|---|
| `AddCapabilityAsync` (no override) | NEW transaction | 1 row (`Created`) |
| `AddCapabilityAsync` (markMappedImmediately) | NEW transaction | 2 rows (`Created` + `Reviewed`) |
| `UpdateCapabilityAsync` | wrap existing path in a transaction if not already | 1 row (`Edited`) |
| `ReviewCapabilityAsync` | wrap existing path in a transaction if not already | 1 row (`Reviewed`) |
| `ArchiveCapabilityAsync` | wrap existing path in a transaction if not already | 1 row (`Archived`) |
| `ReparentCapabilityAsync` (NEW) | NEW transaction | 1 row (`Moved`) |
| `RemapAsync` | wrap existing path in a transaction if not already | N rows (variable; per R11) |

**Rule**: every public method on `ICspInheritedComponentService` that
mutates a capability MUST execute its state change and its
`AppendAsync` call inside one transaction. Failure to commit MUST roll
back both the state change and the history row.

A unit test asserts this by simulating a `SaveChangesAsync` exception
and verifying the history row is not visible after the throw.

---

## 5. Cross-reference matrix

| FR | Service member | Section |
|---|---|---|
| FR-001 | `AddCapabilityAsync` (signature change) | § 2.1 |
| FR-002 | `ReparentCapabilityAsync` (new) | § 2.2 |
| FR-004 | `ICapabilityHistoryService.AppendAsync` (new) | § 1 |
| FR-005 | `ICapabilityHistoryService.ListAsync` (new) | § 1 |
| FR-007 | `RemapAsync` (preserves `mappedBy = User`) | § 2.4 (already enforced pre-050; this feature only adds the audit row) |
| FR-012 | `ReparentCapabilityAsync` requires non-null `rowVersion` | § 2.2.2 |
| FR-013 | All reads filter by `tenantId` | § 1.3, § 2.2.2 |
| FR-014 | `ListAsync` clamp + ordering + stable secondary sort | § 1.3, § 1.4 |
| FR-015 | FK relationships in data-model.md § 1.7; service performs no `DELETE` on history | § 1.3 (no `DeleteAsync`) |
| FR-016 | `RemapAsync` extension + `remapRunId` correlator | § 2.4 |
