using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Users;

/// <summary>
/// Regression coverage for the "invited user shows as Deactivated" bug: <see cref="User.SetEnabled"/>
/// must never move Status away from Invited — only SetInvited/AcceptInvitation/AcceptInvitationViaSso may.
/// </summary>
public sealed class UserSetEnabledTests
{
    private static User InvitedUser()
    {
        var user = new User("local:invited@x.io", "invited@x.io", null, Guid.NewGuid());
        user.SetInvited("hashed-token", DateTime.UtcNow.AddHours(1));
        return user;
    }

    [Fact]
    public void SetEnabled_false_does_not_demote_an_invited_user_to_inactive()
    {
        var user = InvitedUser();

        // Mirrors UpdateLocalUserAsync re-saving the invited user's already-false IsEnabled
        // (e.g. an admin editing the display name of a still-pending invite).
        user.SetEnabled(false);

        user.Status.Should().Be(UserStatus.Invited);
        user.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void SetEnabled_true_does_not_promote_an_invited_user_to_active()
    {
        var user = InvitedUser();

        // Mirrors the "Activate User" row action being clicked on a still-pending invite —
        // activation must only ever happen via AcceptInvitation(Via SSO).
        user.SetEnabled(true);

        user.Status.Should().Be(UserStatus.Invited);
        user.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void SetEnabled_still_toggles_active_and_inactive_for_a_non_invited_user()
    {
        var user = new User("local:active@x.io", "active@x.io", null, Guid.NewGuid());
        user.Status.Should().Be(UserStatus.Active);

        user.SetEnabled(false);
        user.Status.Should().Be(UserStatus.Inactive);
        user.IsEnabled.Should().BeFalse();

        user.SetEnabled(true);
        user.Status.Should().Be(UserStatus.Active);
        user.IsEnabled.Should().BeTrue();
    }
}
