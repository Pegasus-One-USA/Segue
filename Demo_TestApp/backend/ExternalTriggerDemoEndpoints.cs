using System.Text;
using System.Text.Json;

namespace HealthAppBackend;

public sealed record ListWorkflowsDemoRequest(string FhirBridgeBaseUrl, string ClientId, string ClientSecret, string? ReturnUrl = null);

public sealed record LaunchDemoRequest(
    string FhirBridgeBaseUrl, string ClientId, string ClientSecret, string WorkflowId, string? ReturnUrl,
    string? EhrEndpointCode, string? Mode, string? WindowMode, string? CloseOnComplete);

/// <summary>
/// Server-side proxy for the one read-only call the external-trigger console needs before it can render its
/// workflow picker: <c>POST /api/v1/workflows/external/list</c>. Proxied (not called directly from the
/// browser) for two reasons — the Client Secret stays out of client-side JS entirely, and FHIRBridge's CORS
/// policy has no reason to know about this demo app's origin, since every call it makes to FHIRBridge is
/// server-to-server. The actual trigger action (<see cref="ExternalTriggerConsoleComponent"/>-equivalent
/// Angular page) is NOT proxied — it's a real HTML form POST + full-page navigation, so FHIRBridge sees a
/// genuine Referer and the browser genuinely redirects.
/// </summary>
public static class ExternalTriggerDemoEndpoints
{
    public static void MapExternalTriggerDemoEndpoints(this WebApplication app)
    {
        app.MapPost("/api/external-trigger-demo/list-workflows", async (
            ListWorkflowsDemoRequest request,
            IHttpClientFactory httpClientFactory) =>
        {
            var listUrl = $"{request.FhirBridgeBaseUrl.TrimEnd('/')}/api/v1/workflows/external/list";
            var client = httpClientFactory.CreateClient();

            var payload = JsonSerializer.Serialize(new { client_id = request.ClientId, client_secret = request.ClientSecret, return_url = request.ReturnUrl });

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(listUrl, new StringContent(payload, Encoding.UTF8, "application/json"));
            }
            catch (Exception ex)
            {
                return Results.Ok(new { success = false, error = $"Could not reach FHIRBridge: {ex.Message}", workflows = Array.Empty<object>() });
            }

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return Results.Ok(new { success = false, error = DescribeFailure(response.StatusCode, body), workflows = Array.Empty<object>() });
            }

            return Results.Content(
                $"{{\"success\":true,\"workflows\":{body}}}", "application/json");
        });

        // Same reasoning as list-workflows above, for /api/v1/workflows/external/ehr-endpoints — powers the
        // EHR Endpoint Code dropdown instead of a free-text field the caller has to know the right value for.
        app.MapPost("/api/external-trigger-demo/list-ehr-endpoints", async (
            ListWorkflowsDemoRequest request,
            IHttpClientFactory httpClientFactory) =>
        {
            var listUrl = $"{request.FhirBridgeBaseUrl.TrimEnd('/')}/api/v1/workflows/external/ehr-endpoints";
            var client = httpClientFactory.CreateClient();

            var payload = JsonSerializer.Serialize(new { client_id = request.ClientId, client_secret = request.ClientSecret, return_url = request.ReturnUrl });

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(listUrl, new StringContent(payload, Encoding.UTF8, "application/json"));
            }
            catch (Exception ex)
            {
                return Results.Ok(new { success = false, error = $"Could not reach FHIRBridge: {ex.Message}", ehrEndpoints = Array.Empty<object>() });
            }

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return Results.Ok(new { success = false, error = DescribeFailure(response.StatusCode, body), ehrEndpoints = Array.Empty<object>() });
            }

            return Results.Content(
                $"{{\"success\":true,\"ehrEndpoints\":{body}}}", "application/json");
        });

        // Step 1 of FHIRBridge's secure browser-redirect flow, done SERVER-TO-SERVER: exchange the Client ID/Secret
        // for a single-use launch ticket (POST /workflows/external/launch-ticket) and hand the browser only the
        // resulting run URL. A real integrating app keeps the secret in its own backend configuration; the demo
        // takes it from the console form purely so it can be exercised with different clients.
        app.MapPost("/api/external-trigger-demo/launch", async (
            LaunchDemoRequest request,
            IHttpClientFactory httpClientFactory) =>
        {
            var url = $"{request.FhirBridgeBaseUrl.TrimEnd('/')}/api/v1/workflows/external/launch-ticket";
            var payload = JsonSerializer.Serialize(new
            {
                client_id = request.ClientId,
                client_secret = request.ClientSecret,
                workflow_id = request.WorkflowId,
                return_url = request.ReturnUrl,
                ehr_endpoint_code = request.EhrEndpointCode,
                mode = request.Mode,
                window_mode = request.WindowMode,
                close_on_complete = request.CloseOnComplete,
            });

            HttpResponseMessage response;
            try
            {
                response = await httpClientFactory.CreateClient().PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
            }
            catch (Exception ex)
            {
                return Results.Ok(new { success = false, error = $"Could not reach FHIRBridge: {ex.Message}", runUrl = (string?)null });
            }

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return Results.Ok(new { success = false, error = DescribeFailure(response.StatusCode, body), runUrl = (string?)null });
            }

            using var doc = JsonDocument.Parse(body);
            var runUrl = doc.RootElement.TryGetProperty("run_url", out var r) ? r.GetString() : null;
            return Results.Ok(new { success = runUrl is not null, error = runUrl is null ? "FHIRBridge returned no run_url." : null, runUrl });
        });
    }

    private static string DescribeFailure(System.Net.HttpStatusCode status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error_description", out var d) && d.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (JsonException)
        {
        }

        return $"FHIRBridge returned {(int)status}: {body}";
    }
}
