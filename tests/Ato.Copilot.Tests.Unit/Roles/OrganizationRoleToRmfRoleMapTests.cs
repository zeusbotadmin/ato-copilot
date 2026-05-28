using Ato.Copilot.Core.Models.Compliance;
using Ato.Copilot.Core.Models.Onboarding;
using Ato.Copilot.Core.Services.Roles;
using FluentAssertions;
using Xunit;

namespace Ato.Copilot.Tests.Unit.Roles;

/// <summary>
/// Pins the cross-enum mapping rules per
/// <c>specs/049-unified-rmf-role-assignments/data-model.md § Cross-enum mapping</c>.
///
/// <para>The mapping rules are:</para>
/// <list type="bullet">
///   <item>Identity edges: <c>MissionOwner ↔ MissionOwner</c>,
///         <c>AuthorizingOfficial ↔ AuthorizingOfficial</c>,
///         <c>SystemOwner ↔ SystemOwner</c>, <c>Issm ↔ Issm</c>, <c>Isso ↔ Isso</c>.</item>
///   <item>The single non-identity edge: <c>OrganizationRole.Assessor ↔ RmfRole.Sca</c>.</item>
///   <item><c>OrganizationRole.Administrator</c> has no RMF-document equivalent;
///         <c>TryMap(OrganizationRole.Administrator)</c> returns <c>null</c>.</item>
///   <item>Round-trip stability: for every non-Administrator <c>OrganizationRole</c>,
///         <c>TryMap(TryMap(o)!.Value) == o</c>.</item>
/// </list>
/// </summary>
public class OrganizationRoleToRmfRoleMapTests
{
    [Theory]
    [InlineData(OrganizationRole.Issm, RmfRole.Issm)]
    [InlineData(OrganizationRole.Isso, RmfRole.Isso)]
    [InlineData(OrganizationRole.MissionOwner, RmfRole.MissionOwner)]
    [InlineData(OrganizationRole.AuthorizingOfficial, RmfRole.AuthorizingOfficial)]
    [InlineData(OrganizationRole.SystemOwner, RmfRole.SystemOwner)]
    public void OrganizationRole_to_RmfRole_identity_edges(
        OrganizationRole input,
        RmfRole expected)
    {
        // Arrange
        // (theory inputs)

        // Act
        var actual = OrganizationRoleToRmfRoleMap.TryMap(input);

        // Assert
        actual.Should().Be(expected);
    }

    [Fact]
    public void Assessor_maps_to_Sca()
    {
        // Arrange
        const OrganizationRole input = OrganizationRole.Assessor;

        // Act
        var actual = OrganizationRoleToRmfRoleMap.TryMap(input);

        // Assert
        actual.Should().Be(RmfRole.Sca,
            "Assessor↔Sca is the only non-identity edge in the cross-enum map");
    }

    [Fact]
    public void Administrator_maps_to_null()
    {
        // Arrange
        const OrganizationRole input = OrganizationRole.Administrator;

        // Act
        var actual = OrganizationRoleToRmfRoleMap.TryMap(input);

        // Assert
        actual.Should().BeNull(
            "Administrator is an Org-scope-only role with no RMF-document equivalent " +
            "and does not appear in OSCAL party exports");
    }

    [Theory]
    [InlineData(RmfRole.Issm, OrganizationRole.Issm)]
    [InlineData(RmfRole.Isso, OrganizationRole.Isso)]
    [InlineData(RmfRole.MissionOwner, OrganizationRole.MissionOwner)]
    [InlineData(RmfRole.AuthorizingOfficial, OrganizationRole.AuthorizingOfficial)]
    [InlineData(RmfRole.SystemOwner, OrganizationRole.SystemOwner)]
    [InlineData(RmfRole.Sca, OrganizationRole.Assessor)] // reverse of the only non-identity edge
    public void RmfRole_to_OrganizationRole_total_map(RmfRole input, OrganizationRole expected)
    {
        // Arrange
        // (theory inputs)

        // Act
        var actual = OrganizationRoleToRmfRoleMap.TryMap(input);

        // Assert
        actual.Should().Be(expected,
            "every RmfRole has exactly one OrganizationRole pre-image — there is no " +
            "RmfRole equivalent that lacks a Org-scope row");
    }

    [Theory]
    [InlineData(OrganizationRole.Issm)]
    [InlineData(OrganizationRole.Isso)]
    [InlineData(OrganizationRole.Assessor)]
    [InlineData(OrganizationRole.MissionOwner)]
    [InlineData(OrganizationRole.AuthorizingOfficial)]
    [InlineData(OrganizationRole.SystemOwner)]
    public void Non_Administrator_round_trip_is_stable(OrganizationRole input)
    {
        // Arrange
        // (theory input)

        // Act
        var rmf = OrganizationRoleToRmfRoleMap.TryMap(input);
        var back = rmf.HasValue ? OrganizationRoleToRmfRoleMap.TryMap(rmf.Value) : null;

        // Assert
        rmf.Should().NotBeNull("every non-Administrator OrganizationRole has an RmfRole image");
        back.Should().Be(input,
            "OrganizationRole.{0} → RmfRole.{1} → OrganizationRole.{2} must equal OrganizationRole.{0}",
            input, rmf, back);
    }
}
