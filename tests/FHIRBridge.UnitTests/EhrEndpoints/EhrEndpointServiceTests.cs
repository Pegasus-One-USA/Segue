using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.EhrEndpoints;

/// <summary>
/// Covers the audience-scoped endpoint validation used by the anonymous Standalone launch mint endpoints. Provider
/// Standalone accepts the vendor-sandbox rows — <see cref="EhrEndpointType.Epic"/> AND <see cref="EhrEndpointType.Ecw"/>
/// (added so an eCW provider sandbox isn't mislabeled Epic) — while Patient Standalone keeps the single MyChart type.
/// Regression guard for OAuthController.public-standalone-url, which now validates against the {Epic, Ecw} set.
/// </summary>
public sealed class EhrEndpointServiceTests
{
    private static EhrEndpointService CreateService(EhrEndpoint? stored)
    {
        var repo = new Mock<IEhrEndpointRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);
        return new EhrEndpointService(repo.Object, new Mock<IUserDisplayNameResolver>().Object);
    }

    private static EhrEndpoint Endpoint(EhrEndpointType type) => new(
        SourceSystemType.Healow, "vid", "eCW Staging (FFBJCD)",
        "https://staging-fhir.ecwcloud.com/fhir/r4/FFBJCD", "R4", "active", type);

    [Theory]
    [InlineData(EhrEndpointType.Epic, true)]
    [InlineData(EhrEndpointType.Ecw, true)]
    [InlineData(EhrEndpointType.MyChart, false)]
    public async Task Provider_standalone_set_accepts_Epic_and_Ecw_sandbox_rows_but_not_MyChart(EhrEndpointType type, bool expected)
    {
        var sut = CreateService(Endpoint(type));

        var result = await sut.IsKnownEndpointAsync(
            Guid.NewGuid(), new[] { EhrEndpointType.Epic, EhrEndpointType.Ecw }, CancellationToken.None);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task Unknown_id_is_not_a_known_endpoint_for_the_set_overload()
    {
        var sut = CreateService(stored: null);

        var result = await sut.IsKnownEndpointAsync(
            Guid.NewGuid(), new[] { EhrEndpointType.Epic, EhrEndpointType.Ecw }, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Single_type_overload_still_matches_exactly_for_the_patient_flow()
    {
        var sut = CreateService(Endpoint(EhrEndpointType.MyChart));

        (await sut.IsKnownEndpointAsync(Guid.NewGuid(), EhrEndpointType.MyChart, CancellationToken.None)).Should().BeTrue();
        (await sut.IsKnownEndpointAsync(Guid.NewGuid(), EhrEndpointType.Epic, CancellationToken.None)).Should().BeFalse();
    }
}
