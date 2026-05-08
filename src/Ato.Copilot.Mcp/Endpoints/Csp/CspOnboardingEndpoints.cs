using System.Diagnostics;
using System.Security.Claims;
using Ato.Copilot.Core.Interfaces.Tenancy;
using Ato.Copilot.Core.Models.Tenancy;
using Ato.Copilot.Mcp.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Ato.Copilot.Mcp.Endpoints.Csp;

/// <summary>
/// CSP-Admin onboarding wizard endpoints (Feature 048 US7 / FR-006 / FR-090 /
/// FR-092 / FR-093). Mirrors
/// <c>specs/048-tenant-isolation/contracts/csp-onboarding.openapi.yaml</c>.
/// </summary>
/// <remarks>
/// Auth — every endpoint requires <c>ITenantContext.IsCspAdmin = true</c>;
/// otherwise the response is <c>403 FORBIDDEN_NOT_CSP_ADMIN</c>.
/// SingleTenant short-circuit — when <c>DeploymentOptions.Mode</c> is
/// <see cref="DeploymentMode.SingleTenant"/>, every route returns
/// <c>404 SINGLE_TENANT_MODE</c> per FR-093 BEFORE touching the DB so no
/// <see cref="CspProfile"/> row is ever persisted.
/// </remarks>
public static class CspOnboardingEndpoints
{
    public static IEndpointRouteBuilder MapCspOnboardingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/csp/onboarding")
            .WithTags("CSP Onboarding");

        group.MapGet("/state", GetStateAsync).WithName("GetCspOnboardingState");
        group.MapPost("/identity", PostIdentityAsync).WithName("PostCspOnboardingIdentity");
        group.MapPost("/support", PostSupportAsync).WithName("PostCspOnboardingSupport");
        group.MapPost("/classification", PostClassificationAsync).WithName("PostCspOnboardingClassification");
        group.MapPost("/submit", PostSubmitAsync).WithName("PostCspOnboardingSubmit");

