using Ato.Copilot.Core.Data.Context;
using Ato.Copilot.Core.Models.Compliance;
using Ato.Copilot.Core.Models.Onboarding;
using Ato.Copilot.Core.Services.Roles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ato.Copilot.Tests.Unit.Roles;

/// <summary>
/// T012 [US1] — Failing test pinning the read-time precedence chain for
/// <see cref="IUnifiedRoleReader.GetSystemRolesAsync"/> per
/// <c>specs/049-unified-rmf-role-assignments/data-model.md § Read-time precedence</c>:
///
/// <list type="number">
/// <item><c>SystemRoleAssignment</c> with <c>IsInherited=false</c> (override) wins.</item>
/// <item>Then <c>SystemRoleAssignment</c> with <c>IsInherited=true</c> (inherited).</item>
/// <item>Then <c>OrganizationRoleAssignment</c> exists but no per-system inherited row
///       has materialized yet (org-fallback).</item>
/// <item>Then legacy <c>RmfRoleAssignment</c> (FR-024 read-side compatibility).</item>
/// <item>Otherwise <c>NotAssigned</c>.</item>
/// </list>
///
/// <para>
/// Also pins: (a) <c>IsPrimary</c> wins ties at the Org-fallback layer, (b) the
/// most-recent <c>CreatedAt</c> wins ties when neither row is <c>IsPrimary</c>,
/// (c) tenant isolation (cross-tenant rows are never returned), (d) the snapshot
/// always carries exactly six <see cref="RmfRole"/> rows.
/// </para>
/// </summary>
public class UnifiedRoleReaderTests
{
    private static AtoCopilotContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AtoCopilotContext>()
            .UseInMemoryDatabase(name)
            .Options);

    private sealed class StaticFactory : IDbContextFactory<AtoCopilotContext>
    {
        private readonly string _dbName;
        public StaticFactory(string dbName) => _dbName = dbName;
        public AtoCopilotContext CreateDbContext() => NewDb(_dbName);
    }

    private static async Task SeedPersonAsync(
        AtoCopilotContext db,
        Guid tenantId,
        Guid personId,
        string displayName)
    {
        db.Persons.Add(new Person
        {
            Id = personId,
            TenantId = tenantId,
            DisplayName = displayName,
            Email = $"{displayName.ToLowerInvariant().Replace(' ', '.')}@x.mil",
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds a <see cref="RegisteredSystem"/> row so that
    /// <see cref="UnifiedRoleReader.GetSystemRolesAsync"/>'s system-existence
    /// pre-check passes (the reader returns six <c>NotAssigned</c> rows when
    /// the system does not exist in the queried tenant — this is the
    /// FR-004/SC-006 cross-tenant safety net pinned by
    /// <c>TenantIsolationRolesTests</c>).
    /// </summary>
    private static async Task SeedSystemAsync(
        AtoCopilotContext db,
        Guid tenantId,
        string systemId)
    {
        db.RegisteredSystems.Add(new RegisteredSystem
        {
            Id = systemId,
            TenantId = tenantId,
            Name = $"Sys-{systemId[..8]}",
            SystemType = SystemType.MajorApplication,
            MissionCriticality = MissionCriticality.MissionEssential,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Snapshot_always_has_six_rmf_role_rows_even_when_unassigned()
    {
        // Arrange
        var dbName = $"unified_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var tenantId = Guid.NewGuid();
        var systemId = Guid.NewGuid().ToString();
        IUnifiedRoleReader reader = new UnifiedRoleReader(factory);

        // Act
        var snapshot = await reader.GetSystemRolesAsync(tenantId, systemId, CancellationToken.None);

        // Assert
        snapshot.TenantId.Should().Be(tenantId);
        snapshot.RegisteredSystemId.Should().Be(systemId);
        snapshot.Roles.Should().HaveCount(6,
            "every RmfRole must be represented in the snapshot — empty layers are " +
            "encoded as RoleAssignmentSource.NotAssigned, not omitted");
        snapshot.Roles.Select(r => r.Role).Should().BeEquivalentTo(Enum.GetValues<RmfRole>());
        snapshot.Roles.Should().AllSatisfy(r =>
        {
            r.Source.Should().Be(RoleAssignmentSource.NotAssigned);
            r.PersonId.Should().BeNull();
            r.PersonDisplayName.Should().BeNull();
            r.OrgRoleId.Should().BeNull();
        });
    }

    [Fact]
    public async Task Override_wins_over_inherited_and_org_and_legacy()
    {
        // Arrange
        var dbName = $"unified_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var tenantId = Guid.NewGuid();
        var systemId = Guid.NewGuid().ToString();
        var overridePersonId = Guid.NewGuid();
        var inheritedPersonId = Guid.NewGuid();
        var orgPersonId = Guid.NewGuid();
        var legacyUserId = "legacy.user@x.mil";
        var orgAssignmentId = Guid.NewGuid();

        await using (var db = NewDb(dbName))
        {
            await SeedSystemAsync(db, tenantId, systemId);
            await SeedPersonAsync(db, tenantId, overridePersonId, "Override Owner");
            await SeedPersonAsync(db, tenantId, inheritedPersonId, "Inherited Owner");
            await SeedPersonAsync(db, tenantId, orgPersonId, "Org Owner");

            // Legacy row
            db.RmfRoleAssignments.Add(new RmfRoleAssignment
            {
                Id = Guid.NewGuid().ToString(),
                TenantId = tenantId,
                RegisteredSystemId = systemId,
                RmfRole = RmfRole.SystemOwner,
                UserId = legacyUserId,
                UserDisplayName = "Legacy User",
                AssignedBy = "test",
                IsActive = true,
            });
            // Org-level row (for OrgFallback layer)
            db.OrganizationRoleAssignments.Add(new OrganizationRoleAssignment
            {
                Id = orgAssignmentId,
                TenantId = tenantId,
                Role = OrganizationRole.SystemOwner,
                PersonId = orgPersonId,
                IsPrimary = true,
            });
            // Inherited per-system row
            db.SystemRoleAssignments.Add(new SystemRoleAssignment
            {
                TenantId = tenantId,
                RegisteredSystemId = systemId,
                Role = OrganizationRole.SystemOwner,
                PersonId = inheritedPersonId,
                IsInherited = true,
                SourceOrganizationRoleAssignmentId = orgAssignmentId,
            });
            // Override (IsInherited=false)
            db.SystemRoleAssignments.Add(new SystemRoleAssignment
            {
                TenantId = tenantId,
                RegisteredSystemId = systemId,
                Role = OrganizationRole.SystemOwner,
                PersonId = overridePersonId,
                IsInherited = false,
            });
            await db.SaveChangesAsync();
        }

        IUnifiedRoleReader reader = new UnifiedRoleReader(factory);

        // Act
        var snapshot = await reader.GetSystemRolesAsync(tenantId, systemId, CancellationToken.None);

        // Assert
        var systemOwnerRow = snapshot.Roles.Single(r => r.Role == RmfRole.SystemOwner);
        systemOwnerRow.Source.Should().Be(RoleAssignmentSource.Override);
        systemOwnerRow.PersonId.Should().Be(overridePersonId);
        systemOwnerRow.PersonDisplayName.Should().Be("Override Owner");
        systemOwnerRow.OrgRoleId.Should().BeNull(
            "Override rows do not carry the Org assignment id — only Inherited and OrgFallback do");
    }

    [Fact]
    public async Task Inherited_wins_over_org_and_legacy_when_no_override()
    {
        // Arrange
        var dbName = $"unified_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var tenantId = Guid.NewGuid();
        var systemId = Guid.NewGuid().ToString();
        var inheritedPersonId = Guid.NewGuid();
        var orgPersonId = Guid.NewGuid();
        var orgAssignmentId = Guid.NewGuid();

        await using (var db = NewDb(dbName))
        {
            await SeedSystemAsync(db, tenantId, systemId);
            await SeedPersonAsync(db, tenantId, inheritedPersonId, "Inherited Owner");
            await SeedPersonAsync(db, tenantId, orgPersonId, "Org Owner");

            db.RmfRoleAssignments.Add(new RmfRoleAssignment
            {
                Id = Guid.NewGuid().ToString(),
                TenantId = tenantId,
                RegisteredSystemId = systemId,
                RmfRole = RmfRole.MissionOwner,
                UserId = "legacy@x.mil",
                AssignedBy = "test",
                IsActive = true,
            });
            db.OrganizationRoleAssignments.Add(new OrganizationRoleAssignment
            {
                Id = orgAssignmentId,
                TenantId = tenantId,
                Role = OrganizationRole.MissionOwner,
                PersonId = orgPersonId,
                IsPrimary = true,
            });
            db.SystemRoleAssignments.Add(new SystemRoleAssignment
            {
                TenantId = tenantId,
                RegisteredSystemId = systemId,
                Role = OrganizationRole.MissionOwner,
                PersonId = inheritedPersonId,
                IsInherited = true,
                SourceOrganizationRoleAssignmentId = orgAssignmentId,
            });
            await db.SaveChangesAsync();
        }

        IUnifiedRoleReader reader = new UnifiedRoleReader(factory);

        // Act
        var snapshot = await reader.GetSystemRolesAsync(tenantId, systemId, CancellationToken.None);

        // Assert
        var row = snapshot.Roles.Single(r => r.Role == RmfRole.MissionOwner);
        row.Source.Should().Be(RoleAssignmentSource.Inherited);
        row.PersonId.Should().Be(inheritedPersonId);
        row.OrgRoleId.Should().Be(orgAssignmentId);
    }

    [Fact]
    public async Task OrgFallback_wins_over_legacy_when_no_per_system_row()
    {
        // Arrange
        var dbName = $"unified_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var tenantId = Guid.NewGuid();
        var systemId = Guid.NewGuid().ToString();
        var orgPersonId = Guid.NewGuid();
        var orgAssignmentId = Guid.NewGuid();

        await using (var db = NewDb(dbName))
        {
            await SeedSystemAsync(db, tenantId, systemId);
            await SeedPersonAsync(db, tenantId, orgPersonId, "Org AO");

            db.RmfRoleAssignments.Add(new RmfRoleAssignment
            {
                Id = Guid.NewGuid().ToString(),
                TenantId = tenantId,
                RegisteredSystemId = systemId,
                RmfRole = RmfRole.AuthorizingOfficial,
                UserId = "legacy@x.mil",
                AssignedBy = "test",
                IsActive = true,
            });
            db.OrganizationRoleAssignments.Add(new OrganizationRoleAssignment
            {
                Id = orgAssignmentId,
                TenantId = tenantId,
                Role = OrganizationRole.AuthorizingOfficial,
                PersonId = orgPersonId,
                IsPrimary = true,
            });
            await db.SaveChangesAsync();
        }

        IUnifiedRoleReader reader = new UnifiedRoleReader(factory);

        // Act
        var snapshot = await reader.GetSystemRolesAsync(tenantId, systemId, CancellationToken.None);

        // Assert
        var row = snapshot.Roles.Single(r => r.Role == RmfRole.AuthorizingOfficial);
        row.Source.Should().Be(RoleAssignmentSource.OrgFallback);
        row.PersonId.Should().Be(orgPersonId);
        row.PersonDisplayName.Should().Be("Org AO");
        row.OrgRoleId.Should().Be(orgAssignmentId);
    }

    [Fact]
    public async Task Legacy_wins_when_no_org_and_no_per_system_rows()
    {
        // Arrange
        var dbName = $"unified_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var tenantId = Guid.NewGuid();
        var systemId = Guid.NewGuid().ToString();

        await using (var db = NewDb(dbName))
        {
            await SeedSystemAsync(db, tenantId, systemId);
            db.RmfRoleAssignments.Add(new RmfRoleAssignment
            {
                Id = Guid.NewGuid().ToString(),
                TenantId = tenantId,
                RegisteredSystemId = systemId,
                RmfRole = RmfRole.Issm,
                UserId = "legacy.issm@x.mil",
                UserDisplayName = "Legacy ISSM",
                AssignedBy = "test",
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        IUnifiedRoleReader reader = new UnifiedRoleReader(factory);

        // Act
        var snapshot = await reader.GetSystemRolesAsync(tenantId, systemId, CancellationToken.None);

        // Assert
        var row = snapshot.Roles.Single(r => r.Role == RmfRole.Issm);
        row.Source.Should().Be(RoleAssignmentSource.Legacy);
        row.PersonDisplayName.Should().Be("Legacy ISSM",
            "the legacy reader projects RmfRoleAssignment.UserDisplayName into PersonDisplayName");
    }

    [Fact]
    public async Task Soft_removed_rows_are_not_returned()
    {
        // Arrange
        var dbName = $"unified_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var tenantId = Guid.NewGuid();
        var systemId = Guid.NewGuid().ToString();
        var personId = Guid.NewGuid();

        await using (var db = NewDb(dbName))
        {
            await SeedSystemAsync(db, tenantId, systemId);
            await SeedPersonAsync(db, tenantId, personId, "Removed Owner");
            db.SystemRoleAssignments.Add(new SystemRoleAssignment
            {
                TenantId = tenantId,
                RegisteredSystemId = systemId,
                Role = OrganizationRole.Issm,
                PersonId = personId,
                IsInherited = false,
                RemovedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            });
            await db.SaveChangesAsync();
        }

        IUnifiedRoleReader reader = new UnifiedRoleReader(factory);

        // Act
        var snapshot = await reader.GetSystemRolesAsync(tenantId, systemId, CancellationToken.None);

        // Assert
        snapshot.Roles.Single(r => r.Role == RmfRole.Issm).Source
            .Should().Be(RoleAssignmentSource.NotAssigned,
                "soft-removed rows MUST NOT appear in the unified read");
    }

    [Fact]
    public async Task IsPrimary_wins_at_org_fallback_layer_on_ties()
    {
        // Arrange — two OrgRole rows for the same role; the IsPrimary=true row wins.
        var dbName = $"unified_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var tenantId = Guid.NewGuid();
        var systemId = Guid.NewGuid().ToString();
        var primaryId = Guid.NewGuid();
        var secondaryId = Guid.NewGuid();

        await using (var db = NewDb(dbName))
        {
            await SeedSystemAsync(db, tenantId, systemId);
            await SeedPersonAsync(db, tenantId, primaryId, "Primary AO");
            await SeedPersonAsync(db, tenantId, secondaryId, "Secondary AO");
            db.OrganizationRoleAssignments.Add(new OrganizationRoleAssignment
            {
                TenantId = tenantId,
                Role = OrganizationRole.AuthorizingOfficial,
                PersonId = secondaryId,
                IsPrimary = false,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.OrganizationRoleAssignments.Add(new OrganizationRoleAssignment
            {
                TenantId = tenantId,
                Role = OrganizationRole.AuthorizingOfficial,
                PersonId = primaryId,
                IsPrimary = true,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10), // older but primary
            });
            await db.SaveChangesAsync();
        }

        IUnifiedRoleReader reader = new UnifiedRoleReader(factory);

        // Act
        var snapshot = await reader.GetSystemRolesAsync(tenantId, systemId, CancellationToken.None);

        // Assert
        snapshot.Roles.Single(r => r.Role == RmfRole.AuthorizingOfficial).PersonId
            .Should().Be(primaryId,
                "IsPrimary beats CreatedAt at the Org-fallback layer per data-model.md");
    }

    [Fact]
    public async Task Most_recent_CreatedAt_wins_when_no_IsPrimary()
    {
        // Arrange — two OrgRole rows, neither IsPrimary; the most recent wins.
        var dbName = $"unified_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var tenantId = Guid.NewGuid();
        var systemId = Guid.NewGuid().ToString();
        var olderId = Guid.NewGuid();
        var newerId = Guid.NewGuid();

        await using (var db = NewDb(dbName))
        {
            await SeedSystemAsync(db, tenantId, systemId);
            await SeedPersonAsync(db, tenantId, olderId, "Older AO");
            await SeedPersonAsync(db, tenantId, newerId, "Newer AO");
            db.OrganizationRoleAssignments.Add(new OrganizationRoleAssignment
            {
                TenantId = tenantId,
                Role = OrganizationRole.AuthorizingOfficial,
                PersonId = olderId,
                IsPrimary = false,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
            db.OrganizationRoleAssignments.Add(new OrganizationRoleAssignment
            {
                TenantId = tenantId,
                Role = OrganizationRole.AuthorizingOfficial,
                PersonId = newerId,
                IsPrimary = false,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        IUnifiedRoleReader reader = new UnifiedRoleReader(factory);

        // Act
        var snapshot = await reader.GetSystemRolesAsync(tenantId, systemId, CancellationToken.None);

        // Assert
        snapshot.Roles.Single(r => r.Role == RmfRole.AuthorizingOfficial).PersonId
            .Should().Be(newerId,
                "the most-recent CreatedAt wins at the Org-fallback layer when neither row is IsPrimary");
    }

    [Fact]
    public async Task GetMissionOwnerAsync_returns_null_when_no_layer_supplies_one()
    {
        // Arrange
        var dbName = $"unified_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var tenantId = Guid.NewGuid();
        var systemId = Guid.NewGuid().ToString();
        IUnifiedRoleReader reader = new UnifiedRoleReader(factory);

        // Act
        var mo = await reader.GetMissionOwnerAsync(tenantId, systemId, CancellationToken.None);

        // Assert
        mo.Should().BeNull();
    }

    [Fact]
    public async Task GetMissionOwnerAsync_returns_resolved_assignment_when_org_row_exists()
    {
        // Arrange — only an Org-level MissionOwner; system has no per-system row yet.
        var dbName = $"unified_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var tenantId = Guid.NewGuid();
        var systemId = Guid.NewGuid().ToString();
        var personId = Guid.NewGuid();

        await using (var db = NewDb(dbName))
        {
            await SeedSystemAsync(db, tenantId, systemId);
            await SeedPersonAsync(db, tenantId, personId, "MO Person");
            db.OrganizationRoleAssignments.Add(new OrganizationRoleAssignment
            {
                TenantId = tenantId,
                Role = OrganizationRole.MissionOwner,
                PersonId = personId,
                IsPrimary = true,
            });
            await db.SaveChangesAsync();
        }

        IUnifiedRoleReader reader = new UnifiedRoleReader(factory);

        // Act
        var mo = await reader.GetMissionOwnerAsync(tenantId, systemId, CancellationToken.None);

        // Assert
        mo.Should().NotBeNull();
        mo!.Value.Role.Should().Be(RmfRole.MissionOwner);
        mo.Value.PersonId.Should().Be(personId);
        mo.Value.Source.Should().Be(RoleAssignmentSource.OrgFallback,
            "the Org-level Mission Owner clears the banner via OrgFallback in the absence of a per-system row (FR-029)");
    }
}
