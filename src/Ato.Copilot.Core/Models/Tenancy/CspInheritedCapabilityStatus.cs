namespace Ato.Copilot.Core.Models.Tenancy;

/// <summary>
/// Lifecycle for a <see cref="CspInheritedCapability"/> created from the AI
/// mapping pipeline (Feature 048 FR-101 / FR-104).
/// </summary>
public enum CspInheritedCapabilityStatus
{
    /// <summary>
    /// AI mapping confidence ≥ <c>Csp:Inheritance:MappingConfidenceThreshold</c>
    /// (default 0.6). Consumable by hosted tenants.
    /// </summary>
    Mapped = 0,

    /// <summary>
    /// AI mapping confidence below threshold OR the AI returned no candidate
    /// controls. NOT consumable by hosted tenants until a CSP-Admin completes
    /// review via <c>PATCH /csp/inherited-components/{id}/capabilities/{capabilityId}/review</c>.
    /// </summary>
    NeedsReview = 1,

    /// <summary>
    /// CSP-Admin soft-deleted the capability via
    /// <c>DELETE /csp/inherited-components/{id}/capabilities/{capabilityId}</c>.
    /// Hidden from non-CSP-Admin callers (parallels the
    /// <see cref="CspInheritedComponentStatus.Archived"/> read filter on
    /// the parent component). Excluded from the
    /// <see cref="CspInheritedComponent.CapabilityMappedCount"/> /
    /// <see cref="CspInheritedComponent.CapabilityNeedsReviewCount"/>
    /// derived counts (those compare on positive equality of
    /// <see cref="Mapped"/> / <see cref="NeedsReview"/>).
    /// </summary>
    Archived = 2,
}
