# Phase 1: Internal Service Contracts — Unified RMF Role Assignments

**Branch**: `049-unified-rmf-role-assignments`
**Date**: 2026-05-19

This document defines the C# interfaces and DI registration shape for every new internal service. Implementation details live in code; this file is the contract.

---

## Service Inventory

| Service | Project | Lifetime | Drives |
|---|---|---|---|
| `IUnifiedRoleReader` | `Ato.Copilot.Core` | Scoped | FR-002 / FR-003 / FR-004 — precedence-chain reader |
| `IRoleAuthorizationService` | `Ato.Copilot.Core` | Singleton | FR-027 — RBAC matrix evaluator |
| `ISoDConflictDetector` | `Ato.Copilot.Core` | Scoped | FR-026 — DoDI 8510.01 detection |
| `IOrganizationRoleFanoutQueue` | `Ato.Copilot.Core` | Singleton | FR-028 — `Channel<PropagationIntent>` facade |
| `OrganizationRoleFanoutWorker` | `Ato.Copilot.Mcp` | Hosted (`BackgroundService`) | FR-028 — drains the queue + startup reconciliation |
| `RoleMetrics` | `Ato.Copilot.Core` | Singleton | FR-018 / FR-019 / FR-026 / SC-011 — telemetry |

All new services accept `CancellationToken` on every async method (Constitution § Performance Standards).

---

## 1. `IUnifiedRoleReader`

