using Ato.Copilot.Agents.Compliance.Services.Onboarding;
using Ato.Copilot.Core.Data.Context;
using Ato.Copilot.Core.Models.Compliance;
using Ato.Copilot.Core.Models.Onboarding;
using Ato.Copilot.Core.Services.Roles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ato.Copilot.Tests.Integration.Roles;

/// <summary>
/// T018b [US1] — Failing integration test pinning FR-024 propagation at the
/// system-create entry point.
///
/// <para>
/// Seed: 3 active <see cref="OrganizationRoleAssignment"/> rows — MissionOwner,
/// SystemOwner, Administrator — under a single tenant. Invoke
/// <see cref="IRegisteredSystemRoleSnapshotter.SnapshotAsync"/> for a brand-new
/// system id. Assert:
/// </para>
/// <list type="number">
///   <item>Exactly 2 inherited rows appear (Administrator has no RmfRole image
///         and MUST be skipped per FR-020 + FR-024 cross-enum map).</item>
///   <item>Calling SnapshotAsync again is idempotent (still 2 rows).</item>
///   <item>Rows are visible synchronously via <see cref="IUnifiedRoleReader"/>
///         on the same connection — no worker required.</item>
///   <item>A second tenant's org rows do NOT leak into Tenant A's system.</item>
/// </list>
/// </summary>
public class NewSystemInitializesInheritedRolesTests
{
    private sealed class StaticFactory : IDbContextFactory<AtoCopilotContext>
    {
        private readonly string _dbName;
        public StaticFactory(string dbName) => _dbName = dbName;
        public AtoCopilotContext CreateDbContext() => new(
            new DbContextOptionsBuilder<AtoCopilotContext>().UseInMemoryDatabase(_dbName).Options);
    }

    [Fact]
    public async Task Snapshot_skips_Administrator_and_synchronously_yields_two_inherited_rows()
    {
        // Arrange
        var dbName = $"snapinit_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var tenantId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var corr = Guid.NewGuid();
        var systemId = Guid.NewGuid().ToString();
        var moPersonId = Guid.NewGuid();
        var soPersonId = Guid.NewGuid();
        var adminPersonId = Guid.NewGuid();

        await using (var db = factory.CreateDbContext())
        {
            db.Persons.Add(new Person { Id = moPersonId, TenantId = tenantId, DisplayName = "MO", Email = "mo@x.mil" });
            db.Persons.Add(new Person { Id = soPersonId, TenantId = tenantId, DisplayName = "SO", Email = "so@x.mil" });
            db.Persons.Add(new Person { Id = adminPersonId, TenantId = tenantId, DisplayName = "Admin", Email = "admin@x.mil" });
            db.RegisteredSystems.Add(new RegisteredSystem
            {
                Id = systemId,
                TenantId = tenantId,
                Name = "New System",
                SystemType = SystemType.MajorApplication,
                MissionCriticality = MissionCriticality.MissionEssential,
            });
            db.OrganizationRoleAssignments.AddRange(
                new OrganizationRoleAssignment { TenantId = tenantId, Role = OrganizationRole.MissionOwner, PersonId = moPersonId, IsPrimary = true },
                new OrganizationRoleAssignment { TenantId = tenantId, Role = OrganizationRole.SystemOwner, PersonId = soPersonId, IsPrimary = true },
                new OrganizationRoleAssignment { TenantId = tenantId, Role = OrganizationRole.Administrator, PersonId = adminPersonId, IsPrimary = false });
            await db.SaveChangesAsync();
        }

        var snapshotter = new RegisteredSystemRoleSnapshotter(
            factory, NullLogger<RegisteredSystemRoleSnapshotter>.Instance);

        // Act
        var copiedCount = await snapshotter.SnapshotAsync(tenantId, systemId, actor, corr, CancellationToken.None);

        // Assert
        copiedCount.Should().Be(2,
            "FR-024 cross-enum map: Administrator has no RmfRole image and MUST be skipped");

        await using var assertDb = factory.CreateDbContext();
        var inherited = await assertDb.SystemRoleAssignments
            .Where(s => s.RegisteredSystemId == systemId && s.IsInherited && s.RemovedAt == null)
            .ToListAsync();
        inherited.Should().HaveCount(2);
        inherited.Select(s => s.Role).Should().BeEquivalentTo(
            new[] { OrganizationRole.MissionOwner, OrganizationRole.SystemOwner });
    }

