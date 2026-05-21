using Ato.Copilot.Core.Models.Compliance;
using Ato.Copilot.Core.Services.Roles;
using FluentAssertions;
using Xunit;

namespace Ato.Copilot.Tests.Unit.Roles;

/// <summary>
/// T013 [US1] — Failing tests pinning the FR-027 role-tiered authorization matrix
/// per <c>specs/049-unified-rmf-role-assignments/contracts/internal-services.md § 2</c>.
///
/// <para>Total scenarios: <b>54</b></para>
/// <list type="bullet">
///   <item>(a) 36 RmfRole × RmfRole cells — the closed 6-key matrix.</item>
///   <item>(b) 6 <see cref="CallerEffectiveRole.None"/> × RmfRole cells (all denied).</item>
///   <item>(c) 6 <c>IsTenantAdministrator=true</c> × RmfRole cells (all allowed — bypass).</item>
///   <item>(d) 6 <c>isBootstrapSession=true</c> × RmfRole cells (all allowed — bypass).</item>
/// </list>
///
/// <para>Matrix per the spec:</para>
/// <list type="table">
///   <listheader><term>Caller</term><description>May assign target roles</description></listheader>
///   <item><term><c>Issm</c></term><description>all 6 RmfRole values <i>except</i> <c>AuthorizingOfficial</c></description></item>
///   <item><term><c>Isso</c></term><description><c>MissionOwner</c>, <c>SystemOwner</c></description></item>
///   <item><term><c>AuthorizingOfficial</c>, <c>Sca</c>, <c>SystemOwner</c>, <c>MissionOwner</c>, <c>None</c></term><description>none</description></item>
/// </list>
/// </summary>
public class RoleAuthorizationServiceTests
{
    private static IRoleAuthorizationService NewService() => new RoleAuthorizationService();

    // ───── Block (a): 36 RmfRole × RmfRole cells ─────────────────────────────

