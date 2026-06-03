using System.Diagnostics.Metrics;
using Ato.Copilot.Core.Models.Compliance;

namespace Ato.Copilot.Core.Observability;

/// <summary>
/// Singleton telemetry container for the Feature 049 role-assignment subsystem.
///
/// <para>
/// Exposes one <see cref="Meter"/> (shared meter name <c>"Ato.Copilot"</c> — matches
/// <see cref="HttpMetrics"/> and <see cref="ToolMetrics"/>) with four instruments per
/// <c>specs/049-unified-rmf-role-assignments/contracts/internal-services.md § 5</c>:
/// <list type="bullet">
///   <item><c>legacy_role_endpoint_call_total</c> (counter) — every call to the deprecated
///         <c>/api/dashboard/systems/{systemId}/roles</c> endpoint (FR-024 deprecation telemetry).</item>
///   <item><c>legacy_role_endpoint_bypass_total</c> (counter) — calls to the legacy endpoint that
///         would have been denied by <see cref="Services.Roles.IRoleAuthorizationService"/> if the
///         caller had been routed through the unified write path (security-bypass signal for SOC dashboards).</item>
///   <item><c>sod_violation_warning_total</c> (counter) — every DoDI 8510.01 SoD warning the
///         detector surfaces (FR-026 signal — high counts indicate a tenant should be reviewed).</item>
///   <item><c>org_role_propagation_duration_seconds</c> (histogram) — per-event duration of the
///         FR-028 fan-out worker, bucketed by <c>systems_bucket</c> tag to keep label cardinality bounded.</item>
/// </list>
/// </para>
///
/// <para>
/// Registered as a <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton"/>
/// in <c>AtoCopilotMcpServiceExtensions</c>. The owning <see cref="Meter"/> is disposed alongside
/// the host process.
/// </para>
/// </summary>
public sealed class RoleMetrics : IDisposable
{
    /// <summary>Shared meter name across the whole Ato.Copilot process.</summary>
    public const string MeterName = "Ato.Copilot";

    private readonly Meter _meter;
    private readonly Counter<long> _legacyEndpointCalls;
    private readonly Counter<long> _legacyEndpointBypass;
    private readonly Counter<long> _sodWarnings;
    private readonly Histogram<double> _propagationDuration;

    private bool _disposed;

    public RoleMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        _legacyEndpointCalls = _meter.CreateCounter<long>(
            name: "legacy_role_endpoint_call_total",
            unit: null,
            description: "Calls to deprecated /api/dashboard/systems/{systemId}/roles");

        _legacyEndpointBypass = _meter.CreateCounter<long>(
            name: "legacy_role_endpoint_bypass_total",
            unit: null,
            description: "Legacy endpoint writes that would have been denied under FR-027");

        _sodWarnings = _meter.CreateCounter<long>(
            name: "sod_violation_warning_total",
            unit: null,
            description: "DoDI 8510.01 SoD warnings surfaced by FR-026");

        _propagationDuration = _meter.CreateHistogram<double>(
            name: "org_role_propagation_duration_seconds",
            unit: "s",
            description: "FR-028 worker propagation duration per Org-role-add event");
    }

    /// <summary>
    /// Record one call to the deprecated <c>/api/dashboard/systems/{systemId}/roles</c> endpoint.
    /// Tag cardinality: <c>tenant_id</c> ≤ active tenants, <c>method</c> ∈ {GET, POST, DELETE}.
    /// </summary>
    public void RecordLegacyCall(Guid tenantId, string method)
    {
        _legacyEndpointCalls.Add(
            1,
            new KeyValuePair<string, object?>("tenant_id", tenantId),
            new KeyValuePair<string, object?>("method", method));
    }

    /// <summary>
    /// Record a legacy-endpoint write that bypassed FR-027 RBAC — i.e. the same call
    /// would have been denied by <see cref="Services.Roles.IRoleAuthorizationService.Authorize"/>
    /// had it been routed through the unified write path.
    /// </summary>
    public void RecordLegacyBypass(Guid tenantId, RmfRole targetRole)
    {
        _legacyEndpointBypass.Add(
            1,
            new KeyValuePair<string, object?>("tenant_id", tenantId),
            new KeyValuePair<string, object?>("target_role", targetRole.ToString()));
    }

    /// <summary>
    /// Record one DoDI 8510.01 SoD warning surfaced by <see cref="Services.Roles.ISoDConflictDetector"/>.
    /// </summary>
    public void RecordSodWarning(Guid tenantId, RmfRole callerRole, RmfRole conflictingRole)
    {
        _sodWarnings.Add(
            1,
            new KeyValuePair<string, object?>("tenant_id", tenantId),
            new KeyValuePair<string, object?>("caller_role", callerRole.ToString()),
            new KeyValuePair<string, object?>("conflicting_role", conflictingRole.ToString()));
    }

    /// <summary>
    /// Record one fan-out worker iteration duration. The <c>systemsProcessed</c> count is bucketed
    /// to bound histogram label cardinality (4 buckets: <c>1-10</c>, <c>11-100</c>, <c>101-500</c>, <c>500+</c>).
    /// </summary>
    public void RecordPropagation(Guid tenantId, RmfRole targetRole, int systemsProcessed, TimeSpan duration)
    {
        var bucket = systemsProcessed switch
        {
            <= 10 => "1-10",
            <= 100 => "11-100",
            <= 500 => "101-500",
            _ => "500+",
        };

        _propagationDuration.Record(
            duration.TotalSeconds,
            new KeyValuePair<string, object?>("tenant_id", tenantId),
            new KeyValuePair<string, object?>("target_role", targetRole.ToString()),
            new KeyValuePair<string, object?>("systems_bucket", bucket));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _meter.Dispose();
        _disposed = true;
    }
}
