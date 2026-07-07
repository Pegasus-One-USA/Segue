using System.Text;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Infrastructure.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Runtime.UnitTests.Auth;

public sealed class DistributedFhirAuthorizationCodeTokenStoreTests
{
    [Fact]
    public async Task SaveAsync_protects_the_cached_token_payload()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = new DistributedFhirAuthorizationCodeTokenStore(cache, new PrefixDataProtectionProvider());
        var token = new StoredOAuthToken(
            "access-token",
            "refresh-token",
            DateTimeOffset.UtcNow.AddMinutes(5),
            "openid fhirUser",
            "patient-1",
            "https://auth.example.com/token");

        await store.SaveAsync("epic|source-1", token, CancellationToken.None);

        var raw = await cache.GetStringAsync("oauth-token:epic|source-1", CancellationToken.None);
        raw.Should().NotBeNull();
        raw.Should().NotStartWith("{");
        raw.Should().NotContain("access-token").And.NotContain("refresh-token").And.NotContain("patient-1");

        var roundTripped = await store.GetAsync("epic|source-1", CancellationToken.None);
        roundTripped.Should().BeEquivalentTo(token);
    }

    private sealed class PrefixDataProtectionProvider : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => new PrefixDataProtector();
    }

    private sealed class PrefixDataProtector : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;

        public byte[] Protect(byte[] plaintext)
        {
            var payload = Convert.ToBase64String(plaintext);
            return Encoding.UTF8.GetBytes("protected:" + payload);
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            var value = Encoding.UTF8.GetString(protectedData);
            if (!value.StartsWith("protected:", StringComparison.Ordinal))
            {
                throw new System.Security.Cryptography.CryptographicException("Payload is not protected.");
            }

            return Convert.FromBase64String(value["protected:".Length..]);
        }
    }
}
