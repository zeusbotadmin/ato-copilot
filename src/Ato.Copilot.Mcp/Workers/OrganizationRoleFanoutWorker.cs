using Ato.Copilot.Core.Data.Context;
using Ato.Copilot.Core.Models.Compliance;
using Ato.Copilot.Core.Models.Onboarding;
using Ato.Copilot.Core.Observability;
using Ato.Copilot.Core.Services.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ato.Copilot.Mcp.Workers;

/// <summary>
/// FR-028 background consumer of <see cref="IOrganizationRoleFanoutQueue"/>. For each
/// dequeued <see cref="PropagationIntent"/>, idempotently materializes inherited
/// <see cref="SystemRoleAssignment"/> rows (one per active
/// <see cref="RegisteredSystem"/> in the tenant) so a single Org-level
/// assignment fans out to per-system rows that the dashboard can read with the
/// same precedence chain as overrides.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle: <see cref="ExecuteAsync"/> runs a one-time startup-reconciliation
/// sweep (covers the case where the process crashed between Org-row insert and
/// worker drain) and then loops over the channel reader for the lifetime of the
/// host.
/// </para>
/// <para>
/// Idempotency key: <c>(TenantId, RegisteredSystemId, Role,
/// SourceOrganizationRoleAssignmentId)</c>. Re-enqueuing the same intent never
/// produces duplicate rows.
/// </para>
/// </remarks>
public sealed class OrganizationRoleFanoutWorker : BackgroundService
{
    private const int SystemBatchSize = 100;

    private readonly IOrganizationRoleFanoutQueue _queue;
    private readonly IDbContextFactory<AtoCopilotContext> _contextFactory;
    private readonly RoleMetrics _metrics;
    private readonly ILogger<OrganizationRoleFanoutWorker> _logger;

    public OrganizationRoleFanoutWorker(
        IOrganizationRoleFanoutQueue queue,
        IDbContextFactory<AtoCopilotContext> contextFactory,
        RoleMetrics metrics,
        ILogger<OrganizationRoleFanoutWorker> logger)
    {
        _queue = queue;
        _contextFactory = contextFactory;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileOnStartupAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            await foreach (var intent in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await PropagateAsync(intent, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }

    /// <summary>
    /// Drains the channel until <see cref="IOrganizationRoleFanoutQueue.Complete"/>
    /// has been called and all already-enqueued intents have been processed. Used
    /// by integration tests; not used by the hosted runtime path.
    /// </summary>
    public async Task RunUntilDoneAsync(CancellationToken ct)
    {
        await foreach (var intent in _queue.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await PropagateAsync(intent, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Idempotent sweep across every active <see cref="OrganizationRoleAssignment"/>
    /// in every tenant. Synthesizes intents for any (Org-row, system) pair missing
    /// an inherited <see cref="SystemRoleAssignment"/> row, then processes them
    /// inline (does NOT enqueue — avoids the deadlock the channel's single-reader
    /// model would otherwise create).
    /// </summary>
    public async Task ReconcileOnStartupAsync(CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var activeOrgRows = await db.OrganizationRoleAssignments
            .AsNoTracking()
            .Where(r => r.RemovedAt == null)
            .Select(r => new { r.Id, r.TenantId, r.Role, r.PersonId })
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var row in activeOrgRows)
        {
            var targetRmf = OrganizationRoleToRmfRoleMap.TryMap(row.Role);
            if (targetRmf is null)
            {
                // Administrator — no RmfRole image, no per-system inheritance.
                continue;
            }

            await PropagateAsync(
                new PropagationIntent(
                    row.TenantId,
                    row.Id,
                    targetRmf.Value,
                    row.PersonId,
                    DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Idempotently materializes inherited <see cref="SystemRoleAssignment"/> rows
    /// for every active <see cref="RegisteredSystem"/> in the intent's tenant.
    /// </summary>
    private async Task PropagateAsync(PropagationIntent intent, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var orgRoleEquivalent = OrganizationRoleToRmfRoleMap.TryMap(intent.TargetRole);
        if (orgRoleEquivalent is null)
        {
            // Defensive — RmfRole → OrganizationRole map is total for the six
            // values FR-020 freezes, so this branch is unreachable. Bail rather
            // than insert garbage.
            _logger.LogWarning(
                "FanoutWorker received intent with unmappable RmfRole {TargetRole}; skipping",
                intent.TargetRole);
            return;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Load active system IDs for the tenant (paged batches to bound memory).
        var systemIds = await db.RegisteredSystems
            .AsNoTracking()
            .Where(s => s.TenantId == intent.TenantId && s.IsActive)
            .Select(s => s.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var processed = 0;
        var skipped = 0;

        for (var offset = 0; offset < systemIds.Count; offset += SystemBatchSize)
        {
            var batch = systemIds.Skip(offset).Take(SystemBatchSize).ToList();

            // Existing rows that block insertion: either the same inherited row
            // already exists, or a non-inherited override for (system, role).
            var existing = await db.SystemRoleAssignments
                .Where(s => s.TenantId == intent.TenantId
                         && batch.Contains(s.RegisteredSystemId)
                         && s.Role == orgRoleEquivalent.Value
                         && s.RemovedAt == null)
                .Select(s => new { s.RegisteredSystemId, s.IsInherited, s.SourceOrganizationRoleAssignmentId })
                .ToListAsync(ct).ConfigureAwait(false);

            var existingByKey = existing
                .GroupBy(e => e.RegisteredSystemId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var systemId in batch)
            {
                if (existingByKey.TryGetValue(systemId, out var rows))
                {
                    var alreadyHandled = rows.Any(r =>
                        !r.IsInherited ||
                        r.SourceOrganizationRoleAssignmentId == intent.OrganizationRoleAssignmentId);
                    if (alreadyHandled)
                    {
                        skipped++;
                        continue;
                    }
                }

                db.SystemRoleAssignments.Add(new SystemRoleAssignment
                {
                    Id = Guid.NewGuid(),
                    TenantId = intent.TenantId,
                    RegisteredSystemId = systemId,
                    Role = orgRoleEquivalent.Value,
                    PersonId = intent.PersonId,
                    IsInherited = true,
                    SourceOrganizationRoleAssignmentId = intent.OrganizationRoleAssignmentId,
                });
                processed++;
            }

            if (db.ChangeTracker.HasChanges())
            {
                // Per-batch commit so a mid-flight crash leaves earlier batches durable.
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        sw.Stop();

        _logger.LogInformation(
            "OrganizationRoleFanout completed TenantId={TenantId} OrganizationRoleAssignmentId={OrgRoleId} TargetRole={TargetRole} SystemsProcessed={Processed} SystemsSkipped={Skipped} DurationMs={DurationMs}",
            intent.TenantId,
            intent.OrganizationRoleAssignmentId,
            intent.TargetRole,
            processed,
            skipped,
            sw.ElapsedMilliseconds);

        _metrics.RecordPropagation(intent.TenantId, intent.TargetRole, processed, sw.Elapsed);
    }
}
