namespace Ato.Copilot.Core.Services.Roles;

/// <summary>
/// FR-026 DoDI 8510.01 Enclosure 3 separation-of-duties detection.
/// </summary>
/// <remarks>
/// <para>Conflict pairs (closed):</para>
/// <list type="bullet">
///   <item><c>AuthorizingOfficial</c> conflicts with <c>SystemOwner</c>, <c>Issm</c>, <c>Isso</c>.</item>
///   <item><c>Sca</c> conflicts with <c>Issm</c>, <c>Isso</c>, <c>SystemOwner</c>.</item>
/// </list>
/// <para>
/// Read-only; never modifies state. Surfacing a warning never blocks the
/// underlying write — the endpoint layer decides whether to require
/// acknowledgement.
/// </para>
/// </remarks>
public interface ISoDConflictDetector
{
    /// <summary>
    /// Inspect the rows the given <paramref name="personId"/> currently holds in
    /// <c>OrganizationRoleAssignments</c> (tenant-scoped, soft-removed rows
    /// excluded) and return one <see cref="SoDWarning"/> per conflict against
    /// <paramref name="targetRole"/>.
    /// </summary>
    Task<IReadOnlyList<SoDWarning>> DetectAsync(
        Guid tenantId,
        Guid personId,
        Models.Compliance.RmfRole targetRole,
        CancellationToken ct);
}
