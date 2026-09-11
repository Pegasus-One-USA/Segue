using FHIRBridge.Api.Controllers.V1;
using FHIRBridge.Infrastructure.Licensing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// Isolated, no-DB unit-style coverage of <see cref="DevLicenseMintingController"/>'s hard
/// Development-only gate — instantiates the controller directly (no WebApplicationFactory, no
/// <see cref="ApiFixture"/>/database) so it runs the SAME real, unmodified controller code the temporary
/// "Dev: Mint a test license" page calls, without depending on this suite's full application host
/// (DB/RBAC bootstrap) being reachable. That full end-to-end path is also covered by
/// <see cref="DevLicenseMintingTests"/> in the "ApiTests" collection for an environment where the shared
/// host is reachable.
/// </summary>
public sealed class DevLicenseMintingGateUnitTests
{
    private static readonly DevLicenseMintRequestDto ValidRequest = new(
        CustomerId: "cust-unit-test",
        CustomerName: "Unit Test Co",
        Edition: "standard",
        ExpiresUtc: DateTime.UtcNow.AddYears(1),
        MaxUsers: -1,
        MaxWorkflows: -1,
        MaxSourceConnections: -1,
        Features: Array.Empty<string>());

    private sealed class RecordingMintingService : IDevLicenseMintingService
    {
        public bool WasCalled { get; private set; }

        public string Mint(DevLicenseMintRequest request)
        {
            WasCalled = true;
            return "fake-token";
        }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "FHIRBridge.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Mint__non_development_environment__returns_404_and_never_calls_the_minting_service(string environmentName)
    {
        var mintingService = new RecordingMintingService();
        var environment = new FakeHostEnvironment { EnvironmentName = environmentName };
        var controller = new DevLicenseMintingController(mintingService, environment);

        var result = controller.Mint(ValidRequest);

        Assert.IsType<NotFoundResult>(result);
        Assert.False(mintingService.WasCalled, "The dev-only signing service must never be invoked outside Development.");
    }

    [Fact]
    public void Mint__development_environment__calls_the_minting_service_and_returns_the_token()
    {
        var mintingService = new RecordingMintingService();
        var environment = new FakeHostEnvironment { EnvironmentName = Environments.Development };
        var controller = new DevLicenseMintingController(mintingService, environment);

        var result = controller.Mint(ValidRequest);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<DevLicenseMintResponse>(ok.Value);
        Assert.Equal("fake-token", response.Token);
        Assert.True(mintingService.WasCalled);
    }

    [Fact]
    public void Mint__development_environment_missing_customerId__returns_400_before_minting()
    {
        var mintingService = new RecordingMintingService();
        var environment = new FakeHostEnvironment { EnvironmentName = Environments.Development };
        var controller = new DevLicenseMintingController(mintingService, environment);

        var result = controller.Mint(ValidRequest with { CustomerId = "  " });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(mintingService.WasCalled);
    }
}
