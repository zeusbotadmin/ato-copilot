namespace Ato.Copilot.Core.Services.Roles;

/// <summary>
/// Single read facade over the three role-data sources
/// (per-system override → inherited → org-level fallback → legacy).
/// Replaces the direct table reads in <c>SystemProfileService</c> and exposes
/// the same precedence chain to the dashboard.
/// </summary>
/// <remarks>
/// <para>Implementation contract (encoded in <see cref="UnifiedRoleReader"/>):</para>
/// <list type="number">
/// <item>Implementation MUST respect tenant isolation — every query filters by
/// <c>tenantId</c>. Tested by <c>TenantIsolationRolesTests</c>.</item>
/// <item>Implementation MUST resolve precedence per data-model.md § Read-time precedence.</item>
/// <item>For <c>IsPrimary</c> tie-breaking on the org-fallback step: <c>IsPrimary=true</c>
/// wins; ties are broken by most-recent <c>CreatedAt</c>.</item>
/// </list>
/// </remarks>
public interface IUnifiedRoleReader
{
    /// <summary>
    /// Returns the full 6-role state for the given system, with each role's source
    /// (override / inherited / org-fallback / legacy / not-assigned) surfaced for
    /// UI affordance. The returned <see cref="SystemRoleSnapshot"/> always carries
    /// exactly 6 <see cref="ResolvedRoleAssignment"/> rows — one per
    /// <see cref="Models.Compliance.RmfRole"/> value (per FR-020).
    /// </summary>
    Task<SystemRoleSnapshot> GetSystemRolesAsync(
        Guid tenantId,
        string registeredSystemId,
        CancellationToken ct);

    /// <summary>
    /// Convenience read for the banner: returns the resolved <c>MissionOwner</c> or
    /// <c>null</c>. Implemented as a 1-role projection of
    /// <see cref="GetSystemRolesAsync"/> to keep precedence logic in exactly one place.
    /// </summary>
    Task<ResolvedRoleAssignment?> GetMissionOwnerAsync(
        Guid tenantId,
        string registeredSystemId,
        CancellationToken ct);
}
