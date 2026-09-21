using System.Net.Http.Json;
using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities.Licensing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Licensing;

/// <summary>Body posted to the licensor's intake endpoint — mirrors <see cref="LicenseRequestPayload"/>
/// (the same shape used for the manual-fallback blob) field for field, since both paths carry identical
/// information.</summary>
internal sealed record LicenseRequestIntakeBody(
    string ClientName, string Email, string? CompanyName, string? Address, string PhoneNumber, string UniqueKey,
    string? RequestHost);

public sealed class LicenseRequestService : ILicenseRequestService
{
    /// <summary>Fixed path appended to the licensor's base URL — the base itself is the only part an
    /// admin edits.</summary>
    private const string IntakePath = "/api/license-requests";

    private readonly ILicenseRequestRepository _repository;
    private readonly ISystemSettingsCache _settingsCache;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LicenseRequestService> _logger;

    public LicenseRequestService(
        ILicenseRequestRepository repository,
        ISystemSettingsCache settingsCache,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<LicenseRequestService> logger)
    {
        _repository = repository;
        _settingsCache = settingsCache;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LicenseRequestStatusResult>> ListAsync(CancellationToken cancellationToken)
    {
        var requests = await _repository.ListAsync(cancellationToken);
        return requests.Select(ToResult).ToList();
    }

    public async Task<LicenseRequestStatusResult> CreateAndSubmitAsync(
        LicenseRequestInput input, string? requestHost, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.ClientName) || string.IsNullOrWhiteSpace(input.Email)
            || string.IsNullOrWhiteSpace(input.PhoneNumber))
        {
            throw new InvalidOperationException("Client name, email, and phone number are required.");
        }

        // 32 random bytes, hex-encoded (64 chars) — long enough that guessing another install's key is
        // infeasible, short enough to type/paste by hand if ever needed for support.
        var uniqueKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        var request = new LicenseRequest(
            Guid.NewGuid(), input.ClientName.Trim(), input.Email.Trim(),
            string.IsNullOrWhiteSpace(input.CompanyName) ? null : input.CompanyName.Trim(),
            string.IsNullOrWhiteSpace(input.Address) ? null : input.Address.Trim(),
            input.PhoneNumber.Trim(), uniqueKey, DateTime.UtcNow,
            string.IsNullOrWhiteSpace(requestHost) ? null : requestHost.Trim());

        await _repository.AddAsync(request, cancellationToken);
        await AttemptSubmitAsync(request, cancellationToken);

        return ToResult(request);
    }

    public async Task<LicenseRequestStatusResult> UpdateAsync(
        Guid id, LicenseRequestInput input, string? requestHost, CancellationToken cancellationToken)
    {
        var request = await _repository.GetAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("That license request could not be found.");

        if (string.IsNullOrWhiteSpace(input.ClientName) || string.IsNullOrWhiteSpace(input.Email)
            || string.IsNullOrWhiteSpace(input.PhoneNumber))
        {
            throw new InvalidOperationException("Client name, email, and phone number are required.");
        }

        request.UpdateDetails(
            input.ClientName.Trim(), input.Email.Trim(),
            string.IsNullOrWhiteSpace(input.CompanyName) ? null : input.CompanyName.Trim(),
            string.IsNullOrWhiteSpace(input.Address) ? null : input.Address.Trim(),
            input.PhoneNumber.Trim());
        request.Resubmit(requestHost);
        await AttemptSubmitAsync(request, cancellationToken);

        return ToResult(request);
    }

    private async Task AttemptSubmitAsync(LicenseRequest request, CancellationToken cancellationToken)
    {
        var attemptedUtc = DateTime.UtcNow;
        string baseUrl;
        try
        {
            baseUrl = await ResolveLicensorApplicationUrlAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // No licensor URL configured yet — a normal, recoverable setup state, not a network/DNS
            // failure, but it still has to land in Failed (with the fallback blob) rather than
            // propagating: AddAsync already committed this request row by the time we get here, so
            // letting this throw would leave it stuck Pending forever with no fallback blob and no way
            // to recover. Marking Failed here instead means: fallback blob available immediately, and
            // once an admin sets the URL, editing (or submitting a fresh request) resolves it and
            // proceeds normally.
            _logger.LogWarning(ex, "License request {RequestId} could not be submitted: {Message}", request.Id, ex.Message);
            request.MarkFailed(attemptedUtc, ex.Message);
            await _repository.SaveAsync(request, cancellationToken);
            return;
        }

        var host = DisplayHostOf(baseUrl);
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(LicenseRequestService));
            var response = await client.PostAsJsonAsync(
                $"{baseUrl}{IntakePath}",
                new LicenseRequestIntakeBody(
                    request.ClientName, request.Email, request.CompanyName, request.Address,
                    request.PhoneNumber, request.UniqueKey, request.RequestHost),
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                request.MarkSubmitted(attemptedUtc);
            }
            else
            {
                request.MarkFailed(attemptedUtc, $"Licensor at {host} responded with {(int)response.StatusCode}.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            // Network/DNS/timeout/malformed-URL — exactly the case the manual-fallback blob exists for.
            // Logged for the operator's own diagnostics; never surfaced as an error to the caller, since a
            // Failed status (with the fallback blob) is a normal, expected outcome here, not a bug.
            _logger.LogWarning(
                ex, "Direct license request submission failed; falling back to manual sharing for request {RequestId}.",
                request.Id);
            request.MarkFailed(attemptedUtc, $"Could not reach the licensor's application at {host}.");
        }

        await _repository.SaveAsync(request, cancellationToken);
    }

    private async Task<string> ResolveLicensorApplicationUrlAsync(CancellationToken cancellationToken)
    {
        var configured = await _settingsCache.GetStringAsync(
            LicenseRequestSettingKeys.LicensorApplicationUrl,
            _configuration[LicenseRequestSettingKeys.LicensorApplicationUrl] ?? string.Empty,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "No licensor application URL is configured — set License:LicensorApplicationUrl.");
        }

        return configured.TrimEnd('/');
    }

    /// <summary>Just the host (e.g. "license.pegasusone.com") for a submission-failure message — shorter
    /// and more readable than the full URL, and still enough for an operator to recognize whether
    /// License:LicensorApplicationUrl is pointed at the wrong place. Falls back to the raw value if it
    /// somehow isn't a well-formed absolute URI (SystemSettingsService validates this on save, but a
    /// value written directly to the database could still bypass that).</summary>
    private static string DisplayHostOf(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : baseUrl;

    private static LicenseRequestStatusResult ToResult(LicenseRequest request)
    {
        var encodedPayload = request.Status == LicenseRequestStatus.Failed
            ? LicenseRequestPayloadEncoder.Encode(new LicenseRequestPayload(
                request.ClientName, request.Email, request.CompanyName, request.Address, request.PhoneNumber,
                request.UniqueKey, request.RequestHost))
            : null;

        return new LicenseRequestStatusResult(
            request.Id, request.ClientName, request.Email, request.CompanyName, request.Address, request.PhoneNumber,
            request.Status.ToString(), request.CreatedUtc, request.LastAttemptUtc, request.SubmissionError,
            encodedPayload, request.RequestHost);
    }
}
