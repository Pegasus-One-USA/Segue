using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.Blob;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations.Blob;

/// <summary>
/// <see cref="BlobContainerClientFactory"/> only ever *constructs* Azure SDK client objects — no network call
/// happens until a caller invokes an operation on the returned <see cref="BlobContainerClient"/> — so these are
/// genuine, network-free unit tests asserting the constructed client's observable configuration (URI, account
/// name, whether a shared-key credential was attached).
/// </summary>
public sealed class BlobContainerClientFactoryTests
{
    private readonly Mock<ISecretProvider> _secretProvider = new();

    private static DestinationConfiguration Destination(string? target, string? connectionMetadataJson) =>
        new("Blob Export", DestinationType.BlobStorage, new SecretReference("kv", "secret"), target, connectionMetadataJson);

    private BlobContainerClientFactory CreateFactory() =>
        new(_secretProvider.Object, new BlobContainerClientCache(), NullLogger<BlobContainerClientFactory>.Instance);

    private void SecretResolvesTo(string value) =>
        _secretProvider
            .Setup(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(value);

    [Fact]
    public async Task ConnectionString_mode_builds_a_client_pointed_at_the_configured_account_and_container()
    {
        SecretResolvesTo("DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=Zm9v;EndpointSuffix=core.windows.net");
        var destination = Destination("fhir", """{"dest_blobAuthMode":"connectionString"}""");
        var settings = BlobDestinationSettings.Parse(destination);

        var target = await CreateFactory().GetTargetAsync(destination, settings, CancellationToken.None);

        target.Container.AccountName.Should().Be("acct");
        target.Container.Name.Should().Be("fhir");
        target.SupportsContainerCreate.Should().BeTrue();
    }

    [Fact]
    public async Task AccountKey_mode_attaches_a_shared_key_credential()
    {
        SecretResolvesTo("c2VjcmV0a2V5"); // base64, arbitrary — never validated at construction time
        var destination = Destination("fhir", """{"dest_blobAuthMode":"accountKey","dest_blobAccountName":"acct"}""");
        var settings = BlobDestinationSettings.Parse(destination);

        var target = await CreateFactory().GetTargetAsync(destination, settings, CancellationToken.None);

        target.Container.Uri.Should().Be(new Uri("https://acct.blob.core.windows.net/fhir"));
        target.Container.CanGenerateSasUri.Should().BeTrue(); // proves a shared-key credential was attached
        target.SupportsContainerCreate.Should().BeTrue();
    }

    [Fact]
    public async Task SasUrl_mode_with_a_container_scoped_SAS_disables_container_create()
    {
        SecretResolvesTo("https://acct.blob.core.windows.net/fhir?sv=2023-01-01&sig=abc");
        var destination = Destination("fhir", """{"dest_blobAuthMode":"sasUrl"}""");
        var settings = BlobDestinationSettings.Parse(destination);

        var target = await CreateFactory().GetTargetAsync(destination, settings, CancellationToken.None);

        target.Container.Uri.AbsolutePath.Should().Be("/fhir");
        target.SupportsContainerCreate.Should().BeFalse();
    }

    [Fact]
    public async Task SasUrl_mode_with_an_account_scoped_SAS_keeps_container_create_enabled()
    {
        SecretResolvesTo("https://acct.blob.core.windows.net/?sv=2023-01-01&sig=abc");
        var destination = Destination("fhir", """{"dest_blobAuthMode":"sasUrl"}""");
        var settings = BlobDestinationSettings.Parse(destination);

        var target = await CreateFactory().GetTargetAsync(destination, settings, CancellationToken.None);

        target.Container.Name.Should().Be("fhir");
        target.SupportsContainerCreate.Should().BeTrue();
    }

    [Fact]
    public async Task SasUrl_mode_with_a_bare_token_combines_it_with_the_configured_account_url()
    {
        SecretResolvesTo("sv=2023-01-01&sig=abc");
        var destination = Destination(
            "fhir", """{"dest_blobAuthMode":"sasUrl","dest_blobAccountUrl":"https://acct.blob.core.windows.net"}""");
        var settings = BlobDestinationSettings.Parse(destination);

        var target = await CreateFactory().GetTargetAsync(destination, settings, CancellationToken.None);

        target.Container.Name.Should().Be("fhir");
        target.Container.Uri.Query.Should().Contain("sig=abc");
        target.SupportsContainerCreate.Should().BeTrue();
    }

    [Fact]
    public async Task ManagedIdentity_mode_never_resolves_the_destination_secret()
    {
        var destination = Destination(
            "fhir", """{"dest_blobAuthMode":"managedIdentity","dest_blobAccountUrl":"https://acct.blob.core.windows.net"}""");
        var settings = BlobDestinationSettings.Parse(destination);

        var target = await CreateFactory().GetTargetAsync(destination, settings, CancellationToken.None);

        target.Container.Uri.Should().Be(new Uri("https://acct.blob.core.windows.net/fhir"));
        target.Container.CanGenerateSasUri.Should().BeFalse(); // no shared-key credential attached
        _secretProvider.Verify(
            s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ServicePrincipal_mode_resolves_the_secret_exactly_once_as_the_client_secret()
    {
        SecretResolvesTo("client-secret-value");
        var destination = Destination(
            "fhir",
            """{"dest_blobAuthMode":"servicePrincipal","dest_blobAccountUrl":"https://acct.blob.core.windows.net","dest_blobTenantId":"tenant","dest_blobClientId":"client"}""");
        var settings = BlobDestinationSettings.Parse(destination);

        var target = await CreateFactory().GetTargetAsync(destination, settings, CancellationToken.None);

        target.Container.Uri.Should().Be(new Uri("https://acct.blob.core.windows.net/fhir"));
        target.SupportsContainerCreate.Should().BeTrue();
        _secretProvider.Verify(
            s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Legacy_row_with_no_auth_mode_and_a_URL_secret_is_coerced_to_SasUrl()
    {
        // Rows saved before auth-mode metadata existed have their secret set to the old pre-signed SAS PUT URL.
        SecretResolvesTo("https://acct.blob.core.windows.net/fhir?sv=2023-01-01&sig=abc");
        var destination = Destination("fhir", null);
        var settings = BlobDestinationSettings.Parse(destination);
        settings.AuthMode.Should().Be(BlobDestinationAuthMode.ConnectionString); // as parsed, before Build's coercion

        var target = await CreateFactory().GetTargetAsync(destination, settings, CancellationToken.None);

        target.Container.Uri.AbsolutePath.Should().Be("/fhir");
        target.SupportsContainerCreate.Should().BeFalse(); // proves it took the SasUrl path, not ConnectionString
    }

    [Fact]
    public async Task Repeated_calls_for_the_same_destination_and_secret_return_the_cached_client()
    {
        SecretResolvesTo("DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=Zm9v;EndpointSuffix=core.windows.net");
        var destination = Destination("fhir", """{"dest_blobAuthMode":"connectionString"}""");
        var settings = BlobDestinationSettings.Parse(destination);
        var factory = CreateFactory();

        var first = await factory.GetTargetAsync(destination, settings, CancellationToken.None);
        var second = await factory.GetTargetAsync(destination, settings, CancellationToken.None);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task A_rotated_secret_busts_the_cache()
    {
        var destination = Destination("fhir", """{"dest_blobAuthMode":"connectionString"}""");
        var settings = BlobDestinationSettings.Parse(destination);
        var factory = CreateFactory();

        SecretResolvesTo("DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=Zm9v;EndpointSuffix=core.windows.net");
        var before = await factory.GetTargetAsync(destination, settings, CancellationToken.None);

        SecretResolvesTo("DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=cm90YXRlZA==;EndpointSuffix=core.windows.net");
        var after = await factory.GetTargetAsync(destination, settings, CancellationToken.None);

        after.Should().NotBeSameAs(before);
    }
}
