using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ato.Copilot.Core.Data.Context;
using Ato.Copilot.Core.Models.Compliance;
using Ato.Copilot.Core.Models.Tenancy;
using Ato.Copilot.Mcp;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ato.Copilot.Tests.Integration.Tenancy;

/// <summary>
/// T176 [US8]: simulates a CSP-Admin clicking a tenant row in the dashboard
/// (acceptance scenario 5). The click translates to
/// <c>POST /api/tenants/{id}/impersonate</c>; the subsequent
/// <c>GET /api/dashboard/systems</c> must scope to that tenant.
/// </summary>
/// <remarks>
/// The test fixture pins <c>Tenant:Resolution:BypassForTests=true</c> so the
/// production middleware does not consume the impersonation cookie. To keep
/// the assertion meaningful we (a) verify the impersonate POST returns 200 +
/// a <c>impersonatedTenantId</c> envelope (matching the contract) and
/// (b) flip <see cref="ITenantContext.ImpersonatedTenantId"/> on the
/// fixture's active context to mirror what the middleware would do, then
/// assert <c>/api/dashboard/systems</c> is scoped to Tenant B.
/// </remarks>
[Collection("Tenancy")]
public class CspDashboardDrillThroughTests
    : IClassFixture<MultiTenantWebApplicationFactory<McpProgram>>
{
    private readonly MultiTenantWebApplicationFactory<McpProgram> _factory;
    private readonly HttpClient _client;

    public CspDashboardDrillThroughTests(MultiTenantWebApplicationFactory<McpProgram> factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new() { HandleCookies = true });

        var ctx = factory.GetActiveContext();
        ctx.TenantId = MultiTenantWebApplicationFactory<McpProgram>.TenantAId;
        ctx.IsCspAdmin = true;
        ctx.ImpersonatedTenantId = null;
        ctx.Status = TenantStatus.Active;

        SeedAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task DashboardRowClick_ImpersonatesTenant_AndSubsequentSystemsAreScoped()
    {
        // Arrange — Tenant A has 1 system, Tenant B has 2 systems (see seed).
        var tenantBId = MultiTenantWebApplicationFactory<McpProgram>.TenantBId;

        // Act — the row click POSTs the impersonate endpoint.
        var startResp = await _client.PostAsync(
            $"/api/tenants/{tenantBId}/impersonate", content: null);

        startResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var startBody = await startResp.Content.ReadFromJsonAsync<JsonElement>();
        startBody.GetProperty("status").GetString().Should().Be("success");
        startBody.GetProperty("data").GetProperty("impersonatedTenantId").GetGuid()
            .Should().Be(tenantBId);

        // Mirror what TenantResolutionMiddleware would do once the
        // impersonation cookie is presented on the next request — flip the
        // fixture context so the EF query filter targets Tenant B.
        _factory.GetActiveContext().ImpersonatedTenantId = tenantBId;

        // Act — drill into the per-tenant dashboard.
        var sysResp = await _client.GetAsync("/api/dashboard/systems");

        // Assert
        sysResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var sysBody = await sysResp.Content.ReadFromJsonAsync<JsonElement>();
        sysBody.TryGetProperty("items", out var items).Should().BeTrue();
        items.GetArrayLength().Should().Be(2,
            "Tenant B has exactly 2 seeded systems; Tenant A's 1 system must be filtered out.");

        foreach (var item in items.EnumerateArray())
        {
            item.GetProperty("name").GetString()
                .Should().StartWith("B-System-", "all rows must belong to Tenant B.");
        }
    }

    private async Task SeedAsync()
    {
        var factory = _factory.Services.GetRequiredService<IDbContextFactory<AtoCopilotContext>>();
        await using var db = await factory.CreateDbContextAsync();

        // Idempotent reset of just the system rows we own — keep tenants and
        // CspProfile intact for the shared fixture.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"RegisteredSystems\";");

        var aId = MultiTenantWebApplicationFactory<McpProgram>.TenantAId;
        var bId = MultiTenantWebApplicationFactory<McpProgram>.TenantBId;

        db.RegisteredSystems.AddRange(
            new RegisteredSystem
            {
                TenantId = aId,
                Name = "A-System-1",
                SystemType = SystemType.MajorApplication,
                MissionCriticality = MissionCriticality.MissionEssential,
                HostingEnvironment = "Azure Government",
            },
            new RegisteredSystem
            {
                TenantId = bId,
                Name = "B-System-1",
                SystemType = SystemType.MajorApplication,
                MissionCriticality = MissionCriticality.MissionEssential,
                HostingEnvironment = "Azure Government",
            },
            new RegisteredSystem
            {
                TenantId = bId,
                Name = "B-System-2",
                SystemType = SystemType.Enclave,
                MissionCriticality = MissionCriticality.MissionSupport,
                HostingEnvironment = "Azure Government",
            });

        await db.SaveChangesAsync();
    }
}
