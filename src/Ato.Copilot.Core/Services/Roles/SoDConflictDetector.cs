using Ato.Copilot.Core.Data.Context;
using Ato.Copilot.Core.Models.Compliance;
using Ato.Copilot.Core.Models.Onboarding;
using Microsoft.EntityFrameworkCore;

namespace Ato.Copilot.Core.Services.Roles;

/// <summary>
/// FR-026 DoDI 8510.01 Enclosure 3 separation-of-duties detection. Tenant-scoped,
/// read-only. Cross-products the person's currently-held Org roles against a
/// closed table of conflict pairs and emits one <see cref="SoDWarning"/> per match.
/// </summary>
public sealed class SoDConflictDetector : ISoDConflictDetector
{
    private const string DodiReference = "DoDI 8510.01 Enclosure 3 § 4.b";

    /// <summary>
    /// Conflict pairs (target → existing OrgRoles that conflict). The table is
    /// closed; non-listed pairs MUST NOT emit warnings.
    /// </summary>
    private static readonly Dictionary<RmfRole, HashSet<OrganizationRole>> Conflicts =
        new()
        {
            [RmfRole.AuthorizingOfficial] = new HashSet<OrganizationRole>
            {
                OrganizationRole.SystemOwner,
                OrganizationRole.Issm,
                OrganizationRole.Isso,
            },
            [RmfRole.Sca] = new HashSet<OrganizationRole>
            {
                OrganizationRole.Issm,
                OrganizationRole.Isso,
                OrganizationRole.SystemOwner,
            },
        };

    private readonly IDbContextFactory<AtoCopilotContext> _contextFactory;

    public SoDConflictDetector(IDbContextFactory<AtoCopilotContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SoDWarning>> DetectAsync(
        Guid tenantId,
        Guid personId,
        RmfRole targetRole,
        CancellationToken ct)
    {
        if (!Conflicts.TryGetValue(targetRole, out var conflictSet))
        {
            return Array.Empty<SoDWarning>();
        }

        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        // Tenant-scoped read of the Org rows this person currently holds.
        var existingRoles = await db.OrganizationRoleAssignments
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId
                     && r.PersonId == personId
                     && r.RemovedAt == null)
            .Select(r => r.Role)
            .ToListAsync(ct);

        var warnings = new List<SoDWarning>();
        foreach (var existing in existingRoles)
        {
            if (!conflictSet.Contains(existing))
            {
                continue;
            }

            // Map the Org-row that conflicts to its RmfRole image (or skip if
            // Administrator — currently not in any conflict pair, but defensive).
            var existingRmf = OrganizationRoleToRmfRoleMap.TryMap(existing);
            if (existingRmf is null)
            {
                continue;
            }

            warnings.Add(new SoDWarning(
                Code: "SOD_VIOLATION",
                Message: $"Person {personId} already holds {existing}; assigning {targetRole} would violate DoDI 8510.01 separation of duties.",
                RoleConflict: (existingRmf.Value, targetRole),
                DodiReference: DodiReference,
                SuggestedAction: $"Remove the existing {existing} assignment, or assign {targetRole} to a different person."));
        }

        return warnings;
    }
}
