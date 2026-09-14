using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.Services.Licensing;
using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace FHIRBridge.UnitTests.Licensing;

/// <summary>
/// Exercises <see cref="LicenseQuotaGuard"/> directly against mocked <see cref="ILicenseService"/>/
/// <see cref="ILicenseUsageCountsProvider"/>/<see cref="ILicenseUsageExecutionStatsProvider"/> collaborators —
/// this is the one place all of this product's real license enforcement decisions live (every call site,
/// whether the central <c>LicenseEnforcementSaveChangesInterceptor</c> or a pipeline-run trigger, only ever
/// delegates to these methods).
/// </summary>
public sealed class LicenseQuotaGuardTests
{
    private readonly Mock<ILicenseService> _licenseService = new();
    private readonly Mock<ILicenseUsageCountsProvider> _usageCountsProvider = new();
    private readonly Mock<ILicenseUsageExecutionStatsProvider> _executionStatsProvider = new();
    private readonly LicenseQuotaGuard _sut;

    public LicenseQuotaGuardTests()
    {
        _sut = new LicenseQuotaGuard(
            _licenseService.Object,
            _usageCountsProvider.Object,
            _executionStatsProvider.Object,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<LicenseQuotaGuard>.Instance);
    }

    private static LicenseStatus ActiveStatus(LicenseLimits limits, bool expired = false) => new(
        expired ? LicenseState.Expired : LicenseState.Active,
        "Acme Health",
        "standard",
        DateTime.UtcNow.AddYears(-1),
        expired ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow.AddYears(1),
        limits,
        Array.Empty<string>(),
        null);

    private static LicenseUsageCounts Counts(int users = 0, int sourceConnections = 0, int workflows = 0) =>
        new(users, sourceConnections, TenantCount: 0, workflows);

    // ── EnsureUserQuotaAvailableAsync ───────────────────────────────────────────

