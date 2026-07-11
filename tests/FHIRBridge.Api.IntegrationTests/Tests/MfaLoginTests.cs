using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

[Collection("ApiTests")]
public sealed class MfaLoginTests(ApiFixture f)
{
    // Independent RFC 6238 code generator (HMAC-SHA1, 6 digits, 30s step) so this test doesn't
    // depend on any internal/non-public member of the production TotpService — it only needs to
    // produce a code that TotpService.ValidateCode would also accept for the same secret.
    private static string GenerateTotpCode(string base32Secret, DateTimeOffset? at = null)
    {
        var timestep = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        var key = Base32Decode(base32Secret);
        var counterBytes = BitConverter.GetBytes(timestep);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

        var hash = HMACSHA1.HashData(key, counterBytes);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8)
                     | (hash[offset + 3] & 0xFF);

        return (binary % 1_000_000).ToString().PadLeft(6, '0');
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(input.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in input)
        {
            var value = alphabet.IndexOf(c);
            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }

        return output.ToArray();
    }

    /// <summary>Registers a fresh local-auth user, logs in, and enrolls+confirms TOTP MFA for them.</summary>
    private async Task<(string Email, string Password, string Secret)> CreateMfaEnabledUserAsync()
    {
        var email = $"mfa-{Guid.NewGuid():N}@testhospital.test";
        const string password = "Mfa@Test123!";

        var userResp = await f.AdminClient.PostAsJsonAsync("/api/v1/users", new
        {
            Email = email,
            DisplayName = "MFA Integration User",
            Password = password,
            RoleNames = new[] { "Operations" },
            RequirePasswordChange = false
        });
        userResp.EnsureSuccessStatusCode();

        var jwt = await f.LoginAsync(email, password);
        using var client = f.CreateAuthenticatedClient(jwt);

        var enrollResp = await client.PostAsync("/api/v1/auth/mfa/enroll", null);
        enrollResp.EnsureSuccessStatusCode();
        var enroll = JsonDocument.Parse(await enrollResp.Content.ReadAsStringAsync()).RootElement;
        var secret = enroll.GetProperty("secret").GetString()!;

        var verifyResp = await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new
        {
            Code = GenerateTotpCode(secret)
        });
        verifyResp.EnsureSuccessStatusCode();

        return (email, password, secret);
    }

    [Fact]
    public async Task POST_auth_internal_login__mfa_enabled_account__returns_challenge_not_tokens()
    {
        var (email, password, _) = await CreateMfaEnabledUserAsync();

        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/login", new { Email = email, Password = password });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.True(doc.GetProperty("requiresMfa").GetBoolean());
        Assert.True(doc.TryGetProperty("mfaChallengeToken", out var tokenProp) && tokenProp.ValueKind == JsonValueKind.String);
        Assert.True(doc.TryGetProperty("accessToken", out var accessTokenProp) && accessTokenProp.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task POST_auth_internal_login_mfa__valid_code__completes_login_with_tokens()
    {
        var (email, password, secret) = await CreateMfaEnabledUserAsync();

        var loginResp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/login", new { Email = email, Password = password });
        loginResp.EnsureSuccessStatusCode();
        var challengeToken = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("mfaChallengeToken").GetString()!;

        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/login/mfa", new
        {
            ChallengeToken = challengeToken,
            Code = GenerateTotpCode(secret)
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.False(doc.GetProperty("requiresMfa").GetBoolean());
        Assert.True(doc.TryGetProperty("accessToken", out var accessTokenProp) && accessTokenProp.ValueKind == JsonValueKind.String);

        // Authenticate with the freshly issued access token to confirm the session is genuinely usable.
        using var authed = f.CreateAuthenticatedClient(accessTokenProp.GetString()!);
        var meResp = await authed.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, meResp.StatusCode);
    }

    [Fact]
    public async Task POST_auth_internal_login_mfa__wrong_code__returns_400_and_challenge_stays_usable()
    {
        var (email, password, secret) = await CreateMfaEnabledUserAsync();

        var loginResp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/login", new { Email = email, Password = password });
        loginResp.EnsureSuccessStatusCode();
        var challengeToken = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("mfaChallengeToken").GetString()!;

        var wrongResp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/login/mfa", new
        {
            ChallengeToken = challengeToken,
            Code = "000000"
        });
        Assert.Equal(HttpStatusCode.BadRequest, wrongResp.StatusCode);

        // The same challenge is still usable with the correct code right after — one bad guess doesn't burn it.
        var retryResp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/login/mfa", new
        {
            ChallengeToken = challengeToken,
            Code = GenerateTotpCode(secret)
        });
        Assert.Equal(HttpStatusCode.OK, retryResp.StatusCode);
    }

    [Fact]
    public async Task POST_auth_internal_login_mfa__unknown_challenge_token__returns_400()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/internal/login/mfa", new
        {
            ChallengeToken = "not-a-real-challenge-token",
            Code = "123456"
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
