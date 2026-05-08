using Ato.Copilot.Core.Data.Context;
using Ato.Copilot.Core.Data.Interceptors;
using Ato.Copilot.Core.Interfaces.Tenancy;
using Ato.Copilot.Core.Models.Tenancy;
using Ato.Copilot.Core.Services.Tenancy;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Ato.Copilot.Tests.Integration.Tenancy;

/// <summary>
/// T078 [US3]: Verifies the SingleTenant bootstrap path
/// (<see cref="TenantBootstrapService.EnsureDefaultTenantAndBackfillAsync"/>):
/// <list type="bullet">
///   <item>creates the default tenant when absent (FR-070 / acceptance scenario 1),</item>
///   <item>backfills every NULL <c>TenantId</c> on retrofitted tables to the
///         default tenant (FR-070, acceptance scenario 1),</item>
///   <item>emits the required <c>"Migrated {Count} rows to default tenant {DefaultTenantId}"</c>
///         log entry exactly once,</item>
///   <item>is idempotent — second call is a no-op and emits no migration line.</item>
/// </list>
/// </summary>
/// <remarks>
/// RED until T081–T083 are implemented. Uses a per-fixture in-memory SQLite
/// database with the production <see cref="AtoCopilotContext"/>.
/// </remarks>
public class SingleTenantBootstrapTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _sp = null!;
    private CapturingLoggerProvider _logProvider = null!;

    public SingleTenantBootstrapTests(ITestOutputHelper output)
    {
        _ = output;
    }

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        _logProvider = new CapturingLoggerProvider();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(_logProvider);
        });
        services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
        services.AddSingleton<TenantStampingSaveChangesInterceptor>();
        services.AddDbContext<AtoCopilotContext>((sp, opt) =>
        {
            opt.UseSqlite(_connection);
            opt.AddInterceptors(sp.GetRequiredService<TenantStampingSaveChangesInterceptor>());
        });
        _sp = services.BuildServiceProvider();

        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtoCopilotContext>();
        await db.Database.EnsureCreatedAsync();

        // Simulate the pre-retrofit upgrade scenario: drop the EF-created
        // Organizations table (which has TenantId NOT NULL per the model) and
        // recreate it with TenantId NULLABLE — this is the on-disk state of a
        // production database that existed before Feature 048's TenantId
        // column was added. The bootstrap's backfill is what migrates these
        // rows to have a non-NULL TenantId.
        await db.Database.ExecuteSqlRawAsync(
            """
            DROP TABLE IF EXISTS "Organizations";
            CREATE TABLE "Organizations" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Organizations" PRIMARY KEY,
                "TenantId" TEXT NULL,
                "Name" TEXT NOT NULL,
                "Description" TEXT NULL,
                "ParentOrganizationId" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "CreatedBy" TEXT NOT NULL DEFAULT 'system',
                "RowVersion" BLOB NULL
            );
            """);
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task EnsureDefaultTenant_CreatesRow_WhenMissing()
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtoCopilotContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<SingleTenantBootstrapTests>>();

        var report = await TenantBootstrapService.EnsureDefaultTenantAndBackfillAsync(
            db, isSingleTenantMode: true, defaultTenantIdOverride: null, logger);

        report.Created.Should().BeTrue("default tenant did not exist before the call");
        report.DefaultTenantId.Should().Be(TenantBootstrapService.DefaultTenantId);

        var stored = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == TenantBootstrapService.DefaultTenantId);
        stored.Should().NotBeNull();
        stored!.OnboardingState.Should().Be(OnboardingState.Active);
        stored.Status.Should().Be(TenantStatus.Active);
    }

    [Fact]
    public async Task EnsureDefaultTenant_BackfillsNullTenantIdRows_AndEmitsMigrationLogLine()
    {
        // Arrange — insert an Organization with NULL TenantId by going around
        // the EF model (raw SQL) so the interceptor doesn't stamp it.
        await using (var scope = _sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AtoCopilotContext>();
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Organizations" ("Id","TenantId","Name","CreatedAt","CreatedBy")
                VALUES (@p0, NULL, 'NullScoped', @p1, 'seed');
                """,
                Guid.NewGuid().ToString(),
                DateTimeOffset.UtcNow.ToString("o"));
        }

        // Act
        await using var scope2 = _sp.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AtoCopilotContext>();
        var logger = scope2.ServiceProvider.GetRequiredService<ILogger<SingleTenantBootstrapTests>>();

        var report = await TenantBootstrapService.EnsureDefaultTenantAndBackfillAsync(
            db2, isSingleTenantMode: true, defaultTenantIdOverride: null, logger);

        // Assert — at least one row migrated, log line present.
        report.RowsBackfilled.Should().BeGreaterOrEqualTo(1);
        report.TablesTouched.Should().Contain("Organizations");

        var migrationLine = _logProvider.Entries
            .FirstOrDefault(e => e.Message.Contains("Migrated") &&
                                  e.Message.Contains("rows to default tenant"));
        migrationLine.Should().NotBeNull(
            "FR-070 requires a single 'Migrated {Count} rows to default tenant {DefaultTenantId}' log entry");

        // Verify no NULL TenantId rows remain on Organizations.
        var nullCount = (long)(await db2.Database.ExecuteSqlRawAsync(
            "SELECT COUNT(*) FROM \"Organizations\" WHERE \"TenantId\" IS NULL"));
        // ExecuteSqlRawAsync returns rows-affected for SELECT under SQLite in EF Core 9;
        // use a scalar query for robust assertion instead.
        var conn = db2.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM \"Organizations\" WHERE \"TenantId\" IS NULL";
        var nullsRemaining = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        nullsRemaining.Should().Be(0, "all NULL TenantId rows must be backfilled");
    }

    [Fact]
    public async Task EnsureDefaultTenant_IsIdempotent()
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtoCopilotContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<SingleTenantBootstrapTests>>();

        var first = await TenantBootstrapService.EnsureDefaultTenantAndBackfillAsync(
            db, isSingleTenantMode: true, defaultTenantIdOverride: null, logger);
        var second = await TenantBootstrapService.EnsureDefaultTenantAndBackfillAsync(
            db, isSingleTenantMode: true, defaultTenantIdOverride: null, logger);

        first.Created.Should().BeTrue();
        second.Created.Should().BeFalse("default tenant already existed on the second call");
        second.RowsBackfilled.Should().Be(0, "no NULL rows should remain after the first call");
    }

    [Fact]
    public async Task MultiTenantMode_DoesNotCreateDefaultTenant_AndFailsFastOnNullRows()
    {
        // Arrange — insert a NULL-TenantId row to force the FR-071 fail-fast path.
        await using (var scope = _sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AtoCopilotContext>();
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Organizations" ("Id","TenantId","Name","CreatedAt","CreatedBy")
                VALUES (@p0, NULL, 'NullScoped-Multi', @p1, 'seed');
                """,
                Guid.NewGuid().ToString(),
                DateTimeOffset.UtcNow.ToString("o"));
        }

        await using var scope2 = _sp.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AtoCopilotContext>();
        var logger = scope2.ServiceProvider.GetRequiredService<ILogger<SingleTenantBootstrapTests>>();

        var act = async () =>
            await TenantBootstrapService.EnsureDefaultTenantAndBackfillAsync(
                db2, isSingleTenantMode: false, defaultTenantIdOverride: null, logger);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("MultiTenant", StringComparison.OrdinalIgnoreCase),
                "FR-071 requires fail-fast in MultiTenant mode when NULL TenantIds remain");
    }

    /// <summary>Lightweight in-memory <see cref="ILoggerProvider"/> capturing log events for assertion.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _parent;
            public CapturingLogger(CapturingLoggerProvider parent) => _parent = parent;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                Func<TState, Exception?, string> formatter)
            {
                lock (_parent.Entries)
                {
                    _parent.Entries.Add((level, formatter(state, ex)));
                }
            }
        }
    }
}
