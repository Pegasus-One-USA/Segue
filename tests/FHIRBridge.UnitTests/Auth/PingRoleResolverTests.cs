using System.Net;
using System.Text;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace FHIRBridge.UnitTests.Auth;

public sealed class PingRoleResolverTests
{
    private const string Env = "11111111-2222-3333-4444-555555555555";

    private readonly List<string> _requestedUrls = [];
    private readonly Dictionary<string, string> _responses = new();

    private static PingAuthenticationOptions Options(bool applyToSaml = true) => new()
    {
        Enabled = true,
        EnvironmentId = Env,
        ClientId = "portal-client",
        WorkerClientId = "worker",
        WorkerClientSecretKeyVaultName = "kv",
        WorkerClientSecretName = "ping-worker",
        ApplyToSaml = applyToSaml,
        GroupRoleMappings = new Dictionary<string, string[]>
        {
            ["group-admins"] = ["Admin"],
            ["Clinical Viewers"] = ["Viewer"]
        }
    };

    private PingRoleResolver Resolver(PingAuthenticationOptions options)
    {
        var settingsCache = new Mock<ISystemSettingsCache>();
        settingsCache.Setup(s => s.GetBoolAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, bool d, CancellationToken _) => d);
        settingsCache.Setup(s => s.GetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string d, CancellationToken _) => d);

        var secrets = new Mock<ISecretProvider>();
        secrets.Setup(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("worker-secret");

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(PingManagementClient.HttpClientName))
            .Returns(() => new HttpClient(new StubHandler(_requestedUrls, _responses)));

        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        var client = new PingManagementClient(
            secrets.Object,
            factory.Object,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<PingManagementClient>.Instance);
        return new PingRoleResolver(
            new PingSettingsReader(wrapped, settingsCache.Object),
            client,
            NullLogger<PingRoleResolver>.Instance);
    }

    private void StubTokenAndGroups(string userId, string groupsJson)
    {
        _responses[$"https://auth.pingone.com/{Env}/as/token"] = """{"access_token":"tok","expires_in":3600}""";
        _responses[$"https://api.pingone.com/v1/environments/{Env}/users/{userId}/memberOfGroups"] = groupsJson;
    }

    [Fact]
    public async Task Maps_ping_groups_by_id_and_name_to_fhirbridge_roles()
    {
        StubTokenAndGroups("u1", """
            {"_embedded":{"memberOfGroups":[{"id":"group-admins","name":"Platform"},{"id":"x","name":"clinical viewers"}]}}
            """);

        var result = await Resolver(Options()).ResolveAsync(
            new ExternalIdentity(LoginProvider.Ping, "u1", "a@b.c", null), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.RoleNames.Should().BeEquivalentTo("Admin", "Viewer");
    }

    [Fact]
    public async Task Roles_come_back_in_mapping_order_regardless_of_the_order_ping_lists_the_groups()
    {
        // Ping lists Clinical Viewers first, but the Admin mapping is higher in the configured list.
        StubTokenAndGroups("u1", """
            {"_embedded":{"memberOfGroups":[{"id":"x","name":"Clinical Viewers"},{"id":"group-admins","name":"Platform"}]}}
            """);

        var result = await Resolver(Options()).ResolveAsync(
            new ExternalIdentity(LoginProvider.Ping, "u1", "a@b.c", null), CancellationToken.None);

        result.RoleNames.Should().Equal("Admin", "Viewer");
    }

    [Fact]
    public async Task Reads_the_groupMemberships_array_pingone_documents()
    {
        StubTokenAndGroups("u1", """
            {"_embedded":{"groupMemberships":[{"id":"group-admins","name":"Platform","type":"DIRECT"}]}}
            """);

        var result = await Resolver(Options()).ResolveAsync(
            new ExternalIdentity(LoginProvider.Ping, "u1", "a@b.c", null), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.RoleNames.Should().Equal("Admin");
    }

    [Fact]
    public async Task Unmapped_groups_yield_no_roles()
    {
        StubTokenAndGroups("u1", """{"_embedded":{"memberOfGroups":[{"id":"other","name":"Other"}]}}""");

        var result = await Resolver(Options()).ResolveAsync(
            new ExternalIdentity(LoginProvider.Ping, "u1", "a@b.c", null), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.RoleNames.Should().BeEmpty();
    }

    [Fact]
    public async Task Api_failure_reports_failed_resolution_instead_of_throwing()
    {
        // No stubbed responses: the token request returns 404.
        var result = await Resolver(Options()).ResolveAsync(
            new ExternalIdentity(LoginProvider.Ping, "u1", "a@b.c", null), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_pagination_link_to_a_foreign_host()
    {
        StubTokenAndGroups("u1", """
            {"_embedded":{"memberOfGroups":[]},"_links":{"next":{"href":"https://evil.example/steal"}}}
            """);

        var result = await Resolver(Options()).ResolveAsync(
            new ExternalIdentity(LoginProvider.Ping, "u1", "a@b.c", null), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        _requestedUrls.Should().NotContain(u => u.Contains("evil.example"));
    }

    [Fact]
    public async Task Saml_login_locates_the_ping_user_by_email_before_reading_groups()
    {
        _responses[$"https://auth.pingone.com/{Env}/as/token"] = """{"access_token":"tok","expires_in":3600}""";
        _responses[$"https://api.pingone.com/v1/environments/{Env}/users?filter=email%20eq%20%22a%40b.c%22&limit=2"] =
            """{"_embedded":{"users":[{"id":"u9"}]}}""";
        _responses[$"https://api.pingone.com/v1/environments/{Env}/users/u9/memberOfGroups"] =
            """{"_embedded":{"memberOfGroups":[{"id":"group-admins","name":"Platform"}]}}""";

        var result = await Resolver(Options()).ResolveAsync(
            new ExternalIdentity(LoginProvider.Saml, "nameid", "a@b.c", null), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.RoleNames.Should().BeEquivalentTo("Admin");
    }

    [Theory]
    [InlineData(LoginProvider.Ping, true, true)]
    [InlineData(LoginProvider.Saml, true, true)]
    [InlineData(LoginProvider.Saml, false, false)]
    [InlineData(LoginProvider.Google, true, false)]
    public async Task Applies_only_to_ping_and_configured_saml_logins(LoginProvider provider, bool applyToSaml, bool expected)
    {
        var applies = await Resolver(Options(applyToSaml)).AppliesToAsync(
            new ExternalIdentity(provider, "s", "a@b.c", null), CancellationToken.None);

        applies.Should().Be(expected);
    }

    private sealed class StubHandler(List<string> requested, Dictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            requested.Add(url);

            return Task.FromResult(responses.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
