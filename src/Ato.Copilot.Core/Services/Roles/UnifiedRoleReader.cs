using Ato.Copilot.Core.Data.Context;
using Ato.Copilot.Core.Models.Compliance;
using Ato.Copilot.Core.Models.Onboarding;
using Microsoft.EntityFrameworkCore;

namespace Ato.Copilot.Core.Services.Roles;

/// <summary>
/// Resolves the read-time precedence chain for per-system role assignments per
/// <c>specs/049-unified-rmf-role-assignments/data-model.md § Read-time precedence</c>:
/// override → inherited → org-fallback → legacy → not-assigned.
/// </summary>
/// <remarks>
/// <para>
/// Storage divergence note (FR-029): the three source tables persist <c>Role</c> with
/// different shapes —
/// <c>OrganizationRoleAssignment.Role</c> is <c>nvarchar(32)</c> (string), while
/// <c>SystemRoleAssignment.Role</c> and <c>RmfRoleAssignment.Role</c> are <c>int</c>.
/// Mixing those into a single SQL JOIN with CAST is fragile across providers; this
/// reader instead issues three lightweight per-table queries in parallel and zips
/// the results in memory. The combined wire cost is ≤ a few hundred rows per
/// snapshot (bounded by RmfRole's 6 values), so the round-trip cost dominates and
/// parallelism keeps the latency budget intact.
/// </para>
/// </remarks>
public sealed class UnifiedRoleReader : IUnifiedRoleReader
{
    private static readonly RmfRole[] AllRoles = new[]
    {
        RmfRole.MissionOwner,
        RmfRole.SystemOwner,
        RmfRole.AuthorizingOfficial,
        RmfRole.Sca,
        RmfRole.Issm,
        RmfRole.Isso,
    };

    private readonly IDbContextFactory<AtoCopilotContext> _contextFactory;

    public UnifiedRoleReader(IDbContextFactory<AtoCopilotContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task<SystemRoleSnapshot> GetSystemRolesAsync(
        Guid tenantId,
        string registeredSystemId,
        CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        // FR-004 / SC-006 tenant isolation: if the system does not exist in this
        // tenant, the snapshot is six NotAssigned rows — Org-fallback rows are
        // tenant-wide and would otherwise leak across the (tenant, systemId) boundary
        // when the systemId belongs to a different tenant.
        var systemExists = await db.RegisteredSystems
            .AsNoTracking()
            .AnyAsync(s => s.TenantId == tenantId && s.Id == registeredSystemId, ct);

        if (!systemExists)
        {
            var notAssigned = AllRoles
                .Select(r => new ResolvedRoleAssignment(
                    r,
                    PersonId: null,
                    PersonDisplayName: null,
                    Source: RoleAssignmentSource.NotAssigned,
                    OrgRoleId: null))
                .ToList();
            return new SystemRoleSnapshot(tenantId, registeredSystemId, notAssigned);
        }

        // ── Layer 1: SystemRoleAssignment (override + inherited) ─────────────
        var systemRows = await db.SystemRoleAssignments
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId
                     && s.RegisteredSystemId == registeredSystemId
                     && s.RemovedAt == null)
            .Select(s => new SystemRoleRow(
                s.Role,
                s.PersonId,
                s.Person != null ? s.Person.DisplayName : null,
                s.IsInherited,
                s.SourceOrganizationRoleAssignmentId,
                s.CreatedAt))
            .ToListAsync(ct);

        // ── Layer 3 (precedence): OrganizationRoleAssignment (org-fallback) ──
        var orgRows = await db.OrganizationRoleAssignments
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.RemovedAt == null)
            .Select(r => new OrgRoleRow(
                r.Id,
                r.Role,
                r.PersonId,
                r.Person != null ? r.Person.DisplayName : null,
                r.IsPrimary,
                r.CreatedAt))
            .ToListAsync(ct);

