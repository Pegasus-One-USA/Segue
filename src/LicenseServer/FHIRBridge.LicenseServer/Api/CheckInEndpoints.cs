using FHIRBridge.LicenseServer.Data;
using FHIRBridge.LicenseServer.Domain;
using FHIRBridge.LicenseServer.Licensing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.LicenseServer.Api;

/// <summary>
/// Maps <c>POST /api/checkin</c> — the endpoint deployed FHIRBridge instances call to report usage and have
/// their license token verified. Anonymous by design (a deployed instance has no admin session), fast, and
/// side-effect-free beyond the single upsert + append described below.
/// </summary>
public static class CheckInEndpoints
{
    public static IEndpointRouteBuilder MapCheckInApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/checkin", HandleCheckInAsync)
            .AllowAnonymous()
            .WithName("CheckIn");

        return app;
    }

    private static async Task<IResult> HandleCheckInAsync(
        [FromBody] CheckInRequest request,
        LicenseTokenValidator validator,
        LicenseServerDbContext db,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.InstallationId))
        {
            return Results.BadRequest(new CheckInError("installationId is required."));
        }

        var verification = validator.Validate(request.LicenseToken);
        if (!verification.IsValid)
        {
            logger.LogWarning(
                "Check-in rejected for installation {InstallationId}: {Reason}",
                request.InstallationId,
                verification.InvalidReason);
            return Results.Json(new CheckInError($"License token rejected: {verification.InvalidReason}"), statusCode: StatusCodes.Status401Unauthorized);
        }

        var receivedAtUtc = DateTime.UtcNow;

        // Best-effort enrichment only: find the IssuedLicense row (if any) whose stored token exactly
        // matches the one just posted - exact and unambiguous even if the same customer has multiple
        // historical licenses, unlike matching on "sub" alone. Null (no match) is expected and fine - e.g.
        // a token minted before this server existed, or via the main repo's own CLI minter tool - and
        // never fails the check-in.
        var matchedLicense = await db.IssuedLicenses
            .Where(l => l.Token == request.LicenseToken)
            .OrderByDescending(l => l.IssuedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var installation = await db.Installations.FindAsync([request.InstallationId], cancellationToken);
        if (installation is null)
        {
            installation = new Installation { InstallationId = request.InstallationId };
            db.Installations.Add(installation);
        }

        installation.CustomerId = verification.Sub;
        installation.CustomerName = verification.CustomerName;
        installation.Edition = verification.Edition;
        installation.CurrentIssuedLicenseId = matchedLicense?.Id;
        installation.LastSeenUtc = receivedAtUtc;
        installation.LastObservedUtc = request.ObservedUtc;
        installation.LastUserCount = request.Counts.UserCount;
        installation.LastSourceConnectionCount = request.Counts.SourceConnectionCount;
        installation.LastTenantCount = request.Counts.TenantCount;
        installation.LastWorkflowCount = request.Counts.WorkflowCount;
        installation.LastCumulativeConfiguredPipelineRunCount = request.Counts.CumulativeConfiguredPipelineRunCount;
        installation.LastCumulativeRuntimeWorkflowRunCount = request.Counts.CumulativeRuntimeWorkflowRunCount;
        installation.LastProcessedRecordsThisMonth = request.Counts.ProcessedRecordsThisMonth;

        db.CheckIns.Add(new CheckIn
        {
            InstallationId = request.InstallationId,
            CurrentIssuedLicenseId = matchedLicense?.Id,
            ObservedUtc = request.ObservedUtc,
            ReceivedAtUtc = receivedAtUtc,
            UserCount = request.Counts.UserCount,
            SourceConnectionCount = request.Counts.SourceConnectionCount,
            TenantCount = request.Counts.TenantCount,
            WorkflowCount = request.Counts.WorkflowCount,
            CumulativeConfiguredPipelineRunCount = request.Counts.CumulativeConfiguredPipelineRunCount,
            CumulativeRuntimeWorkflowRunCount = request.Counts.CumulativeRuntimeWorkflowRunCount,
            ProcessedRecordsThisMonth = request.Counts.ProcessedRecordsThisMonth,
        });

        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new CheckInResponse(
            "ok",
            request.InstallationId,
            verification.CustomerName,
            verification.Edition,
            receivedAtUtc));
    }
}
