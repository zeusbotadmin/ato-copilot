using Ato.Copilot.Agents.Compliance.Services.Onboarding;
using Ato.Copilot.Core.Data.Context;
using Ato.Copilot.Core.Interfaces.Onboarding;
using Ato.Copilot.Core.Models.Onboarding;
using Ato.Copilot.Core.Services.Roles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Ato.Copilot.Tests.Integration.Roles;

/// <summary>
/// T017a [US1] — Failing integration test pinning FR-007 cascade soft-remove.
///
/// <para>
/// Seed: 1 active <see cref="OrganizationRoleAssignment"/> + 3 inherited
/// <see cref="SystemRoleAssignment"/> rows + 1 per-system override row.
/// Soft-remove the Org row via <see cref="OrganizationRoleAssignmentService"/>.
/// Assert:
/// </para>
/// <list type="number">
///   <item>All 3 inherited rows are soft-removed in the SAME SaveChangesAsync
///         (matching <c>RemovedAt</c> timestamp).</item>
///   <item>The override row (<c>IsInherited=false</c>) is preserved.</item>
/// </list>
/// </summary>
public class OrgRoleSoftRemoveCascadeTests
{
    private sealed class StaticFactory : IDbContextFactory<AtoCopilotContext>
    {
        private readonly string _dbName;
        public StaticFactory(string dbName) => _dbName = dbName;
        public AtoCopilotContext CreateDbContext() => new(
            new DbContextOptionsBuilder<AtoCopilotContext>().UseInMemoryDatabase(_dbName).Options);
    }

    [Fact]
    public async Task Soft_remove_Org_row_cascades_to_inherited_but_not_overrides()
    {
        // Arrange
        var dbName = $"cascade_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var tenantId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var overridePersonId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var system1 = Guid.NewGuid().ToString();
        var system2 = Guid.NewGuid().ToString();
        var system3 = Guid.NewGuid().ToString();

        await using (var db = factory.CreateDbContext())
        {
            db.Persons.Add(new Person { Id = personId, TenantId = tenantId, DisplayName = "Org MO", Email = "mo@x.mil" });
            db.Persons.Add(new Person { Id = overridePersonId, TenantId = tenantId, DisplayName = "Override MO", Email = "ovr@x.mil" });
            db.OrganizationRoleAssignments.Add(new OrganizationRoleAssignment
            {
                Id = orgId,
                TenantId = tenantId,
                Role = OrganizationRole.MissionOwner,
                PersonId = personId,
                IsPrimary = true,
            });
            // 3 inherited rows (one per system)
            foreach (var sid in new[] { system1, system2, system3 })
            {
                db.SystemRoleAssignments.Add(new SystemRoleAssignment
                {
                    TenantId = tenantId,
                    RegisteredSystemId = sid,
                    Role = OrganizationRole.MissionOwner,
                    PersonId = personId,
                    IsInherited = true,
                    SourceOrganizationRoleAssignmentId = orgId,
                });
            }
            // 1 per-system override row on system1 (must be preserved)
            db.SystemRoleAssignments.Add(new SystemRoleAssignment
            {
                TenantId = tenantId,
                RegisteredSystemId = system1,
                Role = OrganizationRole.MissionOwner,
                PersonId = overridePersonId,
                IsInherited = false,
                SourceOrganizationRoleAssignmentId = null,
            });
            await db.SaveChangesAsync();
        }

        var auditMock = new Mock<IWizardAuditService>();
        var service = new OrganizationRoleAssignmentService(
            factory, auditMock.Object,
            NullLogger<OrganizationRoleAssignmentService>.Instance);

        // Act
        await service.RemoveAsync(tenantId, orgId, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        // Assert
        await using var assertDb = factory.CreateDbContext();
        var orgRow = await assertDb.OrganizationRoleAssignments.AsNoTracking().FirstAsync(r => r.Id == orgId);
        orgRow.RemovedAt.Should().NotBeNull("the Org row itself must be soft-removed");

        var inherited = await assertDb.SystemRoleAssignments
            .AsNoTracking()
            .Where(s => s.SourceOrganizationRoleAssignmentId == orgId)
            .ToListAsync();
        inherited.Should().HaveCount(3);
        inherited.Should().AllSatisfy(s => s.RemovedAt.Should().NotBeNull(
            "FR-007: every inherited row pointing at the removed Org row MUST be soft-removed in the same SaveChangesAsync"));
        inherited.Select(s => s.RemovedAt).Distinct().Should().HaveCount(1,
            "all cascaded soft-removes share a single RemovedAt timestamp (single SaveChangesAsync)");

        var overrideRow = await assertDb.SystemRoleAssignments
            .AsNoTracking()
            .SingleAsync(s => s.RegisteredSystemId == system1 && s.IsInherited == false);
        overrideRow.RemovedAt.Should().BeNull(
            "the override row (IsInherited=false) MUST be preserved — per-system overrides outlive their Org-row default");
    }
}
