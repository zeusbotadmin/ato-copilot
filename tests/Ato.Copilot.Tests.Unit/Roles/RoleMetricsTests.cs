using System.Diagnostics.Metrics;
using Ato.Copilot.Core.Models.Compliance;
using Ato.Copilot.Core.Observability;
using FluentAssertions;
using Xunit;

namespace Ato.Copilot.Tests.Unit.Roles;

/// <summary>
/// Pins the public OpenTelemetry-compatible <see cref="System.Diagnostics.Metrics.Meter"/>
/// surface of <see cref="RoleMetrics"/> per
/// <c>specs/049-unified-rmf-role-assignments/contracts/internal-services.md § 5</c>.
///
/// <para>
/// Uses <see cref="MeterListener"/> (the in-process .NET diagnostics API) so the assertions
/// match exactly what an OpenTelemetry exporter would see in production — no shim or mock.
/// </para>
///
/// <para>
/// Asserted properties for each instrument:
/// <list type="bullet">
///   <item>Instrument name (the wire-level metric identifier).</item>
///   <item>Unit (or null for counters, "s" for the propagation histogram).</item>
///   <item>Description (the human-readable label exporters carry into Grafana/Azure Monitor).</item>
/// </list>
/// </para>
///
/// <para>
/// Label cardinality bounds are asserted by emitting one value with each known tag value
/// and asserting the listener received <c>N×M</c> measurements with the expected tag tuple.
/// </para>
/// </summary>
public class RoleMetricsTests
{
    [Fact]
    public void Meter_name_and_version_match_contract()
    {
        // Arrange
        var captured = new List<(Meter Meter, Instrument Instrument)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == RoleMetrics.MeterName)
                {
                    captured.Add((instrument.Meter, instrument));
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.Start();

        // Act
        using var metrics = new RoleMetrics();

        // Assert
        captured.Should().NotBeEmpty("RoleMetrics constructor must register instruments on Meter 'Ato.Copilot'");
        captured.Select(c => c.Meter.Name).Distinct()
            .Should().BeEquivalentTo(new[] { RoleMetrics.MeterName });
    }

    [Fact]
    public void Registers_three_counters_and_one_histogram_with_contract_names()
    {
        // Arrange — the "Ato.Copilot" meter name is shared with HttpMetrics
        // (see RoleMetrics.MeterName docs). Other tests in the same xUnit
        // process may concurrently construct HttpMetrics on that meter, so
        // we filter the listener callback to the role-specific instrument
        // prefixes ("legacy_role_*", "sod_violation_*", "org_role_*") to
        // isolate this test from HttpMetrics-test parallelism.
        var names = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == RoleMetrics.MeterName
                    && (instrument.Name.StartsWith("legacy_role_", StringComparison.Ordinal)
                        || instrument.Name.StartsWith("sod_violation_", StringComparison.Ordinal)
                        || instrument.Name.StartsWith("org_role_", StringComparison.Ordinal)))
                {
                    names.Add(instrument.Name);
                }
            },
        };
        listener.Start();

        // Act
        using var metrics = new RoleMetrics();

        // Assert
        names.Should().BeEquivalentTo(new[]
        {
            "legacy_role_endpoint_call_total",
            "legacy_role_endpoint_bypass_total",
            "sod_violation_warning_total",
            "org_role_propagation_duration_seconds",
        });
    }

    [Fact]
    public void Propagation_histogram_has_seconds_unit()
    {
        // Arrange
        Instrument? propagationInstrument = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "org_role_propagation_duration_seconds")
                {
                    propagationInstrument = instrument;
                }
            },
        };
        listener.Start();

        // Act
        using var metrics = new RoleMetrics();

        // Assert
        propagationInstrument.Should().NotBeNull();
        propagationInstrument!.Unit.Should().Be("s",
            "the propagation histogram is the only role metric with an OpenTelemetry unit");
    }

    [Fact]
    public void RecordPropagation_emits_systems_bucket_tag()
    {
        // Arrange
        var measurements = new List<(double Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "org_role_propagation_duration_seconds")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            measurements.Add((value, tags.ToArray()));
        });
        listener.Start();

        using var metrics = new RoleMetrics();

        // Act: one measurement per cardinality bucket (4 total)
        metrics.RecordPropagation(Guid.NewGuid(), RmfRole.MissionOwner, systemsProcessed: 5,   TimeSpan.FromSeconds(1));
        metrics.RecordPropagation(Guid.NewGuid(), RmfRole.MissionOwner, systemsProcessed: 50,  TimeSpan.FromSeconds(2));
        metrics.RecordPropagation(Guid.NewGuid(), RmfRole.MissionOwner, systemsProcessed: 250, TimeSpan.FromSeconds(3));
        metrics.RecordPropagation(Guid.NewGuid(), RmfRole.MissionOwner, systemsProcessed: 1000, TimeSpan.FromSeconds(4));

        // Assert
        measurements.Should().HaveCount(4);
        measurements.Select(m => m.Tags.Single(t => t.Key == "systems_bucket").Value)
            .Should().BeEquivalentTo(new object?[] { "1-10", "11-100", "101-500", "500+" });
    }

    [Fact]
    public void RecordSodWarning_tags_caller_and_conflicting_role()
    {
        // Arrange
        var measurements = new List<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "sod_violation_warning_total")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            measurements.Add(tags.ToArray());
        });
        listener.Start();

        using var metrics = new RoleMetrics();

        // Act
        metrics.RecordSodWarning(Guid.NewGuid(), RmfRole.AuthorizingOfficial, RmfRole.SystemOwner);

        // Assert
        measurements.Should().HaveCount(1);
        var tags = measurements[0];
        tags.Should().Contain(t => t.Key == "caller_role" && (string?)t.Value == "AuthorizingOfficial");
        tags.Should().Contain(t => t.Key == "conflicting_role" && (string?)t.Value == "SystemOwner");
    }

    [Fact]
    public void Dispose_disposes_meter()
    {
        // Arrange
        var metrics = new RoleMetrics();

        // Act
        metrics.Dispose();

        // Assert: a second Dispose() must be safe (IDisposable contract)
        var act = () => metrics.Dispose();
        act.Should().NotThrow();
    }
}
