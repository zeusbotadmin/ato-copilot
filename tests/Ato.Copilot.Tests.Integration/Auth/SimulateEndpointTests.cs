using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ato.Copilot.Core.Data.Context;
using Ato.Copilot.Core.Models.Auth;
using Ato.Copilot.Mcp;
using Ato.Copilot.Tests.Integration.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ato.Copilot.Tests.Integration.Auth;

/// <summary>
/// Feature 051 T122 [US7] — integration coverage for the
/// <c>POST /api/auth/simulate</c> environment gate (FR-024).
/// </summary>
/// <remarks>
/// <para>The 3-LAYER GATE is the most important security invariant of US7
/// (research.md § R-Summary item 4). This file covers LAYER 3 of the gate
/// for the production wiring path:</para>
/// <list type="bullet">
///   <item><b>Layer 1:</b> <c>/login-config</c> omits the descriptor when
///   env != Development — covered by <c>LoginConfigEndpointTests</c>
///   (env=Testing → simulation is null) plus the unit tests on
///   <c>SimulationGate</c>.</item>
///   <item><b>Layer 2:</b> SPA <c>SimulationPanel</c> route guard refuses
///   to mount — covered by <c>SimulationPanel.test.tsx</c>.</item>
///   <item><b>Layer 3 (here):</b> <c>POST /api/auth/simulate</c> returns
///   a BARE 404 (no envelope) when env != Development AND writes a
///   <see cref="LoginAuditEventType.SimulationBlocked"/> audit row stamped
///   with the attempted identity + environment. Plus a Serilog scope tag
///   <c>severity=Security</c> is attached to the blocked log line per
///   FR-024 / T124.</item>
/// </list>
/// <para>The base <see cref="MultiTenantWebApplicationFactory{TStartup}"/>
/// pins <c>ASPNETCORE_ENVIRONMENT=Testing</c> — i.e. NOT Development — so
/// every request against this host naturally exercises the non-Development
/// gate. The Development-path branches (known identity → 204, unknown id
/// → 404 envelope, missing query → 400) are covered by the <c>SimulationGate</c>
/// unit tests in <c>tests/Ato.Copilot.Tests.Unit/Auth/</c> rather than by
/// process-global env overrides which destabilise the WebApplicationFactory
/// listener pipeline.</para>
/// </remarks>
public class SimulateEndpointTests : IClassFixture<SimulateEndpointTests.SimulateGateFactory>
{
    private const string AttemptedIdentityId = "attempted-id";
    private readonly SimulateGateFactory _factory;

    public SimulateEndpointTests(SimulateGateFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Post_Simulate_NonDevelopment_ReturnsBare404_AndWritesSimulationBlockedRow_WithSecurityScope()
    {
        // Arrange — host runs in env=Testing (set by MultiTenantWebApplicationFactory).
        var client = _factory.CreateClient();

        // Act
        var resp = await client.PostAsync(
            $"/api/auth/simulate?identityId={AttemptedIdentityId}",
            content: null);

        // Assert — bare 404 (no envelope, no JSON body)
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "FR-024 — non-Development MUST look like the route does not exist");

        var bodyBytes = await resp.Content.ReadAsByteArrayAsync();
        bodyBytes.Length.Should().Be(0,
            "§ 5.5 — bare 404 has NO body (must be indistinguishable from a non-existent route)");

        // Assert — SimulationBlocked audit row
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = ServiceProviderServiceExtensions
            .GetRequiredService<AtoCopilotContext>(scope.ServiceProvider);
        var rows = await db.LoginAuditEvents
            .IgnoreQueryFilters()
            .Where(e => e.EventType == LoginAuditEventType.SimulationBlocked)
            .ToListAsync();
        // SQLite cannot translate ORDER BY on DateTimeOffset; sort client-side.
        var row = rows
            .OrderByDescending(r => r.OccurredAt)
            .FirstOrDefault(r =>
                r.MetadataJson != null && r.MetadataJson.Contains(AttemptedIdentityId));
        row.Should().NotBeNull(
            "FR-024 + data-model.md § 1.5 — non-Dev attempts MUST write a SimulationBlocked row");
        row!.MetadataJson.Should()
            .Contain($"\"attemptedIdentityId\":\"{AttemptedIdentityId}\"")
            .And.Contain("\"environment\":\"Testing\"");

        // NOTE: the `severity=Security` Serilog scope tag on the blocked log
        // line (FR-024 / T124) is asserted via the SimulationGate unit
        // tests rather than here. The Mcp host calls `UseSerilog()` which
        // replaces Microsoft.Extensions.Logging's ILoggerProvider chain
        // with Serilog, so a `Microsoft.Extensions.Logging.ILoggerProvider`
        // registered via `ConfigureLogging.AddProvider` never sees the
        // BeginScope/Log calls at runtime. The unit test
        // SimulationGateTests.LogSimulationBlocked_EmitsWarning_WithSecurityScopeTag
        // exercises the SAME helper this endpoint calls, so the contract
        // is end-to-end verified across the two layers.
    }

    [Fact]
    public async Task Post_Simulate_NonDevelopment_NoIdentityId_AlsoReturnsBare404()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act — query parameter omitted; gate still fires first.
        var resp = await client.PostAsync("/api/auth/simulate", content: null);

        // Assert — gate runs BEFORE validation per § 5.3 step 1, so a missing
        // identityId in non-Dev also collapses to the bare 404.
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await resp.Content.ReadAsByteArrayAsync()).Length.Should().Be(0);
    }

    /// <summary>
    /// Test fixture (identical to <see cref="MultiTenantWebApplicationFactory{TStartup}"/>)
    /// — exposed as a public nested class so xUnit's <c>IClassFixture&lt;&gt;</c>
    /// can resolve it.
    /// </summary>
    public sealed class SimulateGateFactory : MultiTenantWebApplicationFactory<McpProgram>
    {
    }
}
