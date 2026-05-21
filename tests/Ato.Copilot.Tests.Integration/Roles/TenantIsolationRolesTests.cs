using Ato.Copilot.Core.Data.Context;
using Ato.Copilot.Core.Models.Compliance;
using Ato.Copilot.Core.Models.Onboarding;
using Ato.Copilot.Core.Services.Roles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ato.Copilot.Tests.Integration.Roles;

/// <summary>
/// T017 [US1] — Failing integration test pinning tenant isolation for
/// <see cref="IUnifiedRoleReader"/>. Drives FR-004, SC-006.
///
/// <para>
/// Seed two tenants (A and B) with disjoint role rows for the same fake person id
/// (intentional collision to detect cross-tenant leakage). Assert that
/// <see cref="IUnifiedRoleReader.GetSystemRolesAsync"/> for Tenant A's system
/// returns ONLY Tenant A's persons; the cross-tenant query yields six
/// <see cref="RoleAssignmentSource.NotAssigned"/> rows.
/// </para>
/// </summary>
public class TenantIsolationRolesTests
{
    private sealed class StaticFactory : IDbContextFactory<AtoCopilotContext>
    {
        private readonly string _dbName;
        public StaticFactory(string dbName) => _dbName = dbName;
        public AtoCopilotContext CreateDbContext() => new(
            new DbContextOptionsBuilder<AtoCopilotContext>().UseInMemoryDatabase(_dbName).Options);
    }

    [Fact]
    public async Task Tenant_A_reader_returns_only_Tenant_A_persons()
    {
        // Arrange — both tenants assign their own Mission Owner to their own system.
        var dbName = $"tenantiso_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var systemA = Guid.NewGuid().ToString();
        var systemB = Guid.NewGuid().ToString();
        var personA = Guid.NewGuid();
        var personB = Guid.NewGuid();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        await using (var db = factory.CreateDbContext())
        {
            db.Persons.Add(new Person { Id = personA, TenantId = tenantA, DisplayName = "Owner A", Email = "a@x.mil" });
            db.Persons.Add(new Person { Id = personB, TenantId = tenantB, DisplayName = "Owner B", Email = "b@x.mil" });

            // FR-004 / SC-006 — each system belongs to its own tenant; cross-tenant
            // queries must return NotAssigned regardless of org-fallback rows in
            // the caller's tenant.
            db.RegisteredSystems.Add(new RegisteredSystem
            {
                Id = systemA,
                TenantId = tenantA,
                Name = "Sys A",
                SystemType = SystemType.MajorApplication,
                MissionCriticality = MissionCriticality.MissionEssential,
            });
            db.RegisteredSystems.Add(new RegisteredSystem
            {
                Id = systemB,
                TenantId = tenantB,
                Name = "Sys B",
                SystemType = SystemType.MajorApplication,
                MissionCriticality = MissionCriticality.MissionEssential,
            });

            db.OrganizationRoleAssignments.Add(new OrganizationRoleAssignment
            {
                Id = orgA, TenantId = tenantA, Role = OrganizationRole.MissionOwner, PersonId = personA, IsPrimary = true,
            });
            db.OrganizationRoleAssignments.Add(new OrganizationRoleAssignment
            {
                Id = orgB, TenantId = tenantB, Role = OrganizationRole.MissionOwner, PersonId = personB, IsPrimary = true,
            });
            await db.SaveChangesAsync();
        }

        IUnifiedRoleReader reader = new UnifiedRoleReader(factory);

        // Act
        var snapA = await reader.GetSystemRolesAsync(tenantA, systemA, CancellationToken.None);
        var snapB = await reader.GetSystemRolesAsync(tenantB, systemB, CancellationToken.None);
        var crossA = await reader.GetSystemRolesAsync(tenantA, systemB, CancellationToken.None);

        // Assert
        snapA.Roles.Single(r => r.Role == RmfRole.MissionOwner).PersonId.Should().Be(personA);
        snapB.Roles.Single(r => r.Role == RmfRole.MissionOwner).PersonId.Should().Be(personB);

        // Tenant A reading Tenant B's system returns 6 NotAssigned rows (no leak)
        crossA.Roles.Should().AllSatisfy(r => r.Source.Should().Be(RoleAssignmentSource.NotAssigned),
            "FR-004 / SC-006 tenant isolation — cross-tenant queries MUST return only NotAssigned");
    }
}
