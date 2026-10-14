using System.Net;
using System.Text.Json;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>A failed PingOne call, keeping the step it failed at and PingOne's own (non-secret) error text.</summary>
internal sealed class PingApiException : Exception
{
    public string Step { get; }
    public HttpStatusCode? Status { get; }
    public string? Detail { get; }

    private PingApiException(string step, HttpStatusCode status, string? detail)
        : base($"PingOne {step} call failed with HTTP {(int)status}.")
    {
        Step = step;
        Status = status;
        Detail = detail;
    }

    public static async Task<PingApiException> FromAsync(string step, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? detail = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            detail = root.TryGetProperty("error_description", out var d) ? d.GetString()
                : root.TryGetProperty("message", out var m) ? m.GetString()
                : root.TryGetProperty("error", out var e) ? e.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not JSON: no detail to show.
        }

        return new PingApiException(step, response.StatusCode, detail is { Length: > 300 } ? detail[..300] : detail);
    }

    public string ToAdminMessage()
    {
        var detail = string.IsNullOrWhiteSpace(Detail) ? string.Empty : $" PingOne said: \"{Detail}\"";
        return (Step, Status) switch
        {
            ("token", HttpStatusCode.Unauthorized) =>
                "PingOne rejected the worker app's client ID or secret at the token endpoint. Check that the secret belongs to the " +
                "Worker app with this client ID, the app is enabled, and the Auth Domain/Environment ID match where the app was created." + detail,
            ("token", HttpStatusCode.BadRequest) =>
                "PingOne rejected the token request. The Worker app may not allow the client-credentials grant." + detail,
            ("token", HttpStatusCode.NotFound) =>
                "PingOne environment not found at the token endpoint. Check the Environment ID and the Auth Domain region." + detail,
            ("api", HttpStatusCode.Unauthorized) =>
                "A token was issued but the Management API did not accept it. Check that the API Domain is in the same region as the Auth Domain." + detail,
            ("api", HttpStatusCode.Forbidden) =>
                "The worker app is authenticated but not allowed to read groups. Assign it a role such as Identity Data Read Only on its Roles tab." + detail,
            ("api", HttpStatusCode.NotFound) =>
                "The Management API did not find this environment. Check the Environment ID and the API Domain region." + detail,
            _ => $"PingOne returned HTTP {(int?)Status} at the {Step} step." + detail
        };
    }
}
