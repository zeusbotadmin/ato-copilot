using Ato.Copilot.Core.Data.Context;
using Ato.Copilot.Core.Interfaces.Compliance;
using Ato.Copilot.Core.Models.Compliance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ato.Copilot.Agents.Compliance.Services;

/// <summary>
/// Implements <see cref="ICspCapabilityService"/> providing CSP capability CRUD
/// with NeedsReview gate enforcement on manual creates (#160) and parent remapping (#161).
/// </summary>
public class CspCapabilityService : ICspCapabilityService
{
    private readonly IDbContextFactory<AtoCopilotContext> _dbFactory;
    private readonly ICapabilityHistoryService _history;
    private readonly ILogger<CspCapabilityService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CspCapabilityService"/>.
    /// </summary>
    public CspCapabilityService(
        IDbContextFactory<AtoCopilotContext> dbFactory,
        ICapabilityHistoryService history,
        ILogger<CspCapabilityService> logger)
    {
        _dbFactory = dbFactory;
        _history = history;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always sets <c>NeedsReview = true</c> and <c>Status = NeedsReview</c>
    /// per the #160 NeedsReview gate requirement for manual creates.
    /// </remarks>
    public async Task<CspCapability> CreateCapabilityAsync(
        string name,
        string? description,
        string? parentCapabilityId,
        string createdBy,
        CancellationToken cancellationToken = default)
    {
        var capability = new CspCapability
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Description = description,
            ParentCapabilityId = parentCapabilityId,
            Status = CapabilityStatus.NeedsReview,   // #160 gate: always NeedsReview on manual create
            NeedsReview = true,                       // #160 gate flag
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.CspCapabilities.Add(capability);
        await db.SaveChangesAsync(cancellationToken);

        // Record NeedsReviewFlagged history event
        await _history.RecordEventAsync(
            capabilityId: capability.Id,
            eventType: CapabilityHistoryEventType.NeedsReviewFlagged,
            newValue: $"{{\"name\":\"{name}\",\"status\":\"NeedsReview\"}}",
            notes: "Capability created manually — flagged for review",
            actorId: createdBy,
            actorName: createdBy,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Created CSP capability {CapabilityId} '{Name}' — flagged NeedsReview (#160 gate)",
            capability.Id, name);

        return capability;
    }

    /// <inheritdoc />
    public async Task<CspCapability?> GetCapabilityAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CspCapabilities.FindAsync(new object[] { id }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CspCapability> ClearReviewAsync(string id, string clearedBy, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var capability = await db.CspCapabilities.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new InvalidOperationException($"Capability '{id}' not found.");

        var previousStatus = capability.Status.ToString();
        capability.NeedsReview = false;
        capability.Status = CapabilityStatus.Active;
        capability.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        // Record ReviewCleared history event
        await _history.RecordEventAsync(
            capabilityId: id,
            eventType: CapabilityHistoryEventType.ReviewCleared,
            previousValue: $"{{\"status\":\"{previousStatus}\",\"needsReview\":true}}",
            newValue: "{\"status\":\"Active\",\"needsReview\":false}",
            notes: "Review cleared — capability is now Active",
            actorId: clearedBy,
            actorName: clearedBy,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Cleared review for CSP capability {CapabilityId} by {ClearedBy}",
            id, clearedBy);

        return capability;
    }

    /// <inheritdoc />
    public async Task<CspCapability> RemapParentAsync(
        string id,
        string? newParentId,
        string remappedBy,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var capability = await db.CspCapabilities.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new InvalidOperationException($"Capability '{id}' not found.");

        var previousParent = capability.ParentCapabilityId;
        capability.ParentCapabilityId = newParentId;
        capability.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        // Record ParentChanged history event
        await _history.RecordEventAsync(
            capabilityId: id,
            eventType: CapabilityHistoryEventType.ParentChanged,
            previousValue: previousParent ?? "(root)",
            newValue: newParentId ?? "(root)",
            notes: newParentId is null
                ? "Remapped to root (no parent)"
                : $"Remapped to parent '{newParentId}'",
            actorId: remappedBy,
            actorName: remappedBy,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Remapped CSP capability {CapabilityId} parent: {Previous} → {New}",
            id, previousParent ?? "(root)", newParentId ?? "(root)");

        return capability;
    }
}
