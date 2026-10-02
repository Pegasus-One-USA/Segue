using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests;

[CollectionDefinition("ApiTests")]
public sealed class ApiTestCollection : ICollectionFixture<ApiFixture> { }

/// <summary>
/// Shared fixture: authenticates as the auto-seeded single-org SuperAdmin once
/// (no tenant registration), then exposes pre-built state used by all test classes
/// in the "ApiTests" collection.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private readonly ApiFactory _factory = new();

    // ── Shared clients ──────────────────────────────────────────────────────────
    public HttpClient AdminClient { get; private set; } = null!;
    public HttpClient AnonClient  { get; private set; } = null!;

    // ── Identity state ──────────────────────────────────────────────────────────
    public Guid   AdminUserId { get; private set; }
    public string AdminJwt    { get; private set; } = "";
    public string RefreshToken { get; private set; } = "";

    /// <summary>The SuperAdmin's TOTP secret: every account must enroll in two-factor before it can use the API, so
    /// the fixture enrolls the admin and later logins need a code (<see cref="TestHelpers.TestTotp"/>).</summary>
    public string AdminMfaSecret { get; private set; } = "";

    // ── Role state ──────────────────────────────────────────────────────────────
    public Guid TestRoleId      { get; private set; }   // general-purpose role
    public Guid RoleToDeleteId  { get; private set; }   // used only by Delete test
    public Guid PermissionId    { get; private set; }   // first seeded permission

    // ── User state ──────────────────────────────────────────────────────────────
    public Guid   InvitedUserId   { get; private set; }
    public string InvitationToken { get; private set; } = "";
    public Guid   UserToDeleteId  { get; private set; }   // used only by Delete test
    public Guid   PwChangeUserId  { get; private set; }   // used only by ChangePassword test
    public string PwChangeUserEmail    { get; } = "pwchange@testhospital.test";
    public string PwChangeUserPassword { get; } = "PwChange@Test123!";

    // ── Token state ─────────────────────────────────────────────────────────────
    public string ResetToken { get; private set; } = "";

    // ── Constants ───────────────────────────────────────────────────────────────
    public const string AdminEmail    = "admin@testhospital.test";
    public const string AdminPassword = "Admin@Test1234!";

    public HttpClient CreateAuthenticatedClient(string jwt)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    // ── Shared helpers for permission-enforcement tests ──────────────────────────
    public const string TestUserPassword = "LowPriv@Test123!";

    public async Task<Guid> GetPermissionIdByNameAsync(string name)
    {
        var resp = await AdminClient.GetAsync("/api/v1/permissions");
        await EnsureOkAsync(resp);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        foreach (var permission in doc.EnumerateArray())
        {
            if (string.Equals(permission.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                return permission.GetProperty("id").GetGuid();
        }

        throw new InvalidOperationException($"Permission '{name}' not found in the seeded catalog.");
    }

    public async Task<string> LoginAsync(string email, string password = TestUserPassword)
    {
        var resp = await AnonClient.PostAsJsonAsync("/api/v1/auth/internal/login", new
        {
            Email    = email,
            Password = password
        });
        await EnsureOkAsync(resp);
        return SessionTokens(resp).Access
            ?? throw new InvalidOperationException("Login returned no access token.");
    }

    /// <summary>
    /// The session tokens a login-style response issued. The API sets them as HttpOnly cookies and strips them from
    /// the JSON body (AuthController.IssueTokenCookiesAndStrip); a body copy, when an endpoint still returns one,
    /// wins. Tests send the access token back as a Bearer header, which the API still accepts and which is exempt
    /// from the cookie CSRF check.
    /// </summary>
    public static (string? Access, string? Refresh) SessionTokens(HttpResponseMessage response, string? body = null)
    {
        string? FromBody(string property)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        string? FromCookie(string name)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var cookies)) return null;
            var cookie = cookies.FirstOrDefault(c => c.StartsWith(name + "=", StringComparison.Ordinal));
            var value = cookie?.Split(';')[0][(name.Length + 1)..];
            return string.IsNullOrEmpty(value) ? null : Uri.UnescapeDataString(value);
        }

        return (FromBody("accessToken") ?? FromCookie("fhirbridge_access_token"),
                FromBody("refreshToken") ?? FromCookie("fhirbridge_refresh_token"));
    }

    /// <summary>Creates a user whose only role grants exactly the given permissions, and logs them in.</summary>
    public async Task<(Guid UserId, string Email, string Jwt)> CreateUserWithPermissionsAndLoginAsync(params string[] permissionNames)
    {
        var permissionIds = new Guid[permissionNames.Length];
        for (var i = 0; i < permissionNames.Length; i++)
        {
            permissionIds[i] = await GetPermissionIdByNameAsync(permissionNames[i]);
        }

        var roleResp = await AdminClient.PostAsJsonAsync("/api/v1/roles", new
        {
            Name          = $"TestRole-{Guid.NewGuid():N}",
            Description   = "Role created by an integration test",
            PermissionIds = permissionIds
        });
        await EnsureOkAsync(roleResp);
        var roleName = JsonDocument.Parse(await roleResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("name").GetString()!;

        var email = $"test-{Guid.NewGuid():N}@testhospital.test";
        var userResp = await AdminClient.PostAsJsonAsync("/api/v1/users", new
        {
            Email                 = email,
            DisplayName           = "Integration Test User",
            Password              = TestUserPassword,
            RoleNames             = new[] { roleName },
            RequirePasswordChange = false,
            RequireMfa            = false
        });
        await EnsureOkAsync(userResp);
        var userId = JsonDocument.Parse(await userResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var jwt = await LoginAsync(email);
        return (userId, email, jwt);
    }

    /// <summary>Password login for an account enrolled in two-factor: the first call returns a challenge, the second
    /// answers it with a code. The code for the NEXT 30-second step is tried when the current one is refused,
    /// because the same code cannot be used twice (enrollment may just have used it).</summary>
    public async Task<(string Access, string? Refresh)> LoginWithMfaAsync(string email, string password, string secret)
    {
        var first = await AnonClient.PostAsJsonAsync("/api/v1/auth/internal/login", new { Email = email, Password = password });
        await EnsureOkAsync(first);
        var challenge = JsonDocument.Parse(await first.Content.ReadAsStringAsync())
            .RootElement.GetProperty("mfaChallengeToken").GetString()!;

        HttpResponseMessage? answer = null;
        for (var attempt = 0; attempt < 3 && answer?.IsSuccessStatusCode != true; attempt++)
        {
            if (attempt == 2)
            {
                // Both the current and next codes were refused (already used by earlier logins this window):
                // wait for a fresh 30-second step.
                await Task.Delay(TimeSpan.FromSeconds(31 - (DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 30)));
            }

            answer = await AnonClient.PostAsJsonAsync("/api/v1/auth/internal/login/mfa", new
            {
                ChallengeToken = challenge,
                Code           = TestHelpers.TestTotp.Code(secret, attempt == 1 ? DateTimeOffset.UtcNow.AddSeconds(30) : null)
            });
        }

        await EnsureOkAsync(answer!);
        var tokens = SessionTokens(answer!, await answer!.Content.ReadAsStringAsync());
        return (tokens.Access ?? throw new InvalidOperationException("MFA login returned no access token."), tokens.Refresh);
    }

    /// <summary>EnsureSuccessStatusCode, but saying what the API answered: every test depends on this setup.</summary>
    private static async Task EnsureOkAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery} returned " +
                $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    // ── Setup ───────────────────────────────────────────────────────────────────
    public async Task InitializeAsync()
    {
        AnonClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        // 1. First-run setup: no user is seeded, so create the sole SuperAdmin via the anonymous,
        //    one-shot setup endpoint. It creates the user (assigning the bootstrapped SuperAdmin role)
        //    and returns a signed-in session — the same response shape as internal/login.
        var loginResp = await AnonClient.PostAsJsonAsync("/api/v1/auth/setup-superadmin", new
        {
            Email       = AdminEmail,
            DisplayName = "Test Admin",
            Password    = AdminPassword,
            // First-run setup now also accepts the terms and stores outbound SMTP settings in the same step.
            // Nothing is sent: no test triggers an email to a real address.
            AcceptTerms   = true,
            EmailSettings = new
            {
                Host        = "localhost",
                Port        = 25,
                EnableSsl   = false,
                Username    = "smtp-test",
                Password    = "smtp-test",
                FromAddress = "noreply@testhospital.test",
                FromName    = "FHIRBridge Integration Tests"
            }
        });
        if (!loginResp.IsSuccessStatusCode)
        {
            // Say why: every test in the suite depends on this one call.
            throw new InvalidOperationException(
                $"First-run SuperAdmin setup failed with {(int)loginResp.StatusCode}: {await loginResp.Content.ReadAsStringAsync()}");
        }

        var loginBody = await loginResp.Content.ReadAsStringAsync();
        var login = JsonDocument.Parse(loginBody).RootElement;
        var tokens = SessionTokens(loginResp, loginBody);
        var setupJwt = tokens.Access ?? throw new InvalidOperationException("First-run setup returned no access token.");
        AdminUserId  = login.GetProperty("profile").GetProperty("userId").GetGuid();

        // 1b. The first SuperAdmin must enroll in two-factor before anything else is allowed (the session gate
        //     answers 403 "Two-factor authentication setup is required"). Enroll and confirm with the setup
        //     session, then sign in again with a code, which yields a session without that requirement.
        using (var setupClient = CreateAuthenticatedClient(setupJwt))
        {
            var enrollResp = await setupClient.PostAsync("/api/v1/auth/mfa/enroll", null);
            await EnsureOkAsync(enrollResp);
            AdminMfaSecret = JsonDocument.Parse(await enrollResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("secret").GetString()!;
            var verifyResp = await setupClient.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { Code = TestHelpers.TestTotp.Code(AdminMfaSecret) });
            await EnsureOkAsync(verifyResp);
        }

        var adminSession = await LoginWithMfaAsync(AdminEmail, AdminPassword, AdminMfaSecret);
        AdminJwt     = adminSession.Access;
        RefreshToken = adminSession.Refresh ?? "";

        AdminClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        AdminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminJwt);

        // 2. Get first permission ID (used for role creation tests).
        var permsResp = await AdminClient.GetAsync("/api/v1/permissions");
        await EnsureOkAsync(permsResp);
        var perms = JsonDocument.Parse(await permsResp.Content.ReadAsStringAsync()).RootElement;
        PermissionId = perms[0].GetProperty("id").GetGuid();

        // 3. Create general-purpose test role.
        var crResp = await AdminClient.PostAsJsonAsync("/api/v1/roles", new
        {
            Name        = "IntegrationTestRole",
            Description = "Role created by integration test suite",
            PermissionIds = new[] { PermissionId }
        });
        await EnsureOkAsync(crResp);
        TestRoleId = JsonDocument.Parse(await crResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // 4. Create role specifically for DELETE test.
        var drResp = await AdminClient.PostAsJsonAsync("/api/v1/roles", new
        {
            Name        = "RoleToDelete",
            Description = "Will be deleted during integration tests",
            PermissionIds = Array.Empty<Guid>()
        });
        await EnsureOkAsync(drResp);
        RoleToDeleteId = JsonDocument.Parse(await drResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // 4b. The tests assign users the "Operations" role. The in-memory store seeds it, but a fresh database
        //     (FHIRBRIDGE_IT_DB mode) seeds only SuperAdmin, so create it there.
        var rolesResp = await AdminClient.GetAsync("/api/v1/roles");
        await EnsureOkAsync(rolesResp);
        var rolesJson = JsonDocument.Parse(await rolesResp.Content.ReadAsStringAsync()).RootElement;
        var roleItems = rolesJson.ValueKind == JsonValueKind.Array
            ? rolesJson
            : rolesJson.TryGetProperty("items", out var itemsProp) ? itemsProp : rolesJson;
        var hasOperations = roleItems.ValueKind == JsonValueKind.Array && roleItems.EnumerateArray().Any(r =>
            r.TryGetProperty("name", out var n) && string.Equals(n.GetString(), "Operations", StringComparison.OrdinalIgnoreCase));
        if (!hasOperations)
        {
            var opResp = await AdminClient.PostAsJsonAsync("/api/v1/roles", new
            {
                Name          = "Operations",
                Description   = "Created by the integration suite on an empty database",
                PermissionIds = Array.Empty<Guid>()
            });
            await EnsureOkAsync(opResp);
        }

        // 5. Invite a test user.
        var invResp = await AdminClient.PostAsJsonAsync("/api/v1/users/invite", new
        {
            Email     = "invited@testhospital.test",
            RoleId    = TestRoleId,
            FirstName = "Invited",
            LastName  = "User"
        });
        await EnsureOkAsync(invResp);
        var inv = JsonDocument.Parse(await invResp.Content.ReadAsStringAsync()).RootElement;
        InvitedUserId = inv.GetProperty("id").GetGuid();
        if (inv.TryGetProperty("invitationToken", out var itProp) && itProp.ValueKind != JsonValueKind.Null)
            InvitationToken = itProp.GetString() ?? "";

        // 6. Create a user dedicated to the DELETE test.
        var duResp = await AdminClient.PostAsJsonAsync("/api/v1/users", new
        {
            Email                 = "tobedeleted@testhospital.test",
            DisplayName           = "To Be Deleted",
            Password              = "Delete@Test123!",
            RoleNames             = new[] { "Operations" },
            RequirePasswordChange = false,
            RequireMfa            = false
        });
        await EnsureOkAsync(duResp);
        UserToDeleteId = JsonDocument.Parse(await duResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // 7. Create a user dedicated to the ChangePassword test.
        var pwResp = await AdminClient.PostAsJsonAsync("/api/v1/users", new
        {
            Email                 = PwChangeUserEmail,
            DisplayName           = "PwChange User",
            Password              = PwChangeUserPassword,
            RoleNames             = new[] { "Operations" },
            RequirePasswordChange = false,
            RequireMfa            = false
        });
        await EnsureOkAsync(pwResp);
        PwChangeUserId = JsonDocument.Parse(await pwResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // 8. Trigger forgot-password to capture reset token (ExposeResetTokens=true in test config).
        var fpResp = await AnonClient.PostAsJsonAsync("/api/v1/auth/internal/forgot-password", new
        {
            Email = AdminEmail
        });
        if (fpResp.IsSuccessStatusCode)
        {
            var fp = JsonDocument.Parse(await fpResp.Content.ReadAsStringAsync()).RootElement;
            if (fp.TryGetProperty("resetToken", out var rstProp) && rstProp.ValueKind != JsonValueKind.Null)
                ResetToken = rstProp.GetString() ?? "";
        }
    }

    public async Task DisposeAsync()
    {
        AdminClient?.Dispose();
        AnonClient?.Dispose();
        await _factory.DisposeAsync();
    }
}
