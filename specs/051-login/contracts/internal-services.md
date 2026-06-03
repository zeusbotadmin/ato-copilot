# Phase 1 — Internal Services Contract: Auth Services

**Feature**: 051-login
**Plan**: [../plan.md](../plan.md)
**Spec**: [../spec.md](../spec.md)
**Data model**: [../data-model.md](../data-model.md)
**Research**: [../research.md](../research.md)
**Date**: 2026-05-28

This document pins the C# interfaces and options-binding shapes for the
**four new internal services** introduced by Feature 051.
All four live under `src/Ato.Copilot.Core/Services/Auth/` and their
interfaces under `src/Ato.Copilot.Core/Interfaces/Auth/`.

## 1. `ILoginAuditService`

### 1.1 Purpose

The single SRP boundary for writing and reading `LoginAuditEvent` rows.
All endpoints (HTTP / VS Code / Teams) route through this service —
never through `AtoCopilotContext.LoginAuditEvents.AddAsync` directly.

### 1.2 Interface

```csharp
namespace Ato.Copilot.Core.Interfaces.Auth;

public interface ILoginAuditService
{
    /// <summary>
    /// Append a new audit row. MUST NOT call <c>SaveChangesAsync</c> —
    /// the caller controls the transaction (R6 / Feature-050 SRP parity).
    /// The returned <see cref="LoginAuditEvent"/> has its <c>Id</c>
    /// populated but is not yet persisted.
    /// </summary>
    Task<LoginAuditEvent> AppendAsync(LoginAuditEventDraft draft, CancellationToken ct = default);

    /// <summary>
    /// List audit rows for a tenant in reverse chronological order.
    /// Subject to the automatic <c>[TenantScoped]</c> query filter, so
    /// callers without tenant context get no rows.
    /// </summary>
    Task<IReadOnlyList<LoginAuditEvent>> ListAsync(
        Guid tenantId,
        DateTimeOffset? since = null,
        int take = 100,
        CancellationToken ct = default);

    /// <summary>
    /// SOC-analyst read path for <c>SYSTEM_TENANT_ID</c> rows
    /// (per <see cref="research.md"/> R9). Requires the
    /// <c>Auth.SocAnalyst</c> claim on the calling identity.
    /// Throws <see cref="UnauthorizedAccessException"/> otherwise.
    /// </summary>
    Task<IReadOnlyList<LoginAuditEvent>> ListSystemTenantAsync(
        DateTimeOffset? since = null,
        int take = 100,
        CancellationToken ct = default);
}

public record LoginAuditEventDraft(
    LoginAuditEventType EventType,
    string? Oid,
    string? Tid,
    Guid EffectiveTenantId,
    string CorrelationId,
    string SourceIp,
    string UserAgent,
    LoginSurface Surface,
    LoginErrorClass? ErrorClass = null,
    string? MetadataJson = null);
```

### 1.3 Behavior contract

- `AppendAsync` validates the draft (per [data-model.md § 1.6](../data-model.md))
  and populates `Id = Guid.NewGuid()` + `OccurredAt = DateTimeOffset.UtcNow`.
  It then calls `AtoCopilotContext.LoginAuditEvents.AddAsync(entity, ct)`
  and returns.
- `ListAsync(tenantId, since, take)` queries
  `LoginAuditEvents.Where(e => e.OccurredAt >= since).OrderByDescending(e => e.OccurredAt).Take(take)`.
  The `[TenantScoped]` query filter applies automatically, so passing a
  `tenantId` that the caller does not own returns an empty list.
- `ListSystemTenantAsync` enforces the `Auth.SocAnalyst` claim BEFORE
  the query, uses `.IgnoreQueryFilters()` scoped to
  `EffectiveTenantId == SYSTEM_TENANT_ID` ONLY, and throws if a future
  caller tries to extend the predicate.

### 1.4 No `UpdateAsync` / `DeleteAsync`

The interface intentionally omits these. A reflection unit test
(`LoginAuditServiceTests.InterfaceMethods_Are_Exactly_Three`) asserts the
public surface is `{ AppendAsync, ListAsync, ListSystemTenantAsync }` to
prevent accidental addition.

## 2. `ILoginThrottleService`

### 2.1 Purpose

Track per-IP AND per-identity failed-login counts in a process-restart-
durable store. Decision latency target: ≤ 5 ms p95 (NFR per
[plan.md § Performance Goals](../plan.md)).

### 2.2 Interface

