namespace Ato.Copilot.Core.Configuration.Auth;

/// <summary>
/// Feature 051 T120 [US7] — a single simulated-identity entry that can be
/// selected from the dashboard's <c>SimulationPanel</c> via
/// <c>POST /api/auth/simulate?identityId=&lt;IdentityId&gt;</c> in Development.
/// </summary>
/// <remarks>
/// <para>Mirrors <c>specs/051-login/data-model.md § 4</c>. Bound under
/// <c>CacAuth:SimulatedIdentities[]</c> alongside Feature 027's pre-existing
/// single-identity <c>CacAuth:SimulatedIdentity</c> shape (which is preserved
/// for backward compatibility with the <c>CacAuthenticationMiddleware</c> /
/// <c>CacSessionService</c> code paths).</para>
/// <para>The three-layer simulation gate (<c>/login-config</c> omits the
/// descriptor, the SPA route guard refuses to mount the panel, and
/// <c>POST /api/auth/simulate</c> returns a bare 404 in non-Development)
/// is the most important security invariant of US7 — see
/// <c>specs/051-login/research.md</c> summary item #4.</para>
/// </remarks>
public sealed class SimulatedIdentityDescriptor
{
    /// <summary>
    /// Unique key used by <c>POST /api/auth/simulate?identityId=&lt;key&gt;</c>
    /// to select this identity. Required, non-empty, case-sensitive.
    /// </summary>
    public string IdentityId { get; init; } = string.Empty;

    /// <summary>Display name rendered on the SimulationPanel button. Required.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Simulated Entra <c>oid</c> claim. Required so the issued cookie has a
    /// stable subject identifier the rest of the platform can correlate on.
    /// </summary>
    public string Oid { get; init; } = string.Empty;

    /// <summary>Simulated Entra <c>tid</c> claim (string form of the home tenant id).</summary>
    public string Tid { get; init; } = string.Empty;

    /// <summary>
    /// Simulated home tenant id used to scope tenant-aware reads. Typically the
    /// GUID parse of <see cref="Tid"/>, but kept distinct so federated/multi-
    /// tenant simulation scenarios can override it.
    /// </summary>
    public Guid TenantId { get; init; }

    /// <summary>Role claims to attach to the synthesised principal. Empty list = least privilege.</summary>
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Persona label shown in the SimulationPanel and recorded on the simulated
    /// session. Free-form (e.g. <c>"CspAdmin"</c>, <c>"ISSO"</c>, <c>"SocAnalyst"</c>).
    /// </summary>
    public string Persona { get; init; } = string.Empty;

    /// <summary>
    /// Validates that the descriptor has the minimum fields required to
    /// participate in simulation. Returns the descriptor unchanged on success
    /// and throws <see cref="InvalidOperationException"/> with a precise
    /// message otherwise.
    /// </summary>
    public SimulatedIdentityDescriptor EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(IdentityId))
        {
            throw new InvalidOperationException(
                "CacAuth:SimulatedIdentities[].IdentityId is required and must be non-empty.");
        }
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            throw new InvalidOperationException(
                $"CacAuth:SimulatedIdentities['{IdentityId}'].DisplayName is required and must be non-empty.");
        }
        return this;
    }
}
