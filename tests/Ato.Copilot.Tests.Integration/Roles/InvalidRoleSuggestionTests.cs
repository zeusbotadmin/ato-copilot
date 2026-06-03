using System.Reflection;
using System.Text.Json;
using Ato.Copilot.Agents.Compliance.Services.Onboarding;
using Ato.Copilot.Core.Data.Context;
using Ato.Copilot.Core.Interfaces.Onboarding;
using Ato.Copilot.Core.Models.Compliance;
using Ato.Copilot.Core.Models.Onboarding;
using Ato.Copilot.Core.Observability;
using Ato.Copilot.Core.Services.Roles;
using Ato.Copilot.Mcp.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Ato.Copilot.Tests.Integration.Roles;

/// <summary>
/// T032 [US2] — Cross-endpoint contract pin for FR-012 / SC-007:
/// EVERY role-write endpoint (legacy + new) MUST reject an unknown role name
/// with HTTP 400, error code <c>INVALID_ROLE</c>, and a suggestion string that
/// lists all 7 role names verbatim.
///
/// <para>
/// Concretely: submitting <c>"misionowner"</c> (typo) to every write endpoint
/// MUST return a suggestion containing each of:
/// <c>AuthorizingOfficial, Issm, Isso, Sca, SystemOwner, MissionOwner, Administrator</c>.
/// </para>
///
/// <para>
/// Drives FR-012, SC-007: the user must be able to recover from a typo in a
/// single screen-read without consulting documentation.
/// </para>
/// </summary>
public class InvalidRoleSuggestionTests
{
    private const string TypoRole = "misionowner";

    private static readonly string[] AllSevenRoles = new[]
    {
        "AuthorizingOfficial",
        "Issm",
        "Isso",
        "Sca",
        "SystemOwner",
        "MissionOwner",
        "Administrator",
    };

    private sealed class StaticFactory : IDbContextFactory<AtoCopilotContext>
    {
        private readonly string _name;
        public StaticFactory(string name) => _name = name;
        public AtoCopilotContext CreateDbContext() => new(
            new DbContextOptionsBuilder<AtoCopilotContext>().UseInMemoryDatabase(_name).Options);
    }

    private sealed record World(
        Guid TenantId,
        string SystemId,
        Guid IssmPersonId,
        Guid OtherPersonId,
        IDbContextFactory<AtoCopilotContext> Factory,
        IUnifiedRoleReader Reader,
        ICallerEffectiveRoleResolver Resolver,
        IRoleAuthorizationService Authz,
        ISoDConflictDetector Sod,
        IOrganizationRoleAssignmentService OrgService,
        RoleMetrics Metrics);