```csharp
namespace Ato.Copilot.Core.Interfaces.Auth;

public interface ILoginThrottleService
{
    /// <summary>
    /// Register a failed login attempt and return whether the next
    /// attempt should be allowed. Idempotent within a single bucket.
    /// </summary>
    Task<LoginThrottleDecision> RegisterAttemptAsync(
        string sourceIp,
        string? identityKey,
        CancellationToken ct = default);

    /// <summary>
    /// Reset counters for an identity after a successful login.
    /// Per-IP counter is NOT reset (a successful sign-in from the same
    /// IP does not invalidate prior failures from that IP — likely a
    /// shared NAT or proxy).
    /// </summary>
    Task ResetIdentityAsync(string identityKey, CancellationToken ct = default);
}

public sealed record LoginThrottleDecision(
    bool Allowed,
    TimeSpan RetryAfter,
    int CurrentIpCount,
    int CurrentIdentityCount);
```

### 2.3 Behavior contract

Per [research.md § R7](../research.md):

- Two `IDistributedCache` keys per attempt:
  - `login-throttle:ip:{ip}:{minute-bucket}`
  - `login-throttle:identity:{identityKey ?? "anonymous"}:{minute-bucket}`
- Each `SetAsync` uses absolute expiration = 60 seconds.
- `RegisterAttemptAsync` increments both keys (a `GetAsync` + `SetAsync`
  pair until .NET 9's atomic `IncrementAsync` lands as standard;
  Redis `INCR` is available via a typed extension and IS used in prod).
- If either count exceeds the env-specific threshold (`PerIpPerMinute` or
  `PerIdentityPerMinute`), returns `Allowed = false` with
  `RetryAfter = (60 - secondsInCurrentBucket) seconds`.
- Otherwise returns `Allowed = true`.

### 2.4 No middleware coupling

The HTTP middleware that consumes this service decides the response code
(`429 Too Many Requests`) and emits the audit row. The service itself
emits no audit rows and no HTTP responses — pure decision logic.

## 3. `IRememberedTenantCookieService`

### 3.1 Purpose

Issue and validate HMAC-signed first-party cookies for the "remember this
tenant on this device" feature (US3 / FR-012). No server-side mirror.

### 3.2 Interface

```csharp
namespace Ato.Copilot.Core.Interfaces.Auth;

public interface IRememberedTenantCookieService
{
    /// <summary>
    /// Build the cookie value (NOT the HTTP cookie header). The caller
    /// is responsible for setting <c>Set-Cookie</c> with the configured
    /// attributes from <c>AuthOptions:Cookie</c>.
    /// </summary>
    string Issue(Guid tenantId, TimeSpan ttl);

    /// <summary>
    /// Validate a cookie value and extract the tenant id. Returns
    /// <c>null</c> for any tampered / expired / unknown cookie. NEVER
    /// throws.
    /// </summary>
    Guid? Validate(string cookieValue);
}
```

### 3.3 Behavior contract

Per [research.md § R8](../research.md):

- `Issue` produces a 4-part `.`-delimited base64url string covering
  `tenantId || iat || exp || hmac` with HMAC-SHA256 over the first 3
  parts. The HMAC key is sourced from
  `IOptions<AuthOptions>.Value.Cookie.SigningKey` (loaded from Key Vault
  in prod, from `appsettings.Development.json` in dev).
- `Validate` rejects any cookie whose HMAC fails OR whose `exp` is in
  the past. It does NOT verify the tenant exists or is `Active` — that
  is the caller's job (the `/api/auth/me` handler checks tenant state).

### 3.4 Key rotation

The cookie signing key is a single 32-byte secret. Rotating the secret
invalidates ALL outstanding cookies in O(1) operator time. There is no
"old key, new key" parallel-acceptance window — that complexity is YAGNI
for a device-only "remember this tenant" cookie.

## 4. `ILoginAuditArchiveService` and `ILoginAuditArchiveSink`

### 4.1 Purpose

Move rows older than 13 months from the hot `LoginAuditEvents` table to
immutable cold storage per FR-036a / Q3.

### 4.2 Interface

```csharp
namespace Ato.Copilot.Core.Interfaces.Auth;

/// <summary>
/// Singleton hosted service. Wakes daily at <c>Auth:Archive:RunHourUtc</c>
/// (default <c>02</c>) and archives rows older than 13 months in
/// 1,000-row batches.
/// </summary>
public interface ILoginAuditArchiveService : IHostedService { }

/// <summary>
/// Plug-in point for the cold-archive storage implementation. Returns
/// the archive blob URL or filename on success. Throws to abort the
/// batch and re-try next run.
/// </summary>
public interface ILoginAuditArchiveSink
{
    Task<string> WriteBatchAsync(
        IReadOnlyList<LoginAuditEvent> rows,
        CancellationToken ct);
}
```

### 4.3 Two sinks shipped (R3)

