using System.Collections.Immutable;
using Ato.Copilot.Core.Models.Compliance;

namespace Ato.Copilot.Core.Services.Roles;

/// <summary>
/// Pure-functional FR-027 RBAC matrix evaluator. No DB or HTTP I/O.
/// Encodes the closed 6 × 6 RmfRole matrix per
/// <c>specs/049-unified-rmf-role-assignments/contracts/internal-services.md § 2</c>.
/// </summary>
/// <remarks>
/// <para>Authorization decision is made in this order:</para>
/// <list type="number">
///   <item>If <c>isBootstrapSession</c> is <c>true</c> → Allowed (the wizard's
///         first <c>OrganizationRoleAssignment</c> write per session).</item>
///   <item>If <c>caller.IsTenantAdministrator</c> is <c>true</c> → Allowed
///         (Administrator bypass; Administrator is an Org-scope-only role with no
///         RmfRole image per FR-020).</item>
///   <item>If <c>caller.RmfRole</c> is <c>null</c> → Denied.</item>
///   <item>Otherwise consult the closed matrix.</item>
/// </list>
/// </remarks>
public sealed class RoleAuthorizationService : IRoleAuthorizationService
{
    /// <summary>
    /// FR-027 matrix keyed by the caller's RmfRole; value is the set of targets
    /// they may assign. 6 keys × 6 RmfRole values each (where applicable).
    /// </summary>
    private static readonly ImmutableDictionary<RmfRole, ImmutableHashSet<RmfRole>> Matrix =
        new Dictionary<RmfRole, ImmutableHashSet<RmfRole>>
        {
            [RmfRole.Issm] = ImmutableHashSet.Create(
                RmfRole.Issm,
                RmfRole.Isso,
                RmfRole.Sca,
                RmfRole.SystemOwner,
                RmfRole.MissionOwner),
            [RmfRole.Isso] = ImmutableHashSet.Create(
                RmfRole.MissionOwner,
                RmfRole.SystemOwner),
            [RmfRole.AuthorizingOfficial] = ImmutableHashSet<RmfRole>.Empty,
            [RmfRole.Sca] = ImmutableHashSet<RmfRole>.Empty,
            [RmfRole.SystemOwner] = ImmutableHashSet<RmfRole>.Empty,
            [RmfRole.MissionOwner] = ImmutableHashSet<RmfRole>.Empty,
        }.ToImmutableDictionary();

    /// <inheritdoc />
    public AuthorizationResult Authorize(
        CallerEffectiveRole caller,
        RmfRole targetRole,
        bool isBootstrapSession)
    {
        // (1) Bootstrap bypass — the wizard's first Org-role write per session.
        if (isBootstrapSession)
        {
            return new AuthorizationResult(true, null);
        }

        // (2) Administrator bypass — Org-scope-only role, no RmfRole image, but
        //     full assign privileges. See § 2 "Design note (Feature 049)".
        if (caller.IsTenantAdministrator)
        {
            return new AuthorizationResult(true, null);
        }

        // (3) No RmfRole-bearing row → denied.
        if (caller.RmfRole is null)
        {
            return new AuthorizationResult(false,
                "Caller holds no RmfRole-bearing assignment for this tenant.");
        }

        // (4) Matrix lookup. Missing key → empty set → denied.
        if (Matrix.TryGetValue(caller.RmfRole.Value, out var allowedTargets)
            && allowedTargets.Contains(targetRole))
        {
            return new AuthorizationResult(true, null);
        }

        return new AuthorizationResult(false,
            $"Role {caller.RmfRole.Value} is not permitted to assign {targetRole} (FR-027 matrix).");
    }
}