**File**: `src/Ato.Copilot.Core/Services/Roles/IUnifiedRoleReader.cs`
**Lifetime**: `Scoped` (one per HTTP request — uses the request's `AtoCopilotContext`)
**Tenant scope**: every method takes `Guid tenantId` explicitly; no ambient state.

```csharp
namespace Ato.Copilot.Core.Services.Roles;

/// <summary>
/// Single read facade over the three role-data sources
/// (per-system override → inherited → org-level fallback → legacy).
/// Replaces the direct table reads in SystemProfileService and exposes
/// the same precedence chain to the dashboard.
/// </summary>
public interface IUnifiedRoleReader
{
    /// <summary>
    /// Returns the full 7-role state for the given system, with each role's
    /// source (override / inherited / org-fallback / legacy / not-assigned)
    /// surfaced for UI affordance.
    /// </summary>
    Task<SystemRoleSnapshot> GetSystemRolesAsync(
        Guid tenantId,
        string registeredSystemId,
        CancellationToken ct);

    /// <summary>
    /// Convenience read for the banner: returns the resolved MissionOwner
    /// or null. Implemented as a 1-role projection of GetSystemRolesAsync
    /// to keep precedence logic in exactly one place.
    /// </summary>
    Task<ResolvedRoleAssignment?> GetMissionOwnerAsync(
        Guid tenantId,
        string registeredSystemId,
        CancellationToken ct);
}

public readonly record struct SystemRoleSnapshot(
    Guid TenantId,
    string RegisteredSystemId,
    IReadOnlyList<ResolvedRoleAssignment> Roles);

public readonly record struct ResolvedRoleAssignment(
    RmfRole Role,
    Guid? PersonId,
    string? PersonDisplayName,
    RoleAssignmentSource Source,
    Guid? OrgRoleId);

public enum RoleAssignmentSource
{
    NotAssigned,
    Override,        // SystemRoleAssignment with IsInherited=false
    Inherited,       // SystemRoleAssignment with IsInherited=true
    OrgFallback,     // OrganizationRoleAssignment row exists; inherited row not yet materialized
    Legacy,          // RmfRoleAssignment row only
}
```

**Implementation contract** (encoded in `UnifiedRoleReader.cs`):

1. The implementation MUST issue at most **one** SQL query per `GetSystemRolesAsync` call (left-join across the three tables, filtered by tenant). N+1 queries are a contract violation.
2. The implementation MUST respect EF Core's tenant query filter (Feature 048). A test in `Tests.Integration/Roles/TenantIsolationRolesTests` asserts cross-tenant queries return `NotAssigned` for every role.
3. The implementation MUST resolve precedence per [data-model.md § Read-time precedence](../data-model.md#read-time-precedence-fr-003-encoded-by-iunifiedrolereader).
4. For `IsPrimary` tie-breaking on the org-fallback step: the row with `IsPrimary=true` wins; ties are broken by most-recent `CreatedAt`.

**Test surface**: `UnifiedRoleReaderTests` (unit), `TenantIsolationRolesTests` (integration).

---

## 2. `IRoleAuthorizationService`

**File**: `src/Ato.Copilot.Core/Services/Roles/IRoleAuthorizationService.cs`
**Lifetime**: `Singleton` (pure-functional, no state, no DB I/O).

```csharp
namespace Ato.Copilot.Core.Services.Roles;

/// <summary>
/// FR-027 role-tiered authorization matrix. Pure-functional;
/// the caller's effective role and the target role are inputs.
/// No DB I/O. No HTTP I/O. Trivially testable.
/// </summary>
public interface IRoleAuthorizationService
{
    AuthorizationResult Authorize(
        CallerEffectiveRole caller,       // the caller's resolved Org-scope identity
        RmfRole targetRole,
        bool isBootstrapSession);         // true only when the wizard is creating the FIRST OrganizationRoleAssignment
}

public readonly record struct AuthorizationResult(
    bool Allowed,
    string? DeniedReason);   // null when Allowed=true

/// <summary>
/// Caller's resolved Org-scope identity for FR-027 authorization. Combines two
/// orthogonal facts the resolver computes from the tenant's role tables:
///   • <see cref="RmfRole"/>  — the highest-privileged RmfRole the caller currently
///     holds, or <c>null</c> when the caller holds none.
///   • <see cref="IsTenantAdministrator"/> — <c>true</c> when the caller holds an
///     active <see cref="Models.Onboarding.OrganizationRole.Administrator"/> assignment.
///     Administrator is an Org-scope-only role with no RmfRole equivalent; it grants
///     full assign privileges (bypass) but does NOT appear in OSCAL exports.
/// </summary>
/// <remarks>
/// The two flags are independent: a tenant Administrator may ALSO hold an RmfRole
/// (e.g., the founder is both Administrator + ISSM). In that case both fields are
/// populated. <see cref="IsTenantAdministrator"/> short-circuits to Allowed before
/// the RmfRole matrix is consulted.
/// </remarks>
public readonly record struct CallerEffectiveRole(
    RmfRole? RmfRole,
    bool IsTenantAdministrator)
{
    /// <summary>Singleton "no roles" instance — caller holds neither an RmfRole nor Administrator.</summary>
    public static CallerEffectiveRole None => new(null, false);
}
```

**Matrix** (closed; encoded as `static readonly ImmutableDictionary<RmfRole, ImmutableHashSet<RmfRole>>` — 6 RmfRole keys only):

| Caller's effective identity | May assign target roles |
|---|---|
| `IsTenantAdministrator=true` (bypass) | all 6 RmfRole values |
| `RmfRole.Issm` | all 6 RmfRole values *except* `AuthorizingOfficial` |
| `RmfRole.Isso` | `MissionOwner`, `SystemOwner` |
| `RmfRole.AuthorizingOfficial`, `Sca`, `SystemOwner`, `MissionOwner`, *or* `CallerEffectiveRole.None` | none |

**Design note (Feature 049 implementation, 2026-05-20)** — The `Administrator` row in the original spec drafts could not be modeled inside the matrix because `RmfRole` is frozen at 6 values per FR-020 and contains no `Administrator` member. Rather than pushing the Administrator check into every endpoint (a security regression risk if any new endpoint forgets it), the resolved caller identity is wrapped in `CallerEffectiveRole`, which carries an explicit `IsTenantAdministrator` flag. `Authorize` short-circuits to Allowed when that flag is set BEFORE evaluating the RmfRole matrix. The matrix itself stays a clean 6-key `ImmutableDictionary` and the security decision lives entirely inside `IRoleAuthorizationService`.

**Bootstrap exception**: when `isBootstrapSession == true`, the result is `(true, null)` for any target role. The wizard service sets the flag exactly once per session (the first `OrganizationRoleAssignment` write); subsequent writes within the same session evaluate against the matrix.

**Resolving the caller's effective identity** (NOT this service's responsibility): see [§ 6 `ICallerEffectiveRoleResolver`](#6-icallereffectiveroleresolver). The endpoint layer calls `ResolveAsync` exactly once per request before invoking `Authorize`.

**Test surface**: `RoleAuthorizationServiceTests` (unit; 36-cell RmfRole×RmfRole matrix + 6 null-caller cells + 6 Administrator-bypass cells + 6 bootstrap-bypass cells = 54 scenarios), `RoleAuthorizationMatrixCoverageTests` (integration; HTTP-level enforcement).

---

## 3. `ISoDConflictDetector`

**File**: `src/Ato.Copilot.Core/Services/Roles/ISoDConflictDetector.cs`
**Lifetime**: `Scoped` (uses the request's `AtoCopilotContext`).

```csharp
namespace Ato.Copilot.Core.Services.Roles;

/// <summary>
/// FR-026 DoDI 8510.01 Enclosure 3 separation-of-duties detection.
/// Pairs encoded:
///   AuthorizingOfficial conflicts with: SystemOwner, Issm, Isso
///   Sca                  conflicts with: Issm, Isso, SystemOwner
/// Read-only; never modifies state.
/// </summary>
public interface ISoDConflictDetector
{
    Task<IReadOnlyList<SoDWarning>> DetectAsync(
        Guid tenantId,
        Guid personId,
        RmfRole targetRole,
        CancellationToken ct);
}

public readonly record struct SoDWarning(
    string Code,                         // always "SOD_VIOLATION" (closed enum for now)
    string Message,
    (RmfRole Existing, RmfRole Target) RoleConflict,
    string DodiReference,                // e.g. "DoDI 8510.01 Enclosure 3 § 4.b"
    string SuggestedAction);
```

**Implementation contract**:

1. Single query: `select Role from OrganizationRoleAssignments where TenantId=@tid and PersonId=@pid and RemovedAt is null` (≤6 rows returned, one per role they hold).
2. Cross-product against the static SoD-pairs table; emit one `SoDWarning` per conflict.
3. MUST execute inside the same `DbContext` scope as the pending write so warnings reflect the state the write commits into (read-committed-snapshot semantics on SQL Server; SQLite's default is serializable single-writer).

**Test surface**: `SoDConflictDetectorTests` (unit, in-memory provider).

---

## 4. `IOrganizationRoleFanoutQueue` + `OrganizationRoleFanoutWorker`

**Files**:

- `src/Ato.Copilot.Core/Services/Roles/IOrganizationRoleFanoutQueue.cs`
- `src/Ato.Copilot.Core/Services/Roles/OrganizationRoleFanoutQueue.cs`
- `src/Ato.Copilot.Mcp/Workers/OrganizationRoleFanoutWorker.cs`

**Queue lifetime**: `Singleton` (one `Channel<PropagationIntent>` per process).
**Worker lifetime**: `Hosted` via `services.AddHostedService<OrganizationRoleFanoutWorker>()`.

```csharp
namespace Ato.Copilot.Core.Services.Roles;

public interface IOrganizationRoleFanoutQueue
{
    /// <summary>
    /// Enqueue a propagation intent. Returns when the intent is on the queue
    /// (does NOT wait for fan-out completion). Bounded; blocks the caller
    /// momentarily under load (BoundedChannelFullMode.Wait).
    /// </summary>
    ValueTask EnqueueAsync(PropagationIntent intent, CancellationToken ct);

    /// <summary>Reader half for the worker. Single consumer expected.</summary>
    ChannelReader<PropagationIntent> Reader { get; }
}

public readonly record struct PropagationIntent(
    Guid TenantId,
    Guid OrganizationRoleAssignmentId,
    RmfRole TargetRole,
    Guid PersonId,
    DateTimeOffset EnqueuedAt);
```

**`OrganizationRoleFanoutQueue` implementation** (constructor only):

```csharp
public OrganizationRoleFanoutQueue()
{
    _channel = Channel.CreateBounded<PropagationIntent>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
}
```

**`OrganizationRoleFanoutWorker` responsibilities**:

```csharp
namespace Ato.Copilot.Mcp.Workers;

public sealed class OrganizationRoleFanoutWorker : BackgroundService
{
    // Constructor takes:
    //   IOrganizationRoleFanoutQueue queue
    //   IServiceScopeFactory scopeFactory   (for per-iteration AtoCopilotContext)
    //   RoleMetrics metrics
    //   ILogger<OrganizationRoleFanoutWorker> logger

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Startup reconciliation sweep (idempotent).
        await ReconcileMissingInheritedRowsAsync(stoppingToken);

        // 2. Drain the channel forever.
        await foreach (var intent in queue.Reader.ReadAllAsync(stoppingToken))
        {
            await PropagateAsync(intent, stoppingToken);
        }
    }
}
```

**`PropagateAsync(intent)` contract**:

1. Open a scope; resolve `AtoCopilotContext`.
2. Load all active `RegisteredSystem.Id` values for `intent.TenantId` (single query, paged in 100-system batches to bound memory).
3. For each system, check whether a non-removed `SystemRoleAssignment` already exists with matching `SourceOrganizationRoleAssignmentId = intent.OrganizationRoleAssignmentId` OR a non-inherited override for the same `(systemId, role)`. If either is present, skip (idempotent).
4. Otherwise insert a `SystemRoleAssignment` with `IsInherited=true`, `SourceOrganizationRoleAssignmentId=intent.OrganizationRoleAssignmentId`, `RegisteredSystemId=...`, `PersonId=intent.PersonId`, `Role=intent.TargetRole`.
5. Commit per-batch via `SaveChangesAsync` so a mid-flight crash leaves earlier batches durable. The remaining work re-derives on startup sweep.
6. Emit one Serilog structured event per `PropagateAsync` call:

```text
@Level: Information
@Message: "OrganizationRoleFanout completed"
TenantId: ...
OrganizationRoleAssignmentId: ...
TargetRole: ...
SystemsProcessed: 187
SystemsSkipped: 13
DurationMs: 4218
```

7. Record `org_role_propagation_duration_seconds` histogram via `RoleMetrics` with the bucketed `systems_bucket` label.

**`ReconcileMissingInheritedRowsAsync(ct)` contract**:

1. Single tenant-scope-aware query that emits one `PropagationIntent` per `(active OrganizationRoleAssignment row, tenant)` whose set of systems is not fully covered by inherited rows. Implementation uses EF Core's raw SQL escape hatch for the `NOT EXISTS` correlated subquery to keep the query single-roundtrip.
2. Each intent is enqueued onto the same `IOrganizationRoleFanoutQueue` the runtime write path uses — no separate code path.
3. Runs once per process start. Re-entering the sweep mid-runtime is prohibited (deadlock risk on the single-reader channel).

**Test surface**: `OrganizationRoleFanoutQueueTests` (unit, channel contract), `OrganizationRoleFanoutWorkerTests` (integration; enqueue 500 systems, drain, assert convergence; idempotency assertion via re-run).

---

## 5. `RoleMetrics`

**File**: `src/Ato.Copilot.Core/Observability/RoleMetrics.cs`
**Lifetime**: `Singleton`.

```csharp
namespace Ato.Copilot.Core.Observability;

public sealed class RoleMetrics : IDisposable
{
    public const string MeterName = "Ato.Copilot";        // shared with HttpMetrics / ToolMetrics

    private readonly Meter _meter;
    private readonly Counter<long> _legacyEndpointCalls;
    private readonly Counter<long> _legacyEndpointBypass;
    private readonly Counter<long> _sodWarnings;
    private readonly Histogram<double> _propagationDuration;

    public RoleMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        _legacyEndpointCalls     = _meter.CreateCounter<long>("legacy_role_endpoint_call_total", description: "Calls to deprecated /api/dashboard/systems/{systemId}/roles");
        _legacyEndpointBypass    = _meter.CreateCounter<long>("legacy_role_endpoint_bypass_total", description: "Legacy endpoint writes that would have been denied under FR-027");
        _sodWarnings             = _meter.CreateCounter<long>("sod_violation_warning_total", description: "DoDI 8510.01 SoD warnings surfaced by FR-026");
        _propagationDuration     = _meter.CreateHistogram<double>("org_role_propagation_duration_seconds", unit: "s", description: "FR-028 worker propagation duration per Org-role-add event");
    }

    public void RecordLegacyCall(Guid tenantId, string method) =>
        _legacyEndpointCalls.Add(1, new("tenant_id", tenantId), new("method", method));

    public void RecordLegacyBypass(Guid tenantId, RmfRole targetRole) =>
        _legacyEndpointBypass.Add(1, new("tenant_id", tenantId), new("target_role", targetRole.ToString()));

    public void RecordSodWarning(Guid tenantId, RmfRole callerRole, RmfRole conflictingRole) =>
        _sodWarnings.Add(1, new("tenant_id", tenantId), new("caller_role", callerRole.ToString()), new("conflicting_role", conflictingRole.ToString()));

    public void RecordPropagation(Guid tenantId, RmfRole targetRole, int systemsProcessed, TimeSpan duration)
    {
        var bucket = systemsProcessed switch { <= 10 => "1-10", <= 100 => "11-100", <= 500 => "101-500", _ => "500+" };
        _propagationDuration.Record(duration.TotalSeconds, new("tenant_id", tenantId), new("target_role", targetRole.ToString()), new("systems_bucket", bucket));
    }

    public void Dispose() => _meter.Dispose();
}
```

**Cardinality notes**:

- `tenant_id` is a UUID — high cardinality but bounded at the active-tenant count; acceptable for the existing OpenTelemetry export pipeline.
- `method` ∈ {`GET`, `POST`, `DELETE`} — 3.
- `target_role` ∈ 7 values; `caller_role` ∈ 7 values; `conflicting_role` ∈ 7 values.
- `systems_bucket` ∈ 4 values.

Total label cardinality per instrument is bounded by `tenants × roles × small constants` — within the OpenTelemetry default series ceiling.

---

## 6. `ICallerEffectiveRoleResolver`

**Purpose**: Resolve the calling principal's **highest-privileged RmfRole** *and* whether the principal currently holds an active `OrganizationRole.Administrator` assignment, so `IRoleAuthorizationService.Authorize(...)` and the dashboard's client-side affordance hiding both have one server-truth source. Returns the resolved facts as a `CallerEffectiveRole` (see § 2). Added per Feature 049 `/speckit.analyze` G2 finding; signature widened on 2026-05-20 during implementation to absorb the Administrator-bypass cleanly (see § 2 "Design note").

**File**: `src/Ato.Copilot.Core/Services/Roles/ICallerEffectiveRoleResolver.cs`, `src/Ato.Copilot.Core/Services/Roles/CallerEffectiveRoleResolver.cs`

```csharp
public interface ICallerEffectiveRoleResolver
{
    /// <summary>
    /// Returns the caller's resolved Org-scope identity for the tenant: the
    /// highest-privileged RmfRole the caller currently holds (or <c>null</c>),
    /// AND whether the caller holds an active Administrator assignment.
    /// </summary>
    /// <remarks>
    /// Reads MUST be tenant-scoped: every query filters by <paramref name="tenantId"/>.
    /// The resolver unions three sources, in priority order:
    ///   1. <c>OrganizationRoleAssignments</c> (active, not soft-removed) —
    ///      <see cref="OrganizationRole.Administrator"/> rows set
    ///      <see cref="CallerEffectiveRole.IsTenantAdministrator"/>=true; all other rows
    ///      contribute to the RmfRole reduction via <see cref="OrganizationRoleToRmfRoleMap.TryMap"/>.
    ///   2. <c>SystemRoleAssignments</c> across all systems the principal owns/operates
    ///      (mapped to RmfRole via the same cross-enum map).
    ///   3. Legacy <c>RmfRoleAssignments</c> (FR-024 read-side compatibility).
    /// After union, the resolver picks the maximum by the privilege gradient:
    ///   Issm > Isso > {AuthorizingOfficial, Sca, SystemOwner, MissionOwner}
    /// for the <see cref="CallerEffectiveRole.RmfRole"/> field. (Administrator is NOT
    /// in this gradient — it lives in a separate boolean field because <see cref="RmfRole"/>
    /// is frozen at 6 values per FR-020.) Ties within the lower tier are arbitrary but
    /// stable per request (the resolver returns the lexicographically-first value among
    /// ties to keep telemetry stable).
    /// </remarks>
    ValueTask<CallerEffectiveRole> ResolveAsync(
        Guid tenantId,
        Guid principalPersonId,
        CancellationToken ct);
}
```

**Implementation notes (`CallerEffectiveRoleResolver`)**:

- Scoped lifetime — captures the request-scoped `AtoCopilotContext`.
- Single round-trip per call: issue the three reads in parallel via `Task.WhenAll` and reduce in memory. Do NOT cache across requests (effective role can change mid-session via Org-role assign).
- The privilege gradient over the 6 RmfRole values is encoded as a `static readonly int[] PrivilegeOrder` keyed by `RmfRole`; the reducer returns `null` for `RmfRole` when no RmfRole-mapped row is present.
- `IsTenantAdministrator` is set independently from RmfRole resolution — a caller may hold BOTH an Administrator row and an RmfRole-bearing row (e.g., founder is `Administrator + ISSM`); both fields are populated in that case.
- Tests: see `tests/Ato.Copilot.Tests.Unit/Roles/CallerEffectiveRoleResolverTests.cs` (T018a) for gradient ordering, empty-set return (`CallerEffectiveRole.None`), Administrator-bit + RmfRole co-existence, legacy-only fallback, and tenant-isolation negative cases.

**Used by**:

- `SystemRolesEndpoints.cs` (per-request resolution before each `IRoleAuthorizationService.Authorize` call).
- `GET /api/roles/effective` (returns `{ effectiveRole: RmfRole | null, isTenantAdministrator: boolean }` for dashboard affordance hiding per FR-027 / SC-009).

---

## DI Registration (in `AtoCopilotMcpServiceExtensions.cs`)

```csharp
// In the same place that WizardJobChannel and WizardJobHostedService are registered:
services.AddSingleton<IOrganizationRoleFanoutQueue, OrganizationRoleFanoutQueue>();
services.AddHostedService<OrganizationRoleFanoutWorker>();
services.AddSingleton<RoleMetrics>();

// Service collection extensions for the core role services:
services.AddSingleton<IRoleAuthorizationService, RoleAuthorizationService>();
services.AddScoped<ISoDConflictDetector, SoDConflictDetector>();
services.AddScoped<IUnifiedRoleReader, UnifiedRoleReader>();
services.AddScoped<ICallerEffectiveRoleResolver, CallerEffectiveRoleResolver>();
```

`OrganizationRoleFanoutWorker` is registered only when the existing `RegisterHostedServices` flag is true (the same flag that gates `WizardJobHostedService`, `SspExportBackgroundService`, etc., in [AtoCopilotMcpServiceExtensions.cs:37](../../../src/Ato.Copilot.Mcp/Extensions/AtoCopilotMcpServiceExtensions.cs#L37)).

---

## Cancellation & Disposal

- Every async method on every new interface accepts `CancellationToken`.
- `RoleMetrics` is `IDisposable`; the DI container disposes the `Meter` on shutdown.
- `OrganizationRoleFanoutWorker.ExecuteAsync` honors `stoppingToken` and exits the `ReadAllAsync` loop on cancellation.

---

## Contract → FR / SC traceability

| Contract | Drives FRs | Drives SCs |
|---|---|---|
| `IUnifiedRoleReader` | FR-002, FR-003, FR-004, FR-008, FR-029 | SC-002, SC-006 |
| `IRoleAuthorizationService` | FR-027 | SC-009 |
| `ISoDConflictDetector` | FR-026 | (audit telemetry only) |
| `IOrganizationRoleFanoutQueue` + worker | FR-006, FR-028 | SC-011 |
| `RoleMetrics` | FR-018, FR-019, FR-026, FR-028 | SC-011 |
| `ICallerEffectiveRoleResolver` | FR-027 | SC-009 |
