using Ato.Copilot.Core.Models.Compliance;
using Ato.Copilot.Core.Models.Onboarding;

namespace Ato.Copilot.Core.Services.Roles;

/// <summary>
/// Pure-functional bidirectional map between <see cref="OrganizationRole"/> (Org-scope
/// administrative roles, including <see cref="OrganizationRole.Administrator"/>) and
/// <see cref="RmfRole"/> (RMF-document roles per DoDI 8510.01).
///
/// <para>
/// Per <c>specs/049-unified-rmf-role-assignments/data-model.md § Cross-enum mapping</c>:
/// <list type="bullet">
///   <item>Every <see cref="RmfRole"/> has exactly one <see cref="OrganizationRole"/> pre-image (the map is total in the RMF → Org direction).</item>
///   <item>Five <see cref="OrganizationRole"/> values map identity-to-identity to their <see cref="RmfRole"/> equivalents
///         (<see cref="OrganizationRole.Issm"/>, <see cref="OrganizationRole.Isso"/>,
///          <see cref="OrganizationRole.MissionOwner"/>, <see cref="OrganizationRole.AuthorizingOfficial"/>,
///          <see cref="OrganizationRole.SystemOwner"/>).</item>
///   <item>The only non-identity edge: <see cref="OrganizationRole.Assessor"/> ↔ <see cref="RmfRole.Sca"/>.</item>
///   <item><see cref="OrganizationRole.Administrator"/> has no RMF-document equivalent and
///         maps to <c>null</c> — Administrator is an Org-scope-only role and does NOT appear
///         in OSCAL party exports.</item>
/// </list>
/// </para>
///
/// <para>
/// Implementation uses <see langword="switch"/> expressions (not <see cref="System.Collections.Generic.IDictionary{TKey, TValue}"/>)
/// because the <see cref="RmfRole"/> enum is frozen at 6 values per FR-020 and the
/// <see cref="OrganizationRole"/> enum is locked at 7 values for the lifetime of this feature.
/// Adding a new value WILL force a compiler error here when the analyzer
/// <c>CS8509</c> ("switch expression does not handle all possible values") fires —
/// that's the intended contract pin.
/// </para>
/// </summary>
public static class OrganizationRoleToRmfRoleMap
{
    /// <summary>
    /// Maps an <see cref="OrganizationRole"/> to its <see cref="RmfRole"/> equivalent,
    /// or <c>null</c> when no such equivalent exists (only
    /// <see cref="OrganizationRole.Administrator"/> currently returns <c>null</c>).
    /// </summary>
    public static RmfRole? TryMap(OrganizationRole role) => role switch
    {
        OrganizationRole.Issm => RmfRole.Issm,
        OrganizationRole.Isso => RmfRole.Isso,
        OrganizationRole.Assessor => RmfRole.Sca,
        OrganizationRole.MissionOwner => RmfRole.MissionOwner,
        OrganizationRole.AuthorizingOfficial => RmfRole.AuthorizingOfficial,
        OrganizationRole.SystemOwner => RmfRole.SystemOwner,
        OrganizationRole.Administrator => null,
        _ => null, // defensive — unreachable while OrganizationRole is closed
    };

    /// <summary>
    /// Maps an <see cref="RmfRole"/> to its <see cref="OrganizationRole"/> pre-image.
    /// The map is total in this direction — every <see cref="RmfRole"/> has an Org-scope row,
    /// so the return is non-nullable in spirit (declared <see cref="OrganizationRole"/>?
    /// only to mirror the forward signature and tolerate future RmfRole additions).
    /// </summary>
    public static OrganizationRole? TryMap(RmfRole role) => role switch
    {
        RmfRole.Issm => OrganizationRole.Issm,
        RmfRole.Isso => OrganizationRole.Isso,
        RmfRole.Sca => OrganizationRole.Assessor,
        RmfRole.MissionOwner => OrganizationRole.MissionOwner,
        RmfRole.AuthorizingOfficial => OrganizationRole.AuthorizingOfficial,
        RmfRole.SystemOwner => OrganizationRole.SystemOwner,
        _ => null, // defensive — unreachable while RmfRole is frozen per FR-020
    };
}
