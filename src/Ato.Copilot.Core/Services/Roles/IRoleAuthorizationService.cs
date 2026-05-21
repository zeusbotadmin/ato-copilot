namespace Ato.Copilot.Core.Services.Roles;

/// <summary>
/// FR-027 role-tiered authorization matrix. Pure-functional; the caller's
/// resolved Org-scope identity and the target role are the only inputs.
/// No DB I/O. No HTTP I/O. Trivially testable.
/// </summary>
public interface IRoleAuthorizationService
{
    /// <summary>
    /// Evaluate whether <paramref name="caller"/> may assign
    /// <paramref name="targetRole"/>. Short-circuits to <c>Allowed</c> when
    /// <paramref name="isBootstrapSession"/> is <c>true</c> (the wizard's very-first
    /// <c>OrganizationRoleAssignment</c> write) or when
    /// <see cref="CallerEffectiveRole.IsTenantAdministrator"/> is <c>true</c>.
    /// Otherwise consults the closed 6 × 6 RmfRole matrix per
    /// <c>specs/049-unified-rmf-role-assignments/contracts/internal-services.md § 2</c>.
    /// </summary>
    AuthorizationResult Authorize(
        CallerEffectiveRole caller,
        Models.Compliance.RmfRole targetRole,
        bool isBootstrapSession);
}
