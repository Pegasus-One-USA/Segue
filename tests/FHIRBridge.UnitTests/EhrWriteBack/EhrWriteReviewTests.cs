using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.SharedKernel.Exceptions;
using FHIRBridge.UnitTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.EhrWriteBack;

/// <summary>
/// Phase 2's controls around live writes: what an administrator can release, which ledger rows reach the review
/// list, and what a reviewer can do with them.
/// </summary>
public sealed class EhrWriteReviewTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);

    private static EhrWriteLedgerEntry Entry(string state, DateTime? updated = null)
    {
        var entry = new EhrWriteLedgerEntry(
            new string('A', 64), Guid.NewGuid(), "AllergyIntolerance", Guid.NewGuid().ToString("N").PadRight(64, '0')[..64],
            new string('C', 64), EhrWriteOperation.Create, null, null, (updated ?? Now).AddMinutes(-1));
        var at = updated ?? Now;
        entry.MarkSending(entry.ContentHash, at);
        switch (state)
        {
            case EhrWriteLedgerState.Written: entry.MarkWritten("eWritten", 201, at); break;
            case EhrWriteLedgerState.Rejected: entry.MarkRejected(422, "59012", at); break;
            case EhrWriteLedgerState.Unknown: entry.MarkUnknown(504, null, at); break;
            case EhrWriteLedgerState.AlreadyAtTarget: entry.MarkAlreadyAtTarget(null, 400, "59141", at); break;
        }

        return entry;
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("Epic:AllergyIntolerance", "AllergyIntolerance")]
    [InlineData(" epic : condition , Epic:DocumentReference ", "Condition,DocumentReference")]
    [InlineData("Epic:Observation,Epic:Patient", "Observation,Patient")]
    [InlineData("Healow:AllergyIntolerance,Epic:Immunization,AllergyIntolerance,:Condition", "")]
    public void Only_live_capable_types_of_the_vendor_can_be_released(string setting, string expected)
    {
        var released = EhrWriteBackSettings.ReleasedResourceTypes(setting, SourceSystemType.Epic);

        released.Order(StringComparer.Ordinal).Should().Equal(
            expected.Split(',', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Eclinicalworks_types_cannot_be_released_for_live_writes_yet()
    {
        EhrWriteBackSettings.ReleasedResourceTypes("Healow:AllergyIntolerance,Healow:Patient", SourceSystemType.Healow)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(EhrWriteLedgerState.Unknown, 0, true)]
    [InlineData(EhrWriteLedgerState.Rejected, 0, true)]
    [InlineData(EhrWriteLedgerState.Pending, 20, true)]
    [InlineData(EhrWriteLedgerState.Pending, 1, false)]
    [InlineData(EhrWriteLedgerState.Written, 0, false)]
    [InlineData(EhrWriteLedgerState.AlreadyAtTarget, 0, false)]
    [InlineData(EhrWriteLedgerState.Released, 0, false)]
    public void Review_list_holds_unknown_refused_and_abandoned_writes(string state, int minutesAgo, bool expected)
    {
        EhrWriteLedgerState.NeedsReview(state, Now.AddMinutes(-minutesAgo), Now).Should().Be(expected);
    }

    [Fact]
    public void Resolving_as_written_records_the_ehr_id_and_the_reviewer()
    {
        var entry = Entry(EhrWriteLedgerState.Unknown);

        entry.ResolveAsWritten(" eFound ", "reviewer@example.com", Now);

        entry.State.Should().Be(EhrWriteLedgerState.Written);
        entry.TargetResourceId.Should().Be("eFound");
        entry.ReviewedBy.Should().Be("reviewer@example.com");
        entry.ReviewedOnUtc.Should().Be(Now);
        EhrWriteLedgerState.BlocksResend(entry.State).Should().BeTrue();
    }

    [Fact]
    public void Releasing_lets_the_next_run_send_it_again()
    {
        var entry = Entry(EhrWriteLedgerState.Unknown);

        entry.ReleaseForResend("reviewer@example.com", Now);

        entry.State.Should().Be(EhrWriteLedgerState.Released);
        EhrWriteLedgerState.BlocksResend(entry.State).Should().BeFalse();
    }

    [Theory]
    [InlineData(EhrWriteLedgerState.Written)]
    [InlineData(EhrWriteLedgerState.AlreadyAtTarget)]
    public void A_settled_write_cannot_be_resolved(string state)
    {
        var entry = Entry(state);

        var act = () => entry.ReleaseForResend("reviewer@example.com", Now);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_send_still_in_flight_cannot_be_resolved()
    {
        var entry = Entry(EhrWriteLedgerState.Pending, Now.AddMinutes(-2));

        var act = () => entry.ResolveAsWritten("eFound", "reviewer@example.com", Now);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void State_is_a_concurrency_token_so_a_reviewer_and_a_run_cannot_both_win()
    {
        using var context = new FHIRBridgeDbContext(SharedInMemoryDatabase.Options<FHIRBridgeDbContext>(SharedInMemoryDatabase.NewDatabaseName()));
        var entity = context.Model.FindEntityType(typeof(EhrWriteLedgerEntry))!;

        entity.FindProperty(nameof(EhrWriteLedgerEntry.State))!.IsConcurrencyToken.Should().BeTrue();
        entity.FindProperty(nameof(EhrWriteLedgerEntry.AttemptCount))!.IsConcurrencyToken.Should().BeTrue();
        entity.FindProperty(nameof(EhrWriteLedgerEntry.ReviewedBy))!.GetMaxLength().Should().Be(256);
    }

    [Fact]
    public async Task Review_service_lists_reviewable_rows_and_names_the_connection()
    {
        var ledger = new InMemoryEhrWriteLedgerRepository();
        var unknown = Entry(EhrWriteLedgerState.Unknown, DateTime.UtcNow);
        await ledger.TryAddAsync(unknown, CancellationToken.None);
        await ledger.TryAddAsync(Entry(EhrWriteLedgerState.Written, DateTime.UtcNow), CancellationToken.None);
        await ledger.TryAddAsync(Entry(EhrWriteLedgerState.Pending, DateTime.UtcNow.AddHours(-1)), CancellationToken.None);

        var page = await CreateService(ledger).ListAsync(null, 1, 25, CancellationToken.None);

        page.TotalCount.Should().Be(2);
        page.Items.Select(i => i.State).Should().BeEquivalentTo([EhrWriteLedgerState.Unknown, EhrWriteLedgerReviewService.AbandonedState]);
    }

    [Fact]
    public async Task Review_service_records_who_resolved_the_row()
    {
        var ledger = new InMemoryEhrWriteLedgerRepository();
        var unknown = Entry(EhrWriteLedgerState.Unknown, DateTime.UtcNow);
        await ledger.TryAddAsync(unknown, CancellationToken.None);

        var dto = await CreateService(ledger).ResolveAsWrittenAsync(unknown.Id, "eFound", CancellationToken.None);

        dto.State.Should().Be(EhrWriteLedgerState.Written);
        dto.TargetResourceId.Should().Be("eFound");
        dto.ReviewedBy.Should().Be("reviewer@example.com");
    }

    [Fact]
    public async Task Review_service_turns_an_invalid_resolution_into_a_business_rule_error()
    {
        var ledger = new InMemoryEhrWriteLedgerRepository();
        var written = Entry(EhrWriteLedgerState.Written, DateTime.UtcNow);
        await ledger.TryAddAsync(written, CancellationToken.None);
        var service = CreateService(ledger);

        await service.Invoking(s => s.ReleaseForResendAsync(written.Id, CancellationToken.None))
            .Should().ThrowAsync<BusinessRuleException>();
        await service.Invoking(s => s.ResolveAsWrittenAsync(written.Id, "  ", CancellationToken.None))
            .Should().ThrowAsync<BusinessRuleException>();
        await service.Invoking(s => s.ReleaseForResendAsync(Guid.NewGuid(), CancellationToken.None))
            .Should().ThrowAsync<NotFoundException>();
    }

    private static EhrWriteLedgerReviewService CreateService(IEhrWriteLedgerRepository ledger)
    {
        var configuration = new Mock<IConfigurationRepository>();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(c => c.CurrentUser).Returns(new CurrentUserInfo("ext-1", "reviewer@example.com", "Reviewer", [], true));
        return new EhrWriteLedgerReviewService(ledger, configuration.Object, currentUser.Object, NullLogger<EhrWriteLedgerReviewService>.Instance);
    }
}
