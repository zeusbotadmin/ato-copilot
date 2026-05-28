using Ato.Copilot.Core.Data.Context;
using Ato.Copilot.Core.Models.Compliance;
using Ato.Copilot.Core.Models.Onboarding;
using Ato.Copilot.Core.Observability;
using Ato.Copilot.Core.Services.Roles;
using Ato.Copilot.Mcp.Workers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ato.Copilot.Tests.Integration.Roles;

/// <summary>
/// T016 [US1] — Failing integration test pinning the Org-role fan-out worker.
///
/// <para>
/// Arrange a tenant with 500 active <see cref="RegisteredSystem"/> rows and one
/// active <see cref="OrganizationRoleAssignment"/>. Enqueue a single
/// <see cref="PropagationIntent"/>. Drain. Assert 500 inherited
/// <see cref="SystemRoleAssignment"/> rows materialize with
/// <c>IsInherited=true</c> and matching <c>SourceOrganizationRoleAssignmentId</c>.
/// </para>
///
/// <para>Re-enqueue the same intent: idempotent (no duplicate rows).</para>
/// <para>Startup reconciliation: produces the same end-state without an enqueue.</para>
/// </summary>
public class OrganizationRoleFanoutWorkerTests
{
    private sealed class StaticFactory : IDbContextFactory<AtoCopilotContext>
    {
        private readonly string _dbName;
        public StaticFactory(string dbName) => _dbName = dbName;
        public AtoCopilotContext CreateDbContext() => new(
            new DbContextOptionsBuilder<AtoCopilotContext>().UseInMemoryDatabase(_dbName).Options);
    }

    private static async Task<(Guid tenantId, Guid orgId, Guid personId)> SeedTenantAsync(
        IDbContextFactory<AtoCopilotContext> factory,
        int systemCount,
        OrganizationRole orgRole)
    {
        var tenantId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var orgId = Guid.NewGuid();

        await using var db = factory.CreateDbContext();
        db.Persons.Add(new Person
        {
            Id = personId,
            TenantId = tenantId,
            DisplayName = "Mission Owner",
            Email = "mo@x.mil",
        });
        for (int i = 0; i < systemCount; i++)
        {
            db.RegisteredSystems.Add(new RegisteredSystem
            {
                Id = Guid.NewGuid().ToString(),
                TenantId = tenantId,
                Name = $"System {i:000}",
                SystemType = SystemType.MajorApplication,
                MissionCriticality = MissionCriticality.MissionEssential,
            });
        }
        db.OrganizationRoleAssignments.Add(new OrganizationRoleAssignment
        {
            Id = orgId,
            TenantId = tenantId,
            Role = orgRole,
            PersonId = personId,
            IsPrimary = true,
        });
        await db.SaveChangesAsync();
        return (tenantId, orgId, personId);
    }

    [Fact]
    public async Task Single_intent_fans_out_to_500_inherited_rows()
    {
        // Arrange
        var dbName = $"fanout_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var (tenantId, orgId, personId) = await SeedTenantAsync(factory, systemCount: 500, OrganizationRole.MissionOwner);

        var queue = new OrganizationRoleFanoutQueue();
        using var metrics = new RoleMetrics();
        var worker = new OrganizationRoleFanoutWorker(
            queue, factory, metrics, NullLogger<OrganizationRoleFanoutWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        await queue.EnqueueAsync(
            new PropagationIntent(tenantId, orgId, RmfRole.MissionOwner, personId, DateTimeOffset.UtcNow),
            cts.Token);
        queue.Complete();
        await worker.RunUntilDoneAsync(cts.Token);

        // Assert
        await using var db = factory.CreateDbContext();
        var inherited = await db.SystemRoleAssignments
            .Where(s => s.TenantId == tenantId
                     && s.Role == OrganizationRole.MissionOwner
                     && s.IsInherited
                     && s.SourceOrganizationRoleAssignmentId == orgId
                     && s.RemovedAt == null)
            .CountAsync();
        inherited.Should().Be(500,
            "one intent for a tenant with 500 systems fans out to 500 inherited rows");
    }

    [Fact]
    public async Task Re_enqueue_is_idempotent_no_duplicates()
    {
        // Arrange
        var dbName = $"fanout_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var (tenantId, orgId, personId) = await SeedTenantAsync(factory, systemCount: 5, OrganizationRole.SystemOwner);

        var queue = new OrganizationRoleFanoutQueue();
        using var metrics = new RoleMetrics();
        var worker = new OrganizationRoleFanoutWorker(
            queue, factory, metrics, NullLogger<OrganizationRoleFanoutWorker>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act — enqueue the same intent twice
        var intent = new PropagationIntent(tenantId, orgId, RmfRole.SystemOwner, personId, DateTimeOffset.UtcNow);
        await queue.EnqueueAsync(intent, cts.Token);
        await queue.EnqueueAsync(intent, cts.Token);
        queue.Complete();
        await worker.RunUntilDoneAsync(cts.Token);

        // Assert
        await using var db = factory.CreateDbContext();
        var count = await db.SystemRoleAssignments
            .Where(s => s.TenantId == tenantId && s.Role == OrganizationRole.SystemOwner && s.IsInherited)
            .CountAsync();
        count.Should().Be(5,
            "re-enqueuing the same intent MUST be idempotent — uniqueness key is (TenantId, SystemId, Role, SourceOrganizationRoleAssignmentId)");
    }

    [Fact]
    public async Task Startup_reconciliation_materializes_missing_rows_without_intents()
    {
        // Arrange — tenant with 3 systems + 1 active Org row. No intent is enqueued.
        var dbName = $"fanout_{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var (tenantId, orgId, _) = await SeedTenantAsync(factory, systemCount: 3, OrganizationRole.AuthorizingOfficial);

        var queue = new OrganizationRoleFanoutQueue();
        using var metrics = new RoleMetrics();
        var worker = new OrganizationRoleFanoutWorker(
            queue, factory, metrics, NullLogger<OrganizationRoleFanoutWorker>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act — invoke startup reconciliation directly
        await worker.ReconcileOnStartupAsync(cts.Token);

        // Assert
        await using var db = factory.CreateDbContext();
        var count = await db.SystemRoleAssignments
            .Where(s => s.TenantId == tenantId
                     && s.Role == OrganizationRole.AuthorizingOfficial
                     && s.IsInherited
                     && s.SourceOrganizationRoleAssignmentId == orgId)
            .CountAsync();
        count.Should().Be(3,
            "startup reconciliation MUST materialize inherited rows for every active Org assignment even when no intent was enqueued");
    }
}