| Implementation | DI registration | Used in |
|---|---|---|
| `AzureBlobAppendArchiveSink` | when `Auth:Archive:Sink = AzureBlobAppend` | Production (AzureUSGovernment storage account, immutable container) |
| `FileSystemArchiveSink` | when `Auth:Archive:Sink = FileSystem` | Development, CI tests |

### 4.4 Behavior contract

- The service runs in a `while (!stoppingToken.IsCancellationRequested)`
  loop with a single `Task.Delay(nextRunDelay)` between iterations.
- Per iteration:
  1. Compute the cutoff = `DateTimeOffset.UtcNow - TimeSpan.FromDays(395)`
     (13 months ≈ 395 days, slight over-retention is intentional per AU-11
     "no less than").
  2. Query `LoginAuditEvents` with `.IgnoreQueryFilters()` (the archive
     job is tenant-agnostic) `.Where(e => e.OccurredAt < cutoff)`
     `.OrderBy(e => e.OccurredAt)` `.Take(1000)`.
  3. Call `_sink.WriteBatchAsync(rows, ct)`.
  4. On success, `LoginAuditEvents.RemoveRange(rows)` + `SaveChangesAsync`.
     On exception, log and abort the batch.
  5. Loop until the query returns < 1000 rows, then sleep until next
     `RunHourUtc`.

### 4.5 Idempotency

The sink is responsible for idempotency on its end (Append-Blob is
naturally idempotent on retry; FileSystem prepends a unique correlation
id to the filename). The service does NOT track "what was archived" —
once removed from the hot table, the only record is in the cold archive.

## 5. Options-binding shapes

### 5.1 `AuthOptions`

```csharp
namespace Ato.Copilot.Core.Configuration;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public LoginAuthMethod DefaultMethod { get; set; } = LoginAuthMethod.Cac;
    public int IdleTimeoutMinutes { get; set; } = 30;
    public int RememberTenantCookieDays { get; set; } = 30;
    public AzureCloud Cloud { get; set; } = AzureCloud.AzureUSGovernment;

    public AuthCookieOptions Cookie { get; set; } = new();
    public AuthVsCodeOptions VSCode { get; set; } = new();
    // NOTE: The simulation-enable gate is sourced from the existing Feature 027
    //       `CacAuth:SimulationMode` flag, NOT from a new Auth:Simulation:Enabled
    //       flag (analysis C10). This avoids two parallel feature flags for the
    //       same behavior. The simulation panel is shown when:
    //         ASPNETCORE_ENVIRONMENT == "Development"
    //         AND CacAuthOptions.SimulationMode == true
    //         AND CacAuthOptions.SimulatedIdentities is non-empty.
    public AuthThrottleOptions Throttle { get; set; } = new();
    public AuthTeamsSsoOptions TeamsSso { get; set; } = new();
    public AuthArchiveOptions Archive { get; set; } = new();
    public AuthMsalOptions Msal { get; set; } = new();
}

public sealed class AuthCookieOptions
{
    /// <summary>HMAC-SHA256 signing key (32 bytes, base64). Required.</summary>
    public string SigningKey { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public bool Secure { get; set; } = true;
}

public sealed class AuthVsCodeOptions
{
    public string Mode { get; set; } = "DeviceCode";  // "DeviceCode" only for now
}

// AuthSimulationOptions removed (analysis C10) — simulation gate is owned by
// Feature 027's `CacAuthOptions.SimulationMode` flag. Adding a second Auth-side
// flag would be redundant and create two startup-validation paths for the same
// invariant.

public sealed class AuthThrottleOptions
{
    /// <summary>
    /// Throttle thresholds per 60-second sliding bucket. Selection:
    ///   ASPNETCORE_ENVIRONMENT == "Development"  → <see cref="Development"/>
    ///   ANY OTHER value (Staging, Production, ...)→ <see cref="Production"/>
    /// (Analysis C11 — non-Development environments use the Production block.)
    ///
    /// Defaults match spec.md FR-034: Production 20/min/IP + 10/min/identity;
    /// Development 100/min/IP + 100/min/identity.
    /// </summary>
    public ThrottleBucket Development { get; set; } = new() { PerIpPerMinute = 100, PerIdentityPerMinute = 100 };
    public ThrottleBucket Production  { get; set; } = new() { PerIpPerMinute = 20,  PerIdentityPerMinute = 10 };
}

public sealed class ThrottleBucket
{
    public int PerIpPerMinute { get; set; }
    public int PerIdentityPerMinute { get; set; }
}

public sealed class AuthTeamsSsoOptions
{
    public TeamsSsoMode Mode { get; set; } = TeamsSsoMode.Optional;
    public string ConnectionName { get; set; } = string.Empty;
}

public enum TeamsSsoMode { Required, Optional, Disabled }

public sealed class AuthArchiveOptions
{
    public ArchiveSinkKind Sink { get; set; } = ArchiveSinkKind.FileSystem;
    public int RunHourUtc { get; set; } = 2;
    public string AzureBlobAccountUrl { get; set; } = string.Empty;
    public string AzureBlobContainer  { get; set; } = "audit-archive";
    public string FileSystemRoot      { get; set; } = "./archive";
}

public enum ArchiveSinkKind { AzureBlobAppend, FileSystem }

public sealed class AuthMsalOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string Authority { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string PostLogoutRedirectUri { get; set; } = string.Empty;
}

public enum LoginAuthMethod { Cac, Entra }
public enum AzureCloud { AzurePublic, AzureUSGovernment }
```

