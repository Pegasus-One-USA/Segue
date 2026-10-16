using System.Text;
using System.Text.Json;

namespace HealthAppBackend;

public sealed record ClientCredentialsDemoRequest(
    string FhirBridgeBaseUrl,
    string WorkflowId,
    string ClientId,
    string ClientSecret);

public sealed record ClientCredentialsDemoStepResult(
    string RequestedUrl,
    int? StatusCode,
    bool Success,
    string? ResponseBody,
    string? Error);

public sealed record ClientCredentialsDemoResult(
    ClientCredentialsDemoStepResult TokenStep,
    string? AccessToken,
    ClientCredentialsDemoStepResult? RunStep);

/// <summary>
/// Exercises FHIRBridge's OAuth 2.0 Client Credentials Grant end to end, server-to-server, exactly the way a
/// real third-party integration would: POST client_id/client_secret to FHIRBridge's own
/// <c>/api/v1/oauth/token</c>, then use the returned Bearer token to call
/// <c>/api/v1/workflows/{workflowId}/run</c>. Deliberately proxied through this backend (never called directly
/// from the browser): the Client Secret never touches client-side JS, and there's no FHIRBridge CORS
/// configuration to fuss with for a demo — the same reasoning /api/workflow/run above already applies.
///
/// Stateless and login-free by design, same as ApiAuthTestEndpoints/ApiEndpointTestEndpoints — this is a
/// developer test harness, not a HealthApp feature behind a role.
/// </summary>
public static class ClientCredentialsDemoEndpoints
{
    public static void MapClientCredentialsDemoEndpoints(this WebApplication app)
    {
        app.MapPost("/api/client-credentials-demo/run", async (
            ClientCredentialsDemoRequest request,
            IHttpClientFactory httpClientFactory) =>
        {
            var baseUrl = request.FhirBridgeBaseUrl.TrimEnd('/');
            var client = httpClientFactory.CreateClient();

            // ── Step 1: exchange client_id/client_secret for a Bearer token ─────────────────────────
            var tokenUrl = $"{baseUrl}/api/v1/oauth/token";
            var tokenPayload = JsonSerializer.Serialize(new
            {
                grant_type = "client_credentials",
                client_id = request.ClientId,
                client_secret = request.ClientSecret,
            });

            string? accessToken = null;
            ClientCredentialsDemoStepResult tokenStep;
            try
            {
                var tokenResponse = await client.PostAsync(
                    tokenUrl, new StringContent(tokenPayload, Encoding.UTF8, "application/json"));
                var tokenBody = await tokenResponse.Content.ReadAsStringAsync();

                if (tokenResponse.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(tokenBody);
                    accessToken = doc.RootElement.TryGetProperty("access_token", out var tokenProp)
                        ? tokenProp.GetString()
                        : null;
                }

                tokenStep = new ClientCredentialsDemoStepResult(
                    tokenUrl,
                    (int)tokenResponse.StatusCode,
                    tokenResponse.IsSuccessStatusCode && accessToken is not null,
                    tokenBody,
                    tokenResponse.IsSuccessStatusCode ? null : "Token request was refused — see ResponseBody.");
            }
            catch (Exception ex)
            {
                tokenStep = new ClientCredentialsDemoStepResult(tokenUrl, null, false, null, $"Could not reach FHIRBridge: {ex.Message}");
            }

            if (accessToken is null)
            {
                // No point calling /run without a token — report the failed token step alone.
                return Results.Ok(new ClientCredentialsDemoResult(tokenStep, null, null));
            }

            // ── Step 2: trigger the workflow with the token just obtained ────────────────────────────
            var runUrl = $"{baseUrl}/api/v1/workflows/{request.WorkflowId}/run";
            ClientCredentialsDemoStepResult runStep;
            try
            {
                using var runRequest = new HttpRequestMessage(HttpMethod.Post, runUrl)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                };
                runRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                var runResponse = await client.SendAsync(runRequest);
                var runBody = await runResponse.Content.ReadAsStringAsync();

                runStep = new ClientCredentialsDemoStepResult(
                    runUrl,
                    (int)runResponse.StatusCode,
                    runResponse.IsSuccessStatusCode,
                    runBody,
                    runResponse.IsSuccessStatusCode ? null : "Workflow run call was refused — see ResponseBody.");
            }
            catch (Exception ex)
            {
                runStep = new ClientCredentialsDemoStepResult(runUrl, null, false, null, $"Could not reach FHIRBridge: {ex.Message}");
            }

            return Results.Ok(new ClientCredentialsDemoResult(tokenStep, accessToken, runStep));
        });
    }
}