    public static IEnumerable<object[]> Issm_allowed_targets =>
        new RmfRole[] { RmfRole.Issm, RmfRole.Isso, RmfRole.Sca, RmfRole.SystemOwner, RmfRole.MissionOwner }
            .Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(Issm_allowed_targets))]
    public void Issm_may_assign_all_except_AuthorizingOfficial(RmfRole target)
    {
        // Arrange
        var caller = new CallerEffectiveRole(RmfRole.Issm, IsTenantAdministrator: false);

        // Act
        var result = NewService().Authorize(caller, target, isBootstrapSession: false);

        // Assert
        result.Allowed.Should().BeTrue();
        result.DeniedReason.Should().BeNull();
    }

    [Fact]
    public void Issm_may_NOT_assign_AuthorizingOfficial()
    {
        // Arrange
        var caller = new CallerEffectiveRole(RmfRole.Issm, IsTenantAdministrator: false);

        // Act
        var result = NewService().Authorize(caller, RmfRole.AuthorizingOfficial, isBootstrapSession: false);

        // Assert
        result.Allowed.Should().BeFalse();
        result.DeniedReason.Should().NotBeNullOrEmpty();
    }

    public static IEnumerable<object[]> Isso_allowed_targets =>
        new RmfRole[] { RmfRole.MissionOwner, RmfRole.SystemOwner }.Select(t => new object[] { t });

    public static IEnumerable<object[]> Isso_denied_targets =>
        new RmfRole[] { RmfRole.AuthorizingOfficial, RmfRole.Issm, RmfRole.Isso, RmfRole.Sca }
            .Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(Isso_allowed_targets))]
    public void Isso_may_assign_only_MissionOwner_and_SystemOwner(RmfRole target)
    {
        // Arrange
        var caller = new CallerEffectiveRole(RmfRole.Isso, IsTenantAdministrator: false);

        // Act
        var result = NewService().Authorize(caller, target, isBootstrapSession: false);

        // Assert
        result.Allowed.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Isso_denied_targets))]
    public void Isso_may_NOT_assign_any_other_role(RmfRole target)
    {
        // Arrange
        var caller = new CallerEffectiveRole(RmfRole.Isso, IsTenantAdministrator: false);

        // Act
        var result = NewService().Authorize(caller, target, isBootstrapSession: false);

        // Assert
        result.Allowed.Should().BeFalse();
        result.DeniedReason.Should().NotBeNullOrEmpty();
    }

    public static IEnumerable<object[]> Lower_tier_callers_and_all_targets =>
        from caller in new[] { RmfRole.AuthorizingOfficial, RmfRole.Sca, RmfRole.SystemOwner, RmfRole.MissionOwner }
        from target in Enum.GetValues<RmfRole>()
        select new object[] { caller, target };

    [Theory]
    [MemberData(nameof(Lower_tier_callers_and_all_targets))]
    public void Lower_tier_RmfRole_callers_may_assign_nothing(RmfRole callerRole, RmfRole targetRole)
    {
        // Arrange
        var caller = new CallerEffectiveRole(callerRole, IsTenantAdministrator: false);

        // Act
        var result = NewService().Authorize(caller, targetRole, isBootstrapSession: false);

        // Assert
        result.Allowed.Should().BeFalse(
            "lower-tier RmfRole callers (AO, Sca, SystemOwner, MissionOwner) have an empty assignable set per FR-027");
    }

    // ───── Block (b): 6 None × RmfRole — all denied ──────────────────────────

    [Theory]
    [InlineData(RmfRole.AuthorizingOfficial)]
    [InlineData(RmfRole.Issm)]
    [InlineData(RmfRole.Isso)]
    [InlineData(RmfRole.Sca)]
    [InlineData(RmfRole.SystemOwner)]
    [InlineData(RmfRole.MissionOwner)]
    public void None_caller_may_assign_nothing(RmfRole target)
    {
        // Arrange
        var caller = CallerEffectiveRole.None;

        // Act
        var result = NewService().Authorize(caller, target, isBootstrapSession: false);

        // Assert
        result.Allowed.Should().BeFalse(
            "a caller with no RMF role and no Administrator bit MUST be denied for every target");
        result.DeniedReason.Should().NotBeNullOrEmpty();
    }

    // ───── Block (c): 6 IsTenantAdministrator=true × RmfRole — all allowed ───

    [Theory]
    [InlineData(RmfRole.AuthorizingOfficial)]
    [InlineData(RmfRole.Issm)]
    [InlineData(RmfRole.Isso)]
    [InlineData(RmfRole.Sca)]
    [InlineData(RmfRole.SystemOwner)]
    [InlineData(RmfRole.MissionOwner)]
    public void IsTenantAdministrator_bypasses_the_matrix(RmfRole target)
    {
        // Arrange — Administrator alone (no RmfRole) and Administrator-with-RmfRole both allowed.
        var adminOnly = new CallerEffectiveRole(RmfRole: null, IsTenantAdministrator: true);
        var adminPlusAo = new CallerEffectiveRole(RmfRole.AuthorizingOfficial, IsTenantAdministrator: true);
        var service = NewService();

        // Act
        var r1 = service.Authorize(adminOnly, target, isBootstrapSession: false);
        var r2 = service.Authorize(adminPlusAo, target, isBootstrapSession: false);

        // Assert
        r1.Allowed.Should().BeTrue(
            "IsTenantAdministrator=true short-circuits to Allowed BEFORE the matrix is consulted (see § 2 design note)");
        r1.DeniedReason.Should().BeNull();
        r2.Allowed.Should().BeTrue(
            "Administrator bypass takes precedence even when the caller ALSO holds a lower-tier RmfRole");
    }

    // ───── Block (d): 6 isBootstrapSession=true × RmfRole — all allowed ──────

    [Theory]
    [InlineData(RmfRole.AuthorizingOfficial)]
    [InlineData(RmfRole.Issm)]
    [InlineData(RmfRole.Isso)]
    [InlineData(RmfRole.Sca)]
    [InlineData(RmfRole.SystemOwner)]
    [InlineData(RmfRole.MissionOwner)]
    public void Bootstrap_session_bypasses_the_matrix(RmfRole target)
    {
        // Arrange — even a None caller is allowed when bootstrap=true (the wizard's first save).
        var caller = CallerEffectiveRole.None;

        // Act
        var result = NewService().Authorize(caller, target, isBootstrapSession: true);

        // Assert
        result.Allowed.Should().BeTrue(
            "isBootstrapSession=true (the FIRST OrganizationRoleAssignment write per wizard session) " +
            "MUST short-circuit to Allowed for any target");
        result.DeniedReason.Should().BeNull();
    }
}