        return app;
    }

    // ─── handlers ───────────────────────────────────────────────────────────

    private static async Task<IResult> GetStateAsync(
        HttpContext http,
        ITenantContext tenantCtx,
        ICspProfileService service,
        IOptions<DeploymentOptions> deployment,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (ShouldShortCircuitSingleTenant(deployment, out var singleTenantResult))
            return singleTenantResult;
        if (!tenantCtx.IsCspAdmin) return ForbiddenNotCspAdmin(sw);

        var profile = await service.GetAsync(ct);
        var step = service.ComputeCurrentStep(profile);
        return Success(sw, BuildStateDto(profile, step));
    }

    private static async Task<IResult> PostIdentityAsync(
        [FromBody] IdentityRequest body,
        HttpContext http,
        ITenantContext tenantCtx,
        ICspProfileService service,
        IOptions<DeploymentOptions> deployment,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (ShouldShortCircuitSingleTenant(deployment, out var singleTenantResult))
            return singleTenantResult;
        if (!tenantCtx.IsCspAdmin) return ForbiddenNotCspAdmin(sw);

        try
        {
            var actor = ResolveActor(http);
            var profile = await service.UpdateIdentityAsync(
                body.LegalEntityName ?? string.Empty,
                body.DisplayName ?? string.Empty,
                body.LogoUrl,
                actor,
                ct);
            var step = service.ComputeCurrentStep(profile);
            return Success(sw, BuildStateDto(profile, step));
        }
        catch (ArgumentException ex)
        {
            return ValidationError(sw, ex.Message);
        }
        catch (CspAlreadyOnboardedException)
        {
            return AlreadyOnboarded(sw);
        }
    }

    private static async Task<IResult> PostSupportAsync(
        [FromBody] SupportRequest body,
        HttpContext http,
        ITenantContext tenantCtx,
        ICspProfileService service,
        IOptions<DeploymentOptions> deployment,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (ShouldShortCircuitSingleTenant(deployment, out var singleTenantResult))
            return singleTenantResult;
        if (!tenantCtx.IsCspAdmin) return ForbiddenNotCspAdmin(sw);

        try
        {
            var actor = ResolveActor(http);
            var profile = await service.UpdateSupportAsync(
                body.PrimarySupportEmail ?? string.Empty,
                body.SupportPhone,
                actor,
                ct);
            var step = service.ComputeCurrentStep(profile);
            return Success(sw, BuildStateDto(profile, step));
        }
        catch (ArgumentException ex)
        {
            return ValidationError(sw, ex.Message);
        }
        catch (CspAlreadyOnboardedException)
        {
            return AlreadyOnboarded(sw);
        }
    }

    private static async Task<IResult> PostClassificationAsync(
        [FromBody] ClassificationRequest body,
        HttpContext http,
        ITenantContext tenantCtx,
        ICspProfileService service,
        IOptions<DeploymentOptions> deployment,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (ShouldShortCircuitSingleTenant(deployment, out var singleTenantResult))
            return singleTenantResult;
        if (!tenantCtx.IsCspAdmin) return ForbiddenNotCspAdmin(sw);

        try
        {
            if (!Enum.TryParse<ClassificationLevel>(
                    body.DefaultClassificationFloor, ignoreCase: true, out var floor))
            {
                return ValidationError(sw,
                    $"defaultClassificationFloor '{body.DefaultClassificationFloor}' is not a valid ClassificationLevel.");
            }
            var actor = ResolveActor(http);
            var profile = await service.UpdateClassificationAsync(floor, actor, ct);
            var step = service.ComputeCurrentStep(profile);
            return Success(sw, BuildStateDto(profile, step));
        }
        catch (ArgumentException ex)
        {
            return ValidationError(sw, ex.Message);
        }
        catch (CspAlreadyOnboardedException)
        {
            return AlreadyOnboarded(sw);
        }
    }

    private static async Task<IResult> PostSubmitAsync(
        HttpContext http,
        ITenantContext tenantCtx,
        ICspProfileService service,
        IOptions<DeploymentOptions> deployment,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (ShouldShortCircuitSingleTenant(deployment, out var singleTenantResult))
            return singleTenantResult;
        if (!tenantCtx.IsCspAdmin) return ForbiddenNotCspAdmin(sw);

        try
        {
            var actor = ResolveActor(http);
            var profile = await service.SubmitAsync(actor, ct);
            var step = service.ComputeCurrentStep(profile);
            return Success(sw, BuildStateDto(profile, step));
        }
        catch (CspOnboardingIncompleteException ex)
        {
            return ValidationError(sw, ex.Message);
        }
        catch (CspAlreadyOnboardedException)
        {
            return AlreadyOnboarded(sw);
        }
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private static bool ShouldShortCircuitSingleTenant(
        IOptions<DeploymentOptions> deployment, out IResult result)
    {
        if (deployment.Value.Mode == DeploymentMode.SingleTenant)
        {
            result = Error(StatusCodes.Status404NotFound, "SINGLE_TENANT_MODE",
                "CSP onboarding is unavailable in SingleTenant deployments.");
            return true;
        }
        result = null!;
        return false;
    }

    private static string ResolveActor(HttpContext http)
    {
        var raw = http.User.FindFirstValue("oid")
            ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? http.User.Identity?.Name;
        return string.IsNullOrWhiteSpace(raw) ? "system" : raw;
    }

    private static object BuildStateDto(CspProfile? profile, CspOnboardingStep currentStep)
    {
        if (profile is null)
        {
            return new
            {
                cspProfileId = (Guid?)null,
                onboardingState = OnboardingState.Pending.ToString(),
                currentStep = currentStep.ToString(),
                identity = (object?)null,
                supportContact = (object?)null,
                classification = (object?)null,
                onboardingCompletedAt = (DateTimeOffset?)null,
            };
        }

        return new
        {
            cspProfileId = (Guid?)profile.Id,
            onboardingState = profile.OnboardingState.ToString(),
            currentStep = currentStep.ToString(),
            identity = profile.IdentityCompletedAt is null ? null : new
            {
                legalEntityName = profile.LegalEntityName,
                displayName = profile.DisplayName,
                logoUrl = profile.LogoUrl,
            },
            supportContact = profile.SupportCompletedAt is null ? null : new
            {
                primarySupportEmail = profile.PrimarySupportEmail,
                supportPhone = profile.SupportPhone,
            },
            classification = profile.ClassificationCompletedAt is null ? null : new
            {
                defaultClassificationFloor = profile.DefaultClassificationFloor.ToString(),
            },
            onboardingCompletedAt = profile.OnboardingCompletedAt,
        };
    }

    private static IResult Success(Stopwatch sw, object data) =>
        Results.Json(new
        {
            status = "success",
            data,
            metadata = new
            {
                executionTimeMs = sw.ElapsedMilliseconds,
                timestamp = DateTimeOffset.UtcNow,
            },
        }, statusCode: StatusCodes.Status200OK);

    private static IResult ForbiddenNotCspAdmin(Stopwatch sw) =>
        Results.Json(new
        {
            status = "error",
            metadata = new
            {
                executionTimeMs = sw.ElapsedMilliseconds,
                timestamp = DateTimeOffset.UtcNow,
            },
            error = new
            {
                errorCode = "FORBIDDEN_NOT_CSP_ADMIN",
                message = "Operation requires CSP.Admin role.",
            },
        }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult ValidationError(Stopwatch sw, string message) =>
        Results.Json(new
        {
            status = "error",
            metadata = new
            {
                executionTimeMs = sw.ElapsedMilliseconds,
                timestamp = DateTimeOffset.UtcNow,
            },
            error = new { errorCode = "VALIDATION_FAILED", message },
        }, statusCode: StatusCodes.Status422UnprocessableEntity);

    private static IResult AlreadyOnboarded(Stopwatch sw) =>
        Results.Json(new
        {
            status = "error",
            metadata = new
            {
                executionTimeMs = sw.ElapsedMilliseconds,
                timestamp = DateTimeOffset.UtcNow,
            },
            error = new
            {
                errorCode = "CSP_ALREADY_ONBOARDED",
                message = "CSP onboarding is already complete; further submissions are rejected.",
            },
        }, statusCode: StatusCodes.Status409Conflict);

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new
        {
            status = "error",
            error = new { errorCode = code, message },
        }, statusCode: statusCode);

    // ─── request DTOs ──────────────────────────────────────────────────────

    public sealed record IdentityRequest(string? LegalEntityName, string? DisplayName, string? LogoUrl);

    public sealed record SupportRequest(string? PrimarySupportEmail, string? SupportPhone);

    public sealed record ClassificationRequest(string? DefaultClassificationFloor);
}
