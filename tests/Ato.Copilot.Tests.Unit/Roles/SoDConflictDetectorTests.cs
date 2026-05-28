using Ato.Copilot.Core.Data.Context;
using Ato.Copilot.Core.Models.Compliance;
using Ato.Copilot.Core.Models.Onboarding;
using Ato.Copilot.Core.Services.Roles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ato.Copilot.Tests.Unit.Roles;

/// <summary>
/// T014 [US1] — Failing tests pinning the FR-026 DoDI 8510.01 Enclosure 3
/// separation-of-duties (SoD) detector per
/// <c>specs/049-unified-rmf-role-assignments/contracts/internal-services.md § 3</c>.
///
/// <para>Conflict pairs (encoded; pre-image is the role the person ALREADY holds,
/// target is the role the caller is about to assign):</para>
/// <list type="bullet">
///   <item><c>AuthorizingOfficial</c> conflicts with <c>SystemOwner</c>, <c>Issm</c>, <c>Isso</c>.</item>
///   <item><c>Sca</c> conflicts with <c>Issm</c>, <c>Isso</c>, <c>SystemOwner</c>.</item>
/// </list>
/// </summary>
public class SoDConflictDetectorTests
{
    private static (IDbContextFactory<AtoCopilotContext> factory, string dbName) NewFactory()
    {
        var dbName = $"sod_{Guid.NewGuid():N}";
        return (new StaticFactory(dbName), dbName);
    }

    private sealed class StaticFactory : IDbContextFactory<AtoCopilotContext>
    {
        private readonly string _dbName;
        public StaticFactory(string dbName) => _dbName = dbName;
        public AtoCopilotContext CreateDbContext() => new(
            new DbContextOptionsBuilder<AtoCopilotContext>().UseInMemoryDatabase(_dbName).Options);
    }

    private static async Task SeedOrgRoleAsync(
        IDbContextFactory<AtoCopilotContext> factory,
        Guid tenantId,
        Guid personId,
        OrganizationRole role)
    {
        await using var db = factory.CreateDbContext();
        if (!await db.Persons.AnyAsync(p => p.Id == personId))
        {
            db.Persons.Add(new Person
            {
                Id = personId,
                TenantId = tenantId,
                DisplayName = $"P{personId:N}".Substring(0, 9),
                Email = $"{personId:N}@x.mil",
            });
        }
        db.OrganizationRoleAssignments.Add(new OrganizationRoleAssignment
        {
            TenantId = tenantId,
            Role = role,
            PersonId = personId,
            IsPrimary = false,
        });
        await db.SaveChangesAsync();
    }

    public static IEnumerable<object[]> AO_conflict_pairs =>
        new[]
        {
            new object[] { OrganizationRole.SystemOwner, RmfRole.AuthorizingOfficial },
            new object[] { OrganizationRole.Issm,        RmfRole.AuthorizingOfficial },
            new object[] { OrganizationRole.Isso,        RmfRole.AuthorizingOfficial },
        };

    public static IEnumerable<object[]> Sca_conflict_pairs =>
        new[]
        {
            new object[] { OrganizationRole.Issm,        RmfRole.Sca },
            new object[] { OrganizationRole.Isso,        RmfRole.Sca },
            new object[] { OrganizationRole.SystemOwner, RmfRole.Sca },
        };

    [Theory]
    [MemberData(nameof(AO_conflict_pairs))]
    public async Task AO_target_with_existing_SystemOwner_Issm_or_Isso_emits_warning(
        OrganizationRole existing,
        RmfRole target)
    {
        // Arrange
        var (factory, _) = NewFactory();
        var tenantId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        await SeedOrgRoleAsync(factory, tenantId, personId, existing);
        ISoDConflictDetector detector = new SoDConflictDetector(factory);

        // Act
        var warnings = await detector.DetectAsync(tenantId, personId, target, CancellationToken.None);

        // Assert
        warnings.Should().HaveCount(1);
        warnings[0].Code.Should().Be("SOD_VIOLATION");
        warnings[0].DodiReference.Should().Contain("DoDI 8510.01",
            "every SoD warning MUST cite the DoDI 8510.01 Enclosure 3 paragraph");
        warnings[0].SuggestedAction.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(Sca_conflict_pairs))]
    public async Task Sca_target_with_existing_Issm_Isso_or_SystemOwner_emits_warning(
        OrganizationRole existing,
        RmfRole target)
    {
        // Arrange
        var (factory, _) = NewFactory();
        var tenantId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        await SeedOrgRoleAsync(factory, tenantId, personId, existing);
        ISoDConflictDetector detector = new SoDConflictDetector(factory);

        // Act
        var warnings = await detector.DetectAsync(tenantId, personId, target, CancellationToken.None);

        // Assert
        warnings.Should().HaveCount(1);
        warnings[0].Code.Should().Be("SOD_VIOLATION");
    }

    public static IEnumerable<object[]> NonConflict_pairs =>
        new[]
        {
            new object[] { OrganizationRole.MissionOwner,        RmfRole.SystemOwner },        // not in conflict table
            new object[] { OrganizationRole.MissionOwner,        RmfRole.Issm },                // not in conflict table
            new object[] { OrganizationRole.AuthorizingOfficial, RmfRole.MissionOwner },        // AO+MO not flagged
            new object[] { OrganizationRole.Administrator,       RmfRole.SystemOwner },         // Admin not in conflict table
            new object[] { OrganizationRole.SystemOwner,         RmfRole.MissionOwner },         // SO+MO not flagged
        };

    [Theory]
    [MemberData(nameof(NonConflict_pairs))]
    public async Task Non_conflict_pairs_emit_no_warning(OrganizationRole existing, RmfRole target)
    {
        // Arrange
        var (factory, _) = NewFactory();
        var tenantId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        await SeedOrgRoleAsync(factory, tenantId, personId, existing);
        ISoDConflictDetector detector = new SoDConflictDetector(factory);

        // Act
        var warnings = await detector.DetectAsync(tenantId, personId, target, CancellationToken.None);

        // Assert
        warnings.Should().BeEmpty(
            "the SoD pairs table is closed; non-listed (existing, target) tuples MUST NOT emit warnings");
    }

    [Fact]
    public async Task Cross_tenant_existing_roles_do_NOT_emit_warnings()
    {
        // Arrange — seed an existing AO row for the person in tenant A; query for tenant B.
        var (factory, _) = NewFactory();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var personId = Guid.NewGuid();
        await SeedOrgRoleAsync(factory, tenantA, personId, OrganizationRole.AuthorizingOfficial);

        ISoDConflictDetector detector = new SoDConflictDetector(factory);

        // Act — querying tenant B should not see tenant A's row.
        var warnings = await detector.DetectAsync(tenantB, personId, RmfRole.Issm, CancellationToken.None);

        // Assert
        warnings.Should().BeEmpty(
            "tenant isolation MUST hold — SoD detection is tenant-scoped per Feature 048 [TenantScoped] discipline");
    }
}