    [Fact]
    public async Task EnsureUserQuotaAvailableAsync_at_limit_throws()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(MaxUsers: 3)));
        _usageCountsProvider.Setup(x => x.GetCurrentCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Counts(users: 3));

        var act = () => _sut.EnsureUserQuotaAvailableAsync(CancellationToken.None);

        await act.Should().ThrowAsync<LicenseQuotaExceededException>();
    }

    [Fact]
    public async Task EnsureUserQuotaAvailableAsync_one_under_limit_passes()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(MaxUsers: 3)));
        _usageCountsProvider.Setup(x => x.GetCurrentCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Counts(users: 2));

        var act = () => _sut.EnsureUserQuotaAvailableAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureUserQuotaAvailableAsync_unlimited_always_passes()
    {
        _licenseService.Setup(x => x.Current)
            .Returns(ActiveStatus(new LicenseLimits(MaxUsers: LicenseLimits.Unlimited)));
        _usageCountsProvider.Setup(x => x.GetCurrentCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Counts(users: 100_000));

        var act = () => _sut.EnsureUserQuotaAvailableAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        _usageCountsProvider.Verify(x => x.GetCurrentCountsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureUserQuotaAvailableAsync_no_license_present_is_a_noop()
    {
        _licenseService.Setup(x => x.Current).Returns(LicenseStatus.Unlicensed);

        var act = () => _sut.EnsureUserQuotaAvailableAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        _usageCountsProvider.Verify(x => x.GetCurrentCountsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureUserQuotaAvailableAsync_dependency_throwing_fails_open()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(MaxUsers: 1)));
        _usageCountsProvider.Setup(x => x.GetCurrentCountsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => _sut.EnsureUserQuotaAvailableAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── EnsureSourceConnectionQuotaAvailableAsync ───────────────────────────────

    [Fact]
    public async Task EnsureSourceConnectionQuotaAvailableAsync_at_limit_throws()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(MaxSourceConnections: 2)));
        _usageCountsProvider.Setup(x => x.GetCurrentCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Counts(sourceConnections: 2));

        var act = () => _sut.EnsureSourceConnectionQuotaAvailableAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().ThrowAsync<LicenseQuotaExceededException>();
    }

    [Fact]
    public async Task EnsureSourceConnectionQuotaAvailableAsync_one_under_limit_passes()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(MaxSourceConnections: 2)));
        _usageCountsProvider.Setup(x => x.GetCurrentCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Counts(sourceConnections: 1));

        var act = () => _sut.EnsureSourceConnectionQuotaAvailableAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureSourceConnectionQuotaAvailableAsync_unlimited_always_passes()
    {
        _licenseService.Setup(x => x.Current)
            .Returns(ActiveStatus(new LicenseLimits(MaxSourceConnections: LicenseLimits.Unlimited)));

        var act = () => _sut.EnsureSourceConnectionQuotaAvailableAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureSourceConnectionQuotaAvailableAsync_allowed_source_type_match_passes()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(
            new LicenseLimits(AllowedSourceTypes: new[] { "Epic", "Cerner" })));

        var act = () => _sut.EnsureSourceConnectionQuotaAvailableAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureSourceConnectionQuotaAvailableAsync_disallowed_source_type_throws()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(
            new LicenseLimits(AllowedSourceTypes: new[] { "Cerner" })));

        var act = () => _sut.EnsureSourceConnectionQuotaAvailableAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().ThrowAsync<LicenseRestrictionViolationException>()
            .Where(ex => ex.Reason == "SourceTypeNotAllowed");
    }

    [Fact]
    public async Task EnsureSourceConnectionQuotaAvailableAsync_null_allow_list_is_unrestricted()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(AllowedSourceTypes: null)));

        var act = () => _sut.EnsureSourceConnectionQuotaAvailableAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureSourceConnectionQuotaAvailableAsync_allowed_hospital_match_passes()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(
            AllowedHospitals: new[] { new AllowedHospital("Epic", "https://fhir.example.org", "Acme Hospital") })));

        var act = () => _sut.EnsureSourceConnectionQuotaAvailableAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureSourceConnectionQuotaAvailableAsync_hospital_not_on_allow_list_throws()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(
            AllowedHospitals: new[] { new AllowedHospital("Epic", "https://other.example.org", "Other Hospital") })));

        var act = () => _sut.EnsureSourceConnectionQuotaAvailableAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().ThrowAsync<LicenseRestrictionViolationException>()
            .Where(ex => ex.Reason == "HospitalNotAllowed");
    }

    [Fact]
    public async Task EnsureSourceConnectionQuotaAvailableAsync_no_license_present_is_a_noop()
    {
        _licenseService.Setup(x => x.Current).Returns(LicenseStatus.Unlicensed);

        var act = () => _sut.EnsureSourceConnectionQuotaAvailableAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureSourceConnectionQuotaAvailableAsync_dependency_throwing_fails_open()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(MaxSourceConnections: 1)));
        _usageCountsProvider.Setup(x => x.GetCurrentCountsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => _sut.EnsureSourceConnectionQuotaAvailableAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── EnsureSourceConnectionStillAllowedAsync (run-time re-check, no count) ──────────────────────

    [Fact]
    public async Task EnsureSourceConnectionStillAllowedAsync_allowed_source_type_match_passes()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(
            new LicenseLimits(AllowedSourceTypes: new[] { "Epic" })));

        var act = () => _sut.EnsureSourceConnectionStillAllowedAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureSourceConnectionStillAllowedAsync_disallowed_source_type_throws()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(
            new LicenseLimits(AllowedSourceTypes: new[] { "Cerner" })));

        var act = () => _sut.EnsureSourceConnectionStillAllowedAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().ThrowAsync<LicenseRestrictionViolationException>()
            .Where(ex => ex.Reason == "SourceTypeNotAllowed");
    }

    [Fact]
    public async Task EnsureSourceConnectionStillAllowedAsync_hospital_not_on_allow_list_throws()
    {
        // Simulates a license renewal that dropped this hospital from the allow-list, or a connection whose
        // BaseUrl was edited after creation — the exact gap this method exists to close.
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(
            AllowedHospitals: new[] { new AllowedHospital("Epic", "https://other.example.org", "Other Hospital") })));

        var act = () => _sut.EnsureSourceConnectionStillAllowedAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().ThrowAsync<LicenseRestrictionViolationException>()
            .Where(ex => ex.Reason == "HospitalNotAllowed");
    }

    [Fact]
    public async Task EnsureSourceConnectionStillAllowedAsync_hospital_allow_list_match_passes()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(
            AllowedHospitals: new[] { new AllowedHospital("Epic", "https://fhir.example.org", "Acme Hospital") })));

        var act = () => _sut.EnsureSourceConnectionStillAllowedAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureSourceConnectionStillAllowedAsync_null_allow_lists_are_unrestricted()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(
            new LicenseLimits(AllowedSourceTypes: null, AllowedHospitals: null)));

        var act = () => _sut.EnsureSourceConnectionStillAllowedAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureSourceConnectionStillAllowedAsync_never_checks_the_count_cap()
    {
        // MaxSourceConnections is a create-time row-count cap - it must never gate an EXISTING connection
        // simply being used again, unlike EnsureSourceConnectionQuotaAvailableAsync.
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(MaxSourceConnections: 0)));

        var act = () => _sut.EnsureSourceConnectionStillAllowedAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().NotThrowAsync();
        _usageCountsProvider.Verify(x => x.GetCurrentCountsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureSourceConnectionStillAllowedAsync_no_license_present_is_a_noop()
    {
        _licenseService.Setup(x => x.Current).Returns(LicenseStatus.Unlicensed);

        var act = () => _sut.EnsureSourceConnectionStillAllowedAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureSourceConnectionStillAllowedAsync_dependency_throwing_fails_open()
    {
        // Failing open here means "the guard's own logic threw," not "the license is disallowed" - simulate
        // via a license service that throws when read.
        _licenseService.Setup(x => x.Current).Throws(new InvalidOperationException("boom"));

        var act = () => _sut.EnsureSourceConnectionStillAllowedAsync(
            SourceSystemType.Epic, "https://fhir.example.org", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── EnsureWorkflowQuotaAvailableAsync ───────────────────────────────────────

    [Fact]
    public async Task EnsureWorkflowQuotaAvailableAsync_at_limit_throws()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(MaxWorkflows: 5)));
        _usageCountsProvider.Setup(x => x.GetCurrentCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Counts(workflows: 5));

        var act = () => _sut.EnsureWorkflowQuotaAvailableAsync(CancellationToken.None);

        await act.Should().ThrowAsync<LicenseQuotaExceededException>();
    }

    [Fact]
    public async Task EnsureWorkflowQuotaAvailableAsync_one_under_limit_passes()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(MaxWorkflows: 5)));
        _usageCountsProvider.Setup(x => x.GetCurrentCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Counts(workflows: 4));

        var act = () => _sut.EnsureWorkflowQuotaAvailableAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureWorkflowQuotaAvailableAsync_unlimited_always_passes()
    {
        _licenseService.Setup(x => x.Current)
            .Returns(ActiveStatus(new LicenseLimits(MaxWorkflows: LicenseLimits.Unlimited)));

        var act = () => _sut.EnsureWorkflowQuotaAvailableAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureWorkflowQuotaAvailableAsync_no_license_present_is_a_noop()
    {
        _licenseService.Setup(x => x.Current).Returns(LicenseStatus.Unlicensed);

        var act = () => _sut.EnsureWorkflowQuotaAvailableAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureWorkflowQuotaAvailableAsync_dependency_throwing_fails_open()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(MaxWorkflows: 1)));
        _usageCountsProvider.Setup(x => x.GetCurrentCountsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => _sut.EnsureWorkflowQuotaAvailableAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── EnsureResourceTypeAllowedAsync ──────────────────────────────────────────

    [Fact]
    public async Task EnsureResourceTypeAllowedAsync_allowed_match_passes()
    {
        _licenseService.Setup(x => x.Current).Returns(
            ActiveStatus(new LicenseLimits(AllowedResourceTypes: new[] { "Patient", "Observation" })));

        var act = () => _sut.EnsureResourceTypeAllowedAsync("Patient", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureResourceTypeAllowedAsync_miss_throws()
    {
        _licenseService.Setup(x => x.Current).Returns(
            ActiveStatus(new LicenseLimits(AllowedResourceTypes: new[] { "Patient" })));

        var act = () => _sut.EnsureResourceTypeAllowedAsync("Observation", CancellationToken.None);

        await act.Should().ThrowAsync<LicenseRestrictionViolationException>()
            .Where(ex => ex.Reason == "ResourceTypeNotAllowed");
    }

    [Fact]
    public async Task EnsureResourceTypeAllowedAsync_null_or_empty_allow_list_always_passes()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(AllowedResourceTypes: null)));

        var act = () => _sut.EnsureResourceTypeAllowedAsync("AnyResourceType", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureResourceTypeAllowedAsync_no_license_present_is_a_noop()
    {
        _licenseService.Setup(x => x.Current).Returns(LicenseStatus.Unlicensed);

        var act = () => _sut.EnsureResourceTypeAllowedAsync("Patient", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureResourceTypeAllowedAsync_dependency_throwing_fails_open()
    {
        _licenseService.Setup(x => x.Current).Throws(new InvalidOperationException("boom"));

        var act = () => _sut.EnsureResourceTypeAllowedAsync("Patient", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── EnsureDestinationTypeAllowedAsync ────────────────────────────────────────

    [Fact]
    public async Task EnsureDestinationTypeAllowedAsync_allowed_match_passes()
    {
        _licenseService.Setup(x => x.Current).Returns(
            ActiveStatus(new LicenseLimits(AllowedDestinationTypes: new[] { "SqlServer", "Csv" })));

        var act = () => _sut.EnsureDestinationTypeAllowedAsync(DestinationType.SqlServer, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureDestinationTypeAllowedAsync_miss_throws()
    {
        _licenseService.Setup(x => x.Current).Returns(
            ActiveStatus(new LicenseLimits(AllowedDestinationTypes: new[] { "Csv" })));

        var act = () => _sut.EnsureDestinationTypeAllowedAsync(DestinationType.SqlServer, CancellationToken.None);

        await act.Should().ThrowAsync<LicenseRestrictionViolationException>()
            .Where(ex => ex.Reason == "DestinationTypeNotAllowed");
    }

    [Fact]
    public async Task EnsureDestinationTypeAllowedAsync_null_or_empty_allow_list_always_passes()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(AllowedDestinationTypes: null)));

        var act = () => _sut.EnsureDestinationTypeAllowedAsync(DestinationType.SqlServer, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureDestinationTypeAllowedAsync_no_license_present_is_a_noop()
    {
        _licenseService.Setup(x => x.Current).Returns(LicenseStatus.Unlicensed);

        var act = () => _sut.EnsureDestinationTypeAllowedAsync(DestinationType.SqlServer, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── EnsureCanStartNewRunAsync ────────────────────────────────────────────────

    [Fact]
    public async Task EnsureCanStartNewRunAsync_expired_license_blocks()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(), expired: true));

        var act = () => _sut.EnsureCanStartNewRunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<LicenseRestrictionViolationException>()
            .Where(ex => ex.Reason == "LicenseExpired");
    }

    [Fact]
    public async Task EnsureCanStartNewRunAsync_non_expired_passes()
    {
        _licenseService.Setup(x => x.Current).Returns(ActiveStatus(new LicenseLimits(), expired: false));

        var act = () => _sut.EnsureCanStartNewRunAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureCanStartNewRunAsync_at_or_over_records_per_month_cap_blocks()
    {
        _licenseService.Setup(x => x.Current).Returns(
            ActiveStatus(new LicenseLimits(MaxProcessedRecordsPerMonth: 1000)));
        _executionStatsProvider.Setup(x => x.GetCurrentStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LicenseUsageExecutionStats(0, 0, ProcessedRecordsThisMonth: 1000, SuccessfulExecutionsThisMonth: 0));

        var act = () => _sut.EnsureCanStartNewRunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<LicenseRestrictionViolationException>()
            .Where(ex => ex.Reason == "ProcessedRecordsQuotaExceeded");
    }

    [Fact]
    public async Task EnsureCanStartNewRunAsync_under_cap_passes()
    {
        _licenseService.Setup(x => x.Current).Returns(
            ActiveStatus(new LicenseLimits(MaxProcessedRecordsPerMonth: 1000)));
        _executionStatsProvider.Setup(x => x.GetCurrentStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LicenseUsageExecutionStats(0, 0, ProcessedRecordsThisMonth: 999, SuccessfulExecutionsThisMonth: 0));

        var act = () => _sut.EnsureCanStartNewRunAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureCanStartNewRunAsync_at_or_over_successful_executions_per_month_cap_blocks()
    {
        _licenseService.Setup(x => x.Current).Returns(
            ActiveStatus(new LicenseLimits(MaxSuccessfulWorkflowExecutionsPerMonth: 50)));
        _executionStatsProvider.Setup(x => x.GetCurrentStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LicenseUsageExecutionStats(0, 0, ProcessedRecordsThisMonth: 0, SuccessfulExecutionsThisMonth: 50));

        var act = () => _sut.EnsureCanStartNewRunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<LicenseRestrictionViolationException>()
            .Where(ex => ex.Reason == "SuccessfulExecutionsQuotaExceeded");
    }

    [Fact]
    public async Task EnsureCanStartNewRunAsync_under_successful_executions_cap_passes()
    {
        _licenseService.Setup(x => x.Current).Returns(
            ActiveStatus(new LicenseLimits(MaxSuccessfulWorkflowExecutionsPerMonth: 50)));
        _executionStatsProvider.Setup(x => x.GetCurrentStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LicenseUsageExecutionStats(0, 0, ProcessedRecordsThisMonth: 0, SuccessfulExecutionsThisMonth: 49));

        var act = () => _sut.EnsureCanStartNewRunAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureCanStartNewRunAsync_unlimited_records_per_month_always_passes()
    {
        _licenseService.Setup(x => x.Current).Returns(
            ActiveStatus(new LicenseLimits(MaxProcessedRecordsPerMonth: LicenseLimits.Unlimited)));

        var act = () => _sut.EnsureCanStartNewRunAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        _executionStatsProvider.Verify(x => x.GetCurrentStatsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureCanStartNewRunAsync_no_license_present_is_a_noop()
    {
        _licenseService.Setup(x => x.Current).Returns(LicenseStatus.Unlicensed);

        var act = () => _sut.EnsureCanStartNewRunAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureCanStartNewRunAsync_dependency_throwing_fails_open()
    {
        _licenseService.Setup(x => x.Current).Returns(
            ActiveStatus(new LicenseLimits(MaxProcessedRecordsPerMonth: 10)));
        _executionStatsProvider.Setup(x => x.GetCurrentStatsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => _sut.EnsureCanStartNewRunAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
