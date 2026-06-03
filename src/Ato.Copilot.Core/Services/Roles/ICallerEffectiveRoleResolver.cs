namespace Ato.Copilot.Core.Services.Roles;

/// <summary>
/// Resolves the calling principal's <see cref="CallerEffectiveRole"/> — the highest-privileged
/// <see cref="Models.Compliance.RmfRole"/> they currently hold plus whether they hold an
/// active <see cref="Models.Onboarding.OrganizationRole.Administrator"/> assignment — so
/// <see cref="IRoleAuthorizationService.Authorize"/> and the dashboard's client-side
/// affordance hiding both have one server-truth source.
/// </summary>
public interface ICallerEffectiveRoleResolver
{
    /// <summary>
    /// Returns the caller's resolved Org-scope identity for the tenant.
    /// </summary>
    /// <remarks>
    /// <para>Reads MUST be tenant-scoped — every query filters by
    /// <paramref name="tenantId"/>. The resolver unions three sources:</para>
    /// <list type="number">
    ///   <item><c>OrganizationRoleAssignments</c> (active, not soft-removed) —
    ///         <c>OrganizationRole.Administrator</c> rows set
    ///         <c>IsTenantAdministrator=true</c>; all other rows contribute to the
    ///         RmfRole reduction via <see cref="OrganizationRoleToRmfRoleMap.TryMap"/>.</item>
    ///   <item><c>SystemRoleAssignments</c> across all systems the principal owns/operates
    ///         (mapped to RmfRole via the same cross-enum map).</item>
    ///   <item>Legacy <c>RmfRoleAssignments</c> (FR-024 read-side compatibility).</item>
    /// </list>
    /// <para>After union, the resolver picks the maximum by the privilege gradient:</para>
    /// <para><c>Issm &gt; Isso &gt; {AuthorizingOfficial, Sca, SystemOwner, MissionOwner}</c></para>
    /// </remarks>
    ValueTask<CallerEffectiveRole> ResolveAsync(
        Guid tenantId,
        Guid principalPersonId,
        CancellationToken ct);
}
