namespace Ato.Copilot.Core.Models.Compliance;

// ═══════════════════════════════════════════════════════════════════════════════
// CSP Capability History Models (Epic #124 — Feature 050)
// Lifecycle event tracking for CSP capabilities.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Defines the types of lifecycle events that can be recorded for a CSP capability.
/// </summary>
public enum CapabilityHistoryEventType
{
    /// <summary>Capability was newly created.</summary>
    Created,

    /// <summary>Capability metadata was updated.</summary>
    Updated,

    /// <summary>Capability was remapped to a different parent (#161).</summary>
    ParentChanged,

    /// <summary>Capability status transitioned to a new value.</summary>
    StatusChanged,

    /// <summary>Capability was flagged as needing review on manual create (#160).</summary>
    NeedsReviewFlagged,

    /// <summary>Capability review flag was cleared and status set to Active (#160).</summary>
    ReviewCleared,

    /// <summary>Capability was deprecated and removed from active use.</summary>
    Deprecated
}

/// <summary>
/// Immutable history record for a CSP capability lifecycle event.
/// Provides a full audit trail of all changes to capabilities.
/// </summary>
public class CapabilityHistoryEvent
{
    /// <summary>Unique identifier for this history record.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>FK to the CSP capability this event belongs to (string ID pattern).</summary>
    public string CapabilityId { get; set; } = string.Empty;

    /// <summary>Type of lifecycle event that occurred.</summary>
    public CapabilityHistoryEventType EventType { get; set; }

    /// <summary>JSON snapshot of the value before the change (null for Created events).</summary>
    public string? PreviousValue { get; set; }

    /// <summary>JSON snapshot of the new value after the change.</summary>
    public string? NewValue { get; set; }

    /// <summary>Optional human-readable notes about the event.</summary>
    public string? Notes { get; set; }

    /// <summary>ID of the actor who triggered this event.</summary>
    public string ActorId { get; set; } = "system";

    /// <summary>Display name of the actor who triggered this event.</summary>
    public string ActorName { get; set; } = "System";

    /// <summary>UTC timestamp when the event occurred.</summary>
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
