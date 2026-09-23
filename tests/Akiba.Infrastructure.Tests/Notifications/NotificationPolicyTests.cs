using Akiba.Domain.Notifications;
using Akiba.Infrastructure.Notifications;

namespace Akiba.Infrastructure.Tests.Notifications;

/// <summary>
/// Which messages the society sends, and whether this machine may send at all.
/// </summary>
/// <remarks>
/// These live here rather than beside the domain tests because the policy is infrastructure -
/// it reads the deployment's configuration. Akiba.Domain.Tests references the domain and
/// nothing else.
/// </remarks>
public sealed class NotificationPolicyTests
{
    [Fact]
    public void Nothing_sends_outside_Production_whatever_else_is_configured()
    {
        var policy = new NotificationPolicy(
            sendingIsAllowed: false, "Not Production.", disabled: []);

        policy.SendingIsAllowed.Should().BeFalse();

        // Every kind is enabled, and it still does not matter - which is the point. The two
        // questions are separate: what the office wants sent, and whether this machine sends.
        policy.IsEnabled(NotificationKind.LoanDisbursed, NotificationChannel.Sms)
            .Should().BeTrue(because: "the kind is on; the environment is what stops it");
    }

    [Fact]
    public void A_kind_can_be_turned_off_on_one_channel_without_the_other()
    {
        var policy = new NotificationPolicy(
            sendingIsAllowed: true,
            "n/a",
            [$"{NotificationKind.ArrearsReminder}:{NotificationChannel.Sms}"]);

        policy.IsEnabled(NotificationKind.ArrearsReminder, NotificationChannel.Sms)
            .Should().BeFalse();

        policy.IsEnabled(NotificationKind.ArrearsReminder, NotificationChannel.Email)
            .Should().BeTrue();
    }

    [Fact]
    public void A_kind_can_be_turned_off_everywhere()
    {
        var policy = new NotificationPolicy(
            sendingIsAllowed: true, "n/a", [NotificationKind.ArrearsReminder.ToString()]);

        policy.IsEnabled(NotificationKind.ArrearsReminder, NotificationChannel.Sms)
            .Should().BeFalse();

        policy.IsEnabled(NotificationKind.ArrearsReminder, NotificationChannel.Email)
            .Should().BeFalse();
    }

    [Fact]
    public void The_long_messages_are_not_texted_by_default()
    {
        // A statement as an SMS arrives as three chargeable fragments and is unreadable.
        var policy = new NotificationPolicy(sendingIsAllowed: true, "n/a");

        policy.IsEnabled(NotificationKind.MonthlyStatement, NotificationChannel.Sms)
            .Should().BeFalse();

        policy.IsEnabled(NotificationKind.MonthlyStatement, NotificationChannel.Email)
            .Should().BeTrue();
    }
}