        // ── Layer 4 (precedence): legacy RmfRoleAssignment (FR-024 read-side) ──
        var legacyRows = await db.RmfRoleAssignments
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId
                     && r.RegisteredSystemId == registeredSystemId
                     && r.IsActive)
            .Select(r => new LegacyRoleRow(
                r.RmfRole,
                r.UserDisplayName))
            .ToListAsync(ct);

        var resolved = new List<ResolvedRoleAssignment>(AllRoles.Length);
        foreach (var role in AllRoles)
        {
            resolved.Add(Resolve(role, systemRows, orgRows, legacyRows));
        }

        return new SystemRoleSnapshot(tenantId, registeredSystemId, resolved);
    }

    /// <inheritdoc />
    public async Task<ResolvedRoleAssignment?> GetMissionOwnerAsync(
        Guid tenantId,
        string registeredSystemId,
        CancellationToken ct)
    {
        var snapshot = await GetSystemRolesAsync(tenantId, registeredSystemId, ct);
        var mo = snapshot.Roles.FirstOrDefault(r => r.Role == RmfRole.MissionOwner);
        return mo.Source == RoleAssignmentSource.NotAssigned ? null : mo;
    }

    /// <summary>
    /// Walks the 5-step precedence chain for a single role.
    /// </summary>
    private static ResolvedRoleAssignment Resolve(
        RmfRole role,
        IReadOnlyList<SystemRoleRow> systemRows,
        IReadOnlyList<OrgRoleRow> orgRows,
        IReadOnlyList<LegacyRoleRow> legacyRows)
    {
        var orgEquivalent = OrganizationRoleToRmfRoleMap.TryMap(role);

        // (1) Override — SystemRoleAssignment with IsInherited=false.
        if (orgEquivalent is not null)
        {
            var overrideRow = systemRows
                .Where(s => s.Role == orgEquivalent.Value && !s.IsInherited)
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefault();
            if (overrideRow is not null)
            {
                return new ResolvedRoleAssignment(
                    role,
                    overrideRow.PersonId,
                    overrideRow.PersonDisplayName,
                    RoleAssignmentSource.Override,
                    OrgRoleId: null);
            }

            // (2) Inherited — SystemRoleAssignment with IsInherited=true.
            var inheritedRow = systemRows
                .Where(s => s.Role == orgEquivalent.Value && s.IsInherited)
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefault();
            if (inheritedRow is not null)
            {
                return new ResolvedRoleAssignment(
                    role,
                    inheritedRow.PersonId,
                    inheritedRow.PersonDisplayName,
                    RoleAssignmentSource.Inherited,
                    inheritedRow.SourceOrgRoleId);
            }

            // (3) Org-fallback — Organization row exists but no inherited yet.
            //     Tie-break: IsPrimary=true wins; else most-recent CreatedAt wins.
            var orgRow = orgRows
                .Where(r => r.Role == orgEquivalent.Value)
                .OrderByDescending(r => r.IsPrimary)
                .ThenByDescending(r => r.CreatedAt)
                .FirstOrDefault();
            if (orgRow is not null)
            {
                return new ResolvedRoleAssignment(
                    role,
                    orgRow.PersonId,
                    orgRow.PersonDisplayName,
                    RoleAssignmentSource.OrgFallback,
                    orgRow.OrgRoleId);
            }
        }

        // (4) Legacy — RmfRoleAssignment (read-only fallback for FR-024).
        var legacyRow = legacyRows.FirstOrDefault(r => r.Role == role);
        if (legacyRow is not null)
        {
            return new ResolvedRoleAssignment(
                role,
                PersonId: null,                       // legacy rows store string UserId (Entra OID/email)
                PersonDisplayName: legacyRow.PersonDisplayName,
                Source: RoleAssignmentSource.Legacy,
                OrgRoleId: null);
        }

        // (5) Not assigned.
        return new ResolvedRoleAssignment(
            role,
            PersonId: null,
            PersonDisplayName: null,
            Source: RoleAssignmentSource.NotAssigned,
            OrgRoleId: null);
    }

    private sealed record SystemRoleRow(
        OrganizationRole Role,
        Guid PersonId,
        string? PersonDisplayName,
        bool IsInherited,
        Guid? SourceOrgRoleId,
        DateTimeOffset CreatedAt);

    private sealed record OrgRoleRow(
        Guid OrgRoleId,
        OrganizationRole Role,
        Guid PersonId,
        string? PersonDisplayName,
        bool IsPrimary,
        DateTimeOffset CreatedAt);

    private sealed record LegacyRoleRow(
        RmfRole Role,
        string? PersonDisplayName);
}