### 5.2 `IValidateOptions<AuthOptions>`

Per [research.md § R12](../research.md):

```csharp
public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var errors = new List<string>();

        // Cookie signing key MUST be set in non-Development.
        if (!_isDevelopment && string.IsNullOrWhiteSpace(options.Cookie.SigningKey))
            errors.Add("Auth:Cookie:SigningKey is required outside Development.");

        // IdleTimeoutMinutes range.
        if (options.IdleTimeoutMinutes < 5 || options.IdleTimeoutMinutes > 480)
            errors.Add("Auth:IdleTimeoutMinutes must be between 5 and 480.");

        // RememberTenantCookieDays range.
        if (options.RememberTenantCookieDays < 1 || options.RememberTenantCookieDays > 365)
            errors.Add("Auth:RememberTenantCookieDays must be between 1 and 365.");

        // Throttle counts must be positive.
        if (options.Throttle.Production.PerIpPerMinute <= 0)
            errors.Add("Auth:Throttle:Production:PerIpPerMinute must be > 0.");
        // ... similar for the three other counts.

        // Teams SSO Required mode must have a connection name AND a
        // manifest with webApplicationInfo.id (validated separately at
        // startup via the manifest path).
        if (options.TeamsSso.Mode == TeamsSsoMode.Required &&
            string.IsNullOrWhiteSpace(options.TeamsSso.ConnectionName))
            errors.Add("Auth:TeamsSso:ConnectionName is required when Mode = Required.");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
```

## 6. DI registration

In `src/Ato.Copilot.Mcp/Program.cs`:

```csharp
// Options
services.AddOptions<AuthOptions>()
    .Bind(configuration.GetSection(AuthOptions.SectionName))
    .ValidateOnStart();
services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();

// Services
services.AddScoped<ILoginAuditService, LoginAuditService>();
services.AddSingleton<ILoginThrottleService, LoginThrottleService>();
services.AddSingleton<IRememberedTenantCookieService, RememberedTenantCookieService>();

// Archive sink — choose by config
if (authOptions.Archive.Sink == ArchiveSinkKind.AzureBlobAppend)
    services.AddSingleton<ILoginAuditArchiveSink, AzureBlobAppendArchiveSink>();
else
    services.AddSingleton<ILoginAuditArchiveSink, FileSystemArchiveSink>();

services.AddHostedService<LoginAuditArchiveService>();

// Distributed cache for throttle
if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
    services.AddDistributedMemoryCache();
else
    services.AddStackExchangeRedisCache(o => {
        o.Configuration = configuration.GetConnectionString("Redis");
        o.InstanceName  = "ato-throttle:";
    });
```

## 7. Cross-reference matrix

| FR | Service | Section |
|---|---|---|
| FR-007 / FR-007a | (front-end only — see [contracts/frontend-types.md](./frontend-types.md)) | — |
| FR-012 | `IRememberedTenantCookieService` | § 3 |
| FR-018 | (extension-side — see [contracts/vscode-extension.md](./vscode-extension.md)) | — |
| FR-021 | `AuthOptions.TeamsSso` + `AuthOptionsValidator` | § 5.1, § 5.2 |
| FR-023 / FR-024 | `AuthOptions.Simulation.Enabled` + endpoint § 5 of [contracts/http-api.md](./http-api.md) | § 5.1 |
| FR-032 / FR-033 | `ILoginAuditService.AppendAsync` | § 1 |
| FR-034 / FR-035 | `ILoginThrottleService.RegisterAttemptAsync` | § 2 |
| FR-036a | `ILoginAuditArchiveService` + `ILoginAuditArchiveSink` | § 4 |
| FR-039 (SOC analyst) | `ILoginAuditService.ListSystemTenantAsync` | § 1.2, § 1.3 |
