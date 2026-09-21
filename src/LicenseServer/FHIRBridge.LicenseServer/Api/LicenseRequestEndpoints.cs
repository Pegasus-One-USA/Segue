using FHIRBridge.LicenseServer.Data;
using FHIRBridge.LicenseServer.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.LicenseServer.Api;

/// <summary>
/// Maps <c>POST /api/license-requests</c> — the endpoint a customer install's own License Request screen
/// calls directly (see the main repo's <c>LicenseRequestService.AttemptSubmitAsync</c>). Anonymous by
/// design, same as <see cref="CheckInEndpoints"/>: a deployed instance has no admin session here.
/// </summary>
public static class LicenseRequestEndpoints
{
    public static IEndpointRouteBuilder MapLicenseRequestApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/license-requests", HandleIntakeAsync)
            .AllowAnonymous()
            .WithName("LicenseRequestIntake");

        return app;
    }

    private static async Task<IResult> HandleIntakeAsync(
        [FromBody] LicenseRequestIntakeBody request,
        LicenseServerDbContext db,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClientName)
            || string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(request.PhoneNumber)
            || string.IsNullOrWhiteSpace(request.UniqueKey))
        {
            return Results.BadRequest(new LicenseRequestIntakeError(
                "clientName, email, phoneNumber and uniqueKey are required."));
        }

        await UpsertLicenseRequestAsync(db, request, receivedManually: false, cancellationToken);

        logger.LogInformation(
            "License request received from {ClientName} <{Email}> (key {UniqueKey}).",
            request.ClientName, request.Email, request.UniqueKey);

        return Results.Ok();
    }

    /// <summary>Shared by the direct API call above and the manual-paste decode flow
    /// (Pages/LicenseRequests/Index.cshtml.cs) — both ultimately carry the exact same fields, and both
    /// upsert by <see cref="LicenseRequest.UniqueKey"/> rather than inserting fresh, since a resubmission
    /// (or a re-paste of the same code) carries the same key as the original ask.</summary>
    public static async Task UpsertLicenseRequestAsync(
        LicenseServerDbContext db, LicenseRequestIntakeBody request, bool receivedManually,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var existing = await db.LicenseRequests
            .FirstOrDefaultAsync(r => r.UniqueKey == request.UniqueKey, cancellationToken);

        if (existing is null)
        {
            db.LicenseRequests.Add(new LicenseRequest
            {
                Id = Guid.NewGuid(),
                ClientName = request.ClientName.Trim(),
                Email = request.Email.Trim(),
                CompanyName = string.IsNullOrWhiteSpace(request.CompanyName) ? null : request.CompanyName.Trim(),
                Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim(),
                PhoneNumber = request.PhoneNumber.Trim(),
                UniqueKey = request.UniqueKey,
                RequestHost = string.IsNullOrWhiteSpace(request.RequestHost) ? null : request.RequestHost.Trim(),
                ReceivedAtUtc = nowUtc,
                LastSubmittedUtc = nowUtc,
                SubmissionCount = 1,
                ReceivedManually = receivedManually,
            });
        }
        else
        {
            // Same install, same key — a resend (or a renewal, once the previous ask was already
            // fulfilled). Contact details can't change on the requesting side either (see the main repo's
            // LicenseRequest.Resubmit), but refresh them anyway in case this row was created from an older
            // manual paste with a typo since corrected via the direct call.
            existing.ClientName = request.ClientName.Trim();
            existing.Email = request.Email.Trim();
            existing.CompanyName = string.IsNullOrWhiteSpace(request.CompanyName) ? null : request.CompanyName.Trim();
            existing.Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();
            existing.PhoneNumber = request.PhoneNumber.Trim();
            // Refreshed every time a value is supplied (same as the other contact fields above) — this is
            // the admin's current browser origin, not an identity anchor like UniqueKey, so a later
            // resubmission correcting an earlier wrong/missing value is expected, not a bug. A resubmit
            // that doesn't supply one (e.g. a non-browser caller) leaves whatever's already stored alone.
            if (!string.IsNullOrWhiteSpace(request.RequestHost))
            {
                existing.RequestHost = request.RequestHost.Trim();
            }
            existing.LastSubmittedUtc = nowUtc;
            existing.SubmissionCount++;

            // A resend is a fresh ask, even if the earlier one was already minted or denied — without
            // this, a renewal request silently vanished into an already-decided row instead of
            // reappearing as something that needs a new look on /LicenseRequests.
            existing.FulfilledAtUtc = null;
            existing.FulfilledIssuedLicenseId = null;
            existing.DeniedAtUtc = null;
            existing.DenialReason = null;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
