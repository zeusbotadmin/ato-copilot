using Ato.Copilot.Core.Models.Compliance;

namespace Ato.Copilot.Core.Services.Roles;

/// <summary>
/// Per-system unified read result returned by <see cref="IUnifiedRoleReader.GetSystemRolesAsync"/>.
/// Carries one <see cref="ResolvedRoleAssignment"/> per <see cref="RmfRole"/> value (six entries),
/// each annotated with its <see cref="RoleAssignmentSource"/> so the dashboard can render the
/// per-row affordance (override, inherited, org-fallback, legacy, or not-assigned).
/// </summary>
public readonly record struct SystemRoleSnapshot(
    Guid TenantId,
    string RegisteredSystemId,
    IReadOnlyList<ResolvedRoleAssignment> Roles);

/// <summary>
/// One resolved role row inside a <see cref="SystemRoleSnapshot"/>. When
/// <see cref="Source"/> is <see cref="RoleAssignmentSource.NotAssigned"/>, both
/// <see cref="PersonId"/> and <see cref="PersonDisplayName"/> are <c>null</c>.
/// </summary>
/// <param name="Role">The RMF role being resolved.</param>
/// <param name="PersonId">Assignee Person ID, or <c>null</c> when not assigned.</param>
/// <param name="PersonDisplayName">Assignee display name, or <c>null</c>.</param>
/// <param name="Source">Provenance of the assignment per precedence chain (override → inherited → org-fallback → legacy).</param>
/// <param name="OrgRoleId">When <see cref="Source"/> is <see cref="RoleAssignmentSource.Inherited"/> or
/// <see cref="RoleAssignmentSource.OrgFallback"/>, the originating <c>OrganizationRoleAssignment.Id</c>; else <c>null</c>.</param>
public readonly record struct ResolvedRoleAssignment(
    RmfRole Role,
    Guid? PersonId,
    string? PersonDisplayName,
    RoleAssignmentSource Source,
    Guid? OrgRoleId);

/// <summary>
/// Provenance label for one row inside a <see cref="SystemRoleSnapshot"/>.
/// </summary>
public enum RoleAssignmentSource
{
    /// <summary>No row exists at any layer of the precedence chain.</summary>
    NotAssigned,
    /// <summary><see cref="Models.Onboarding.SystemRoleAssignment"/> with <c>IsInherited=false</c> — a per-system override.</summary>
    Override,
    /// <summary><see cref="Models.Onboarding.SystemRoleAssignment"/> with <c>IsInherited=true</c> — auto-copied from an Org-level row.</summary>
    Inherited,
    /// <summary>An <see cref="Models.Onboarding.OrganizationRoleAssignment"/> exists but its per-system inherited row
    /// has not yet been materialized (e.g., during the brief window before the fan-out worker runs).</summary>
    OrgFallback,
    /// <summary>Only a legacy <see cref="Models.Compliance.RmfRoleAssignment"/> row exists (FR-024 read-side compatibility).</summary>
    Legacy,
}

/// <summary>
/// Return value of <see cref="IRoleAuthorizationService.Authorize"/> per FR-027.
/// <see cref="DeniedReason"/> is non-null iff <see cref="Allowed"/> is <c>false</c>.
/// </summary>
public readonly record struct AuthorizationResult(
    bool Allowed,
    string? DeniedReason);

/// <summary>
/// Caller's resolved Org-scope identity for FR-027 authorization. Returned by
/// <see cref="ICallerEffectiveRoleResolver.ResolveAsync"/> and consumed by
/// <see cref="IRoleAuthorizationService.Authorize"/>.
/// </summary>
/// <param name="RmfRole">
/// The highest-privileged <see cref="Models.Compliance.RmfRole"/> the caller currently holds
/// for the tenant (per the gradient
/// <c>Issm &gt; Isso &gt; {AuthorizingOfficial, Sca, SystemOwner, MissionOwner}</c>), or
/// <c>null</c> when the caller holds none. Used as the matrix lookup key.
/// </param>
/// <param name="IsTenantAdministrator">
/// <c>true</c> when the caller holds an active
/// <see cref="Models.Onboarding.OrganizationRole.Administrator"/> assignment. Administrator is
/// an Org-scope-only role with no <see cref="Models.Compliance.RmfRole"/> equivalent (the RMF
/// enum is frozen at 6 values per FR-020); it short-circuits <see cref="IRoleAuthorizationService.Authorize"/>
/// to Allowed BEFORE the matrix is consulted.
/// </param>
/// <remarks>
/// The two fields are independent: a caller may hold BOTH an Administrator row AND an
/// RmfRole-bearing row (e.g., founder is `Administrator + ISSM`); both fields are populated
/// in that case.
/// </remarks>
public readonly record struct CallerEffectiveRole(
    RmfRole? RmfRole,
    bool IsTenantAdministrator)
{
    /// <summary>Singleton "no roles" instance — caller holds neither an RmfRole nor Administrator.</summary>
    public static CallerEffectiveRole None => new(null, false);
}

/// <summary>
/// One DoDI 8510.01 Enclosure 3 SoD warning emitted by <see cref="ISoDConflictDetector.DetectAsync"/>.
/// The detector is read-only; surfacing a warning never blocks the underlying write — the
/// endpoint layer decides whether to require acknowledgement.
/// </summary>
public readonly record struct SoDWarning(
    string Code,                                       // closed enum-as-string; currently always "SOD_VIOLATION"
    string Message,
    (RmfRole Existing, RmfRole Target) RoleConflict,
    string DodiReference,                              // e.g. "DoDI 8510.01 Enclosure 3 § 4.b"
    string SuggestedAction);

/// <summary>
/// Intent enqueued on <see cref="IOrganizationRoleFanoutQueue"/> by the Org-role-assign write path
/// (FR-028). The worker dequeues, fans out per active <c>RegisteredSystem</c>, and inserts
/// inherited <c>SystemRoleAssignment</c> rows idempotently.
/// </summary>
public readonly record struct PropagationIntent(
    Guid TenantId,
    Guid OrganizationRoleAssignmentId,
    RmfRole TargetRole,
    Guid PersonId,
    DateTimeOffset EnqueuedAt);
