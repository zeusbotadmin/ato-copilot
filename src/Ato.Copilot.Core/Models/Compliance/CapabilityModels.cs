namespace Ato.Copilot.Core.Models.Compliance;

// ═══════════════════════════════════════════════════════════════════════════════
// CSP Capability Models (Epic #124 — Feature 050, Issue #160)
// Minimal CSP capability model with NeedsReview gate support.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Lifecycle status for a CSP capability.
/// </summary>
public enum CapabilityStatus
{
    /// <summary>Capability is in draft state — not yet reviewed.</summary>
    Draft,

    /// <summary>Capability was manually created and requires review before use.</summary>
    NeedsReview,

    /// <summary>Capability is active and available for use.</summary>
    Active,

    /// <summary>Capability is deprecated and no longer in active use.</summary>
    Deprecated
}

/// <summary>
/// Represents a CSP (Cloud Service Provider) capability that can be tracked,
/// reviewed, and mapped to parent capabilities in a hierarchy.
/// Manual creates always land in <see cref="CapabilityStatus.NeedsReview"/> (#160).
/// </summary>
public class CspCapability
{
    /// <summary>Unique identifier (GUID string, matching repo ID pattern).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Display name of the capability.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description of what this capability provides.</summary>
    public string? Description { get; set; }

    /// <summary>FK → parent CspCapability for hierarchical mapping (#161). Null means root.</summary>
    public string? ParentCapabilityId { get; set; }

    /// <summary>Current lifecycle status of the capability.</summary>
    public CapabilityStatus Status { get; set; } = CapabilityStatus.Draft;

    /// <summary>
    /// True when the capability has been flagged for review (always set on manual create).
    /// Cleared by <c>ClearReviewAsync</c>.
    /// </summary>
    public bool NeedsReview { get; set; } = false;

    /// <summary>ID of the user who created this capability.</summary>
    public string CreatedBy { get; set; } = "system";

    /// <summary>UTC timestamp when the capability was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>UTC timestamp of the most recent update.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
