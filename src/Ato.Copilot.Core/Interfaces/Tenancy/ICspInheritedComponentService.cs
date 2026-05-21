using Ato.Copilot.Core.Models.Tenancy;

namespace Ato.Copilot.Core.Interfaces.Tenancy;

/// <summary>
/// Lifecycle and read surface for <see cref="CspInheritedComponent"/> rows
/// (Feature 048 FR-007 / FR-100 / FR-104 / FR-105). Mutations are gated to
/// <c>CSP.Admin</c> by the endpoint surface.
/// </summary>
public interface ICspInheritedComponentService
{
    /// <summary>Fetch a single component by id.</summary>
    /// <returns>The component (with <see cref="CspInheritedComponent.Capabilities"/> populated) or <c>null</c> if not found.</returns>
    Task<CspInheritedComponent?> GetAsync(Guid componentId, CancellationToken ct = default);

    /// <summary>
    /// List components for a CSP profile, optionally filtered by status.
    /// </summary>
    Task<IReadOnlyList<CspInheritedComponent>> ListAsync(
        Guid cspProfileId,
        CspInheritedComponentStatus? status = null,
        CancellationToken ct = default);

    /// <summary>
    /// Manually create a CSP-inherited component without going through the
    /// ATO-document import pipeline. Used by the CSP-Admin
    /// "Create component" surface. The new row is stamped with
    /// <see cref="SourceFormat.Manual"/> and persisted as
    /// <see cref="CspInheritedComponentStatus.Published"/> so it is
    /// immediately visible to every hosted tenant — there is no extraction
    /// step to defer publishing for.
    /// </summary>
    Task<CspInheritedComponent> CreateAsync(
        Guid cspProfileId,
        string name,
        string description,
        CspComponentType componentType,
        string actor,
        CancellationToken ct = default);

    /// <summary>
    /// Manually add a capability to an existing
    /// <see cref="CspInheritedComponent"/>. The new row is stamped with
    /// <see cref="MappedBy.User"/> and
    /// <see cref="CspInheritedCapabilityStatus.Mapped"/> — no AI confidence
    /// score is recorded. Throws <see cref="KeyNotFoundException"/> if
    /// <paramref name="componentId"/> does not exist.
    /// </summary>
    Task<CspInheritedCapability> AddCapabilityAsync(
        Guid componentId,
        string name,
        string description,
        IReadOnlyList<string> mappedNistControlIds,
        string actor,
        CancellationToken ct = default);

    /// <summary>
    /// Update mutable metadata fields (<c>Name</c>, <c>Description</c>,
    /// <c>ComponentType</c>). Concurrency guarded by <c>RowVersion</c>.
    /// </summary>
    Task<CspInheritedComponent> UpdateAsync(
        Guid componentId,
        string name,
        string description,
        CspComponentType componentType,
        byte[]? rowVersion,
        string actor,
        CancellationToken ct = default);

    /// <summary>
    /// Flip <see cref="CspInheritedComponent.Status"/> to
    /// <see cref="CspInheritedComponentStatus.Published"/>. Idempotent.
    /// </summary>
    Task<CspInheritedComponent> PublishAsync(
        Guid componentId,
        string actor,
        CancellationToken ct = default);

    /// <summary>
    /// Flip <see cref="CspInheritedComponent.Status"/> to
    /// <see cref="CspInheritedComponentStatus.Archived"/>. Idempotent.
    /// </summary>
    Task<CspInheritedComponent> ArchiveAsync(
        Guid componentId,
        string actor,
        CancellationToken ct = default);

    /// <summary>
    /// Re-run the AI capability-mapping pipeline for an existing component
    /// (FR-101 follow-up after FR-102 unavailability or after CSP-Admin
    /// edits). Replaces existing capabilities; preserves any with
    /// <see cref="CspInheritedCapability.MappedBy"/> =
    /// <see cref="MappedBy.User"/> when <paramref name="preserveHumanMappings"/>
    /// is <c>true</c>.
    /// </summary>
    Task<CapabilityMappingResult> RemapAsync(
        Guid componentId,
        bool preserveHumanMappings,
        string actor,
        CancellationToken ct = default);

    /// <summary>
    /// Complete human review for a single
    /// <see cref="CspInheritedCapability"/> — updates the mapped controls,
    /// flips <see cref="CspInheritedCapability.Status"/> to
    /// <see cref="CspInheritedCapabilityStatus.Mapped"/>, and stamps
    /// reviewer metadata. Throws if <paramref name="capabilityId"/> does not
    /// belong to <paramref name="componentId"/>.
    /// </summary>
    Task<CspInheritedCapability> ReviewCapabilityAsync(
        Guid componentId,
        Guid capabilityId,
        IReadOnlyList<string> mappedControlIds,
        string? reviewerNote,
        string actor,
        CancellationToken ct = default);

    /// <summary>
    /// Update mutable metadata fields on a single
    /// <see cref="CspInheritedCapability"/> (Name, Description, mapped NIST
    /// control IDs). Used by the CSP-Admin capability detail drawer for
    /// generic edits outside the NeedsReview-only
    /// <see cref="ReviewCapabilityAsync"/> code-path. The row is stamped
    /// with <see cref="MappedBy.User"/>, reviewer metadata is refreshed,
    /// and the row is moved to
    /// <see cref="CspInheritedCapabilityStatus.Mapped"/> when it was
    /// previously NeedsReview (a manual edit implicitly resolves the
    /// review). Throws <see cref="KeyNotFoundException"/> if the
    /// capability id does not belong to the component id, and
    /// <see cref="DbUpdateConcurrencyException"/> when the supplied
    /// <paramref name="rowVersion"/> stamp is stale.
    /// </summary>
    Task<CspInheritedCapability> UpdateCapabilityAsync(
        Guid componentId,
        Guid capabilityId,
        string name,
        string description,
        IReadOnlyList<string> mappedNistControlIds,
        byte[]? rowVersion,
        string actor,
        CancellationToken ct = default);

    /// <summary>
    /// Soft-delete a single <see cref="CspInheritedCapability"/> by flipping
    /// its <see cref="CspInheritedCapability.Status"/> to
    /// <see cref="CspInheritedCapabilityStatus.Archived"/>. Idempotent —
    /// a row already Archived is returned unchanged. Throws
    /// <see cref="KeyNotFoundException"/> if the capability does not
    /// belong to the supplied <paramref name="componentId"/>.
    /// </summary>
    Task<CspInheritedCapability> ArchiveCapabilityAsync(
        Guid componentId,
        Guid capabilityId,
        string actor,
        CancellationToken ct = default);
}