    private static async Task<World> SeedAsync(string testName)
    {
        var dbName = $"invalid-role-sug-{testName}-{Guid.NewGuid():N}";
        var factory = new StaticFactory(dbName);
        var tenantId = Guid.NewGuid();
        var systemId = Guid.NewGuid().ToString();
        var issmId = Guid.NewGuid();
        var otherId = Guid.NewGuid();

        await using var db = factory.CreateDbContext();
        db.Persons.AddRange(
            new Person { Id = issmId, TenantId = tenantId, DisplayName = "Iris ISSM", Email = "i@x.mil" },
            new Person { Id = otherId, TenantId = tenantId, DisplayName = "Other", Email = "o@x.mil" });
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
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Role = OrganizationRole.Issm,
            PersonId = issmId,
            IsPrimary = true,
        });
        await db.SaveChangesAsync();

        var reader = new UnifiedRoleReader(factory);
        var resolver = new CallerEffectiveRoleResolver(factory);
        var authz = new RoleAuthorizationService();
        var sod = new SoDConflictDetector(factory);
        var queue = new OrganizationRoleFanoutQueue();
        var metrics = new RoleMetrics();
        var audit = new Mock<IWizardAuditService>();
        var orgService = new OrganizationRoleAssignmentService(
            factory, audit.Object,
            NullLogger<OrganizationRoleAssignmentService>.Instance,
            sod, queue, metrics);

        return new World(tenantId, systemId, issmId, otherId,
            factory, reader, resolver, authz, sod, orgService, metrics);
    }

    private static int StatusOf(IResult r)
        => r is IStatusCodeHttpResult sc && sc.StatusCode is int code ? code : 200;

    private static JsonElement BodyOf(IResult r)
    {
        var prop = r.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        var json = JsonSerializer.Serialize(prop?.GetValue(r));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static void AssertEnvelopeListsAllSevenRoles(IResult result)
    {
        StatusOf(result).Should().Be(400);
        var body = BodyOf(result);
        body.GetProperty("status").GetString().Should().Be("error");
        var err = body.GetProperty("error");
        err.GetProperty("code").GetString().Should().Be("INVALID_ROLE");
        var suggestion = err.GetProperty("suggestion").GetString() ?? string.Empty;
        foreach (var roleName in AllSevenRoles)
        {
            suggestion.Should().Contain(roleName,
                $"FR-012/SC-007: invalid-role suggestion MUST list all 7 role names verbatim (missing '{roleName}')");
        }
    }

    [Fact]
    public async Task POST_system_role_with_typo_lists_all_seven_role_names_in_suggestion()
    {
        // Arrange
        var w = await SeedAsync(nameof(POST_system_role_with_typo_lists_all_seven_role_names_in_suggestion));
        var body = new AssignSystemRoleBody(TypoRole, w.OtherPersonId);

        // Act
        var result = await SystemRolesEndpoints.AssignSystemRoleAsync(
            w.SystemId, body, w.TenantId, w.IssmPersonId,
            w.Factory, w.Resolver, w.Authz, w.Sod, w.Metrics, CancellationToken.None);

        // Assert
        AssertEnvelopeListsAllSevenRoles(result);
    }

    [Fact]
    public async Task DELETE_system_role_with_typo_lists_all_seven_role_names_in_suggestion()
    {
        // Arrange
        var w = await SeedAsync(nameof(DELETE_system_role_with_typo_lists_all_seven_role_names_in_suggestion));

        // Act
        var result = await SystemRolesEndpoints.RemoveSystemRoleAsync(
            w.SystemId, TypoRole, w.OtherPersonId, w.TenantId, w.IssmPersonId,
            w.Factory, w.Resolver, w.Authz, w.Metrics, CancellationToken.None);

        // Assert
        AssertEnvelopeListsAllSevenRoles(result);
    }

    [Fact]
    public async Task POST_org_role_with_typo_lists_all_seven_role_names_in_suggestion()
    {
        // Arrange
        var w = await SeedAsync(nameof(POST_org_role_with_typo_lists_all_seven_role_names_in_suggestion));
        var body = new AssignOrgRoleBody(TypoRole, w.OtherPersonId, IsPrimary: true, Bootstrap: false);

        // Act
        var result = await SystemRolesEndpoints.AssignOrgRoleAsync(
            body, w.TenantId, w.IssmPersonId,
            w.Factory, w.Resolver, w.Authz, w.Sod, w.OrgService, CancellationToken.None);

        // Assert
        AssertEnvelopeListsAllSevenRoles(result);
    }

    [Fact]
    public async Task DELETE_org_role_with_typo_lists_all_seven_role_names_in_suggestion()
    {
        // Arrange
        var w = await SeedAsync(nameof(DELETE_org_role_with_typo_lists_all_seven_role_names_in_suggestion));

        // Act
        var result = await SystemRolesEndpoints.RemoveOrgRoleAsync(
            TypoRole, w.OtherPersonId, w.TenantId, w.IssmPersonId,
            w.Factory, w.Resolver, w.Authz, w.OrgService, CancellationToken.None);

        // Assert
        AssertEnvelopeListsAllSevenRoles(result);
    }
}
