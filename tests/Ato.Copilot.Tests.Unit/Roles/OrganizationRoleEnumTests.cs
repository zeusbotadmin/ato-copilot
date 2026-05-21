using Ato.Copilot.Core.Models.Onboarding;
using FluentAssertions;
using Xunit;

namespace Ato.Copilot.Tests.Unit.Roles;

/// <summary>
/// Pins the public contract of the <see cref="OrganizationRole"/> enum after Feature 049
/// extends it with three RMF-document roles (MissionOwner, AuthorizingOfficial, SystemOwner).
///
/// <para>
/// Asserts:
/// <list type="number">
/// <item>The four pre-existing values still serialize to their existing string names
///       (back-compat for the <c>nvarchar(32)</c> column at
///       <c>AtoCopilotContext.cs:3594</c> — <c>HasConversion&lt;string&gt;()</c>).</item>
/// <item>The three new values exist and serialize to <c>"MissionOwner"</c>,
///       <c>"AuthorizingOfficial"</c>, and <c>"SystemOwner"</c> verbatim
///       (per data-model.md §1).</item>
/// <item>Every serialized name fits inside the existing 32-character column width
///       (longest is <c>"AuthorizingOfficial"</c> at 19 chars — well under 32).</item>
/// </list>
/// </para>
/// </summary>
public class OrganizationRoleEnumTests
{
    [Theory]
    [InlineData(OrganizationRole.Issm, "Issm")]
    [InlineData(OrganizationRole.Isso, "Isso")]
    [InlineData(OrganizationRole.Administrator, "Administrator")]
    [InlineData(OrganizationRole.Assessor, "Assessor")]
    public void Existing_values_serialize_to_pre_feature_049_strings(
        OrganizationRole role,
        string expected)
    {
        // Arrange
        // (theory inputs)

        // Act
        var serialized = role.ToString();

        // Assert
        serialized.Should().Be(expected);
    }

    [Theory]
    [InlineData(OrganizationRole.MissionOwner, "MissionOwner")]
    [InlineData(OrganizationRole.AuthorizingOfficial, "AuthorizingOfficial")]
    [InlineData(OrganizationRole.SystemOwner, "SystemOwner")]
    public void Feature_049_values_serialize_verbatim(
        OrganizationRole role,
        string expected)
    {
        // Arrange
        // (theory inputs)

        // Act
        var serialized = role.ToString();

        // Assert
        serialized.Should().Be(expected);
    }

    [Fact]
    public void All_serialized_names_fit_in_nvarchar_32_column()
    {
        // Arrange
        const int columnWidth = 32; // matches HasMaxLength(32) at AtoCopilotContext.cs:3594

        // Act
        var names = Enum.GetNames<OrganizationRole>();

        // Assert
        names.Should().OnlyContain(n => n.Length <= columnWidth,
            "the OrganizationRoleAssignment.Role column is nvarchar(32) — " +
            "any name longer than 32 chars would silently truncate on SQL Server");
    }

    [Fact]
    public void Enum_contains_exactly_seven_values_after_feature_049()
    {
        // Arrange
        // (no setup)

        // Act
        var count = Enum.GetValues<OrganizationRole>().Length;

        // Assert
        count.Should().Be(7,
            "Feature 049 extends OrganizationRole from 4 to 7 values by appending " +
            "MissionOwner, AuthorizingOfficial, SystemOwner");
    }

    [Fact]
    public void Existing_ordinals_must_not_change()
    {
        // Arrange
        // (no setup)

        // Act / Assert: pin the four pre-existing ordinals so future PRs cannot reorder them.
        ((int)OrganizationRole.Issm).Should().Be(0);
        ((int)OrganizationRole.Isso).Should().Be(1);
        ((int)OrganizationRole.Administrator).Should().Be(2);
        ((int)OrganizationRole.Assessor).Should().Be(3);
    }
}
