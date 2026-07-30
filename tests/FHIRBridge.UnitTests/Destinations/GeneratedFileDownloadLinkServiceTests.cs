using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Infrastructure.Destinations;
using FHIRBridge.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;

namespace FHIRBridge.UnitTests.Destinations;

/// <summary>
/// The download-link token is HMAC-signed and carries a guid + creation timestamp (to relocate the
/// date-partitioned file without a database record) — never the real filename or path, so a client can never
/// guess/enumerate physical filenames from the token or the resulting URL.
/// </summary>
public sealed class GeneratedFileDownloadLinkServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "fhirbridge-tests-" + Guid.NewGuid().ToString("N"));

    private GeneratedFileDownloadLinkService CreateService(string signingSecret = "test-secret")
    {
        var secretAccessor = new AppSecretAccessor();
        secretAccessor.Initialize(jwtSigningKey: "unused", downloadLinkSigningSecret: signingSecret);

        var settingsCache = new Mock<ISystemSettingsCache>();
        settingsCache
            .Setup(x => x.GetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string defaultValue, CancellationToken _) => defaultValue);

        return new(Options.Create(new GeneratedFileDownloadOptions
        {
            RootPath = _rootPath,
            PublicBaseUrl = "http://localhost:5000",
        }), secretAccessor, settingsCache.Object);
    }

    private static GeneratedFile SampleFile() => new("Patient_Export.csv", "text/csv", "a,b\n1,2\n"u8.ToArray());

    [Fact]
    public async Task Round_trips_a_valid_token_back_to_the_original_file()
    {
        var service = CreateService();

        var url = await service.CreateLinkAsync(SampleFile(), TimeSpan.FromMinutes(60), CancellationToken.None);
        var token = url.Split('/').Last();
        var resolution = await service.TryResolveAsync(token, CancellationToken.None);

        resolution.Should().NotBeNull();
        resolution!.DisplayFileName.Should().Be("Patient_Export.csv");
        resolution.ContentType.Should().Be("text/csv");
        System.IO.File.Exists(resolution.PhysicalPath).Should().BeTrue();
        (await System.IO.File.ReadAllBytesAsync(resolution.PhysicalPath)).Should().BeEquivalentTo(SampleFile().Content);
    }

    [Fact]
    public async Task Never_exposes_the_real_filename_in_the_token_or_URL()
    {
        var service = CreateService();

        var url = await service.CreateLinkAsync(SampleFile(), TimeSpan.FromMinutes(60), CancellationToken.None);

        url.Should().NotContain("Patient_Export");
        url.Should().NotContain(".csv");
    }

    [Fact]
    public async Task Rejects_an_expired_token_and_deletes_the_underlying_file()
    {
        var service = CreateService();
        var url = await service.CreateLinkAsync(SampleFile(), TimeSpan.FromMilliseconds(1), CancellationToken.None);
        var token = url.Split('/').Last();
        await Task.Delay(50);

        var resolution = await service.TryResolveAsync(token, CancellationToken.None);

        resolution.Should().BeNull();
    }

    [Fact]
    public async Task Rejects_a_tampered_token()
    {
        var service = CreateService();
        var url = await service.CreateLinkAsync(SampleFile(), TimeSpan.FromMinutes(60), CancellationToken.None);
        var token = url.Split('/').Last();
        var tampered = token[..^1] + (token[^1] == 'a' ? 'b' : 'a');

        var resolution = await service.TryResolveAsync(tampered, CancellationToken.None);

        resolution.Should().BeNull();
    }

    [Fact]
    public async Task Rejects_a_token_signed_with_a_different_secret()
    {
        var url = await CreateService("secret-one").CreateLinkAsync(SampleFile(), TimeSpan.FromMinutes(60), CancellationToken.None);
        var token = url.Split('/').Last();

        var resolution = await CreateService("secret-two").TryResolveAsync(token, CancellationToken.None);

        resolution.Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