    [Fact]
    public async Task Re_snapshot_is_idempotent()
    {
        // Arrange
        var dbName = $"snapinit_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var tenantId = Guid.NewGuid();
        var systemId = Guid.NewGuid().ToString();
        var personId = Guid.NewGuid();

        await using (var db = factory.CreateDbContext())
        {
            db.Persons.Add(new Person { Id = personId, TenantId = tenantId, DisplayName = "MO", Email = "mo@x.mil" });
            db.RegisteredSystems.Add(new RegisteredSystem
            {
                Id = systemId,
                TenantId = tenantId,
                Name = "Sys",
                SystemType = SystemType.MajorApplication,
                MissionCriticality = MissionCriticality.MissionEssential,
            });
            db.OrganizationRoleAssignments.Add(new OrganizationRoleAssignment
            {
                TenantId = tenantId, Role = OrganizationRole.MissionOwner, PersonId = personId, IsPrimary = true,
            });
            await db.SaveChangesAsync();
        }

        var snapshotter = new RegisteredSystemRoleSnapshotter(
            factory, NullLogger<RegisteredSystemRoleSnapshotter>.Instance);

        // Act
        await snapshotter.SnapshotAsync(tenantId, systemId, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        await snapshotter.SnapshotAsync(tenantId, systemId, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        // Assert
        await using var assertDb = factory.CreateDbContext();
        var count = await assertDb.SystemRoleAssignments
            .Where(s => s.RegisteredSystemId == systemId && s.IsInherited)
            .CountAsync();
        count.Should().Be(1,
            "FR-024: re-snapshotting the same system MUST be a no-op (idempotent)");
    }

    [Fact]
    public async Task Snapshot_does_not_leak_cross_tenant_org_rows()
    {
        // Arrange — Tenant B has an Org row but it must NOT seed Tenant A's system.
        var dbName = $"snapinit_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var systemA = Guid.NewGuid().ToString();
        var personA = Guid.NewGuid();
        var personB = Guid.NewGuid();

        await using (var db = factory.CreateDbContext())
        {
            db.Persons.Add(new Person { Id = personA, TenantId = tenantA, DisplayName = "A", Email = "a@x.mil" });
            db.Persons.Add(new Person { Id = personB, TenantId = tenantB, DisplayName = "B", Email = "b@x.mil" });
            db.RegisteredSystems.Add(new RegisteredSystem
            {
                Id = systemA,
                TenantId = tenantA,
                Name = "A-sys",
                SystemType = SystemType.MajorApplication,
                MissionCriticality = MissionCriticality.MissionEssential,
            });
            db.OrganizationRoleAssignments.Add(new OrganizationRoleAssignment
            {
                TenantId = tenantA, Role = OrganizationRole.MissionOwner, PersonId = personA, IsPrimary = true,
            });
            db.OrganizationRoleAssignments.Add(new OrganizationRoleAssignment
            {
                TenantId = tenantB, Role = OrganizationRole.SystemOwner, PersonId = personB, IsPrimary = true,
            });
            await db.SaveChangesAsync();
        }

        var snapshotter = new RegisteredSystemRoleSnapshotter(
            factory, NullLogger<RegisteredSystemRoleSnapshotter>.Instance);

        // Act
        await snapshotter.SnapshotAsync(tenantA, systemA, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        // Assert
        await using var assertDb = factory.CreateDbContext();
        var rows = await assertDb.SystemRoleAssignments
            .Where(s => s.RegisteredSystemId == systemA)
            .ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].PersonId.Should().Be(personA, "tenant isolation MUST hold during snapshot");
    }
}
