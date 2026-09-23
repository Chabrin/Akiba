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
            sendingIsAllowed: false, "Not Production.", disabled: [], internalOnly: false);

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
            [$"{NotificationKind.ArrearsReminder}:{NotificationChannel.Sms}"],
            internalOnly: false);

        policy.IsEnabled(NotificationKind.ArrearsReminder, NotificationChannel.Sms)
            .Should().BeFalse();

        policy.IsEnabled(NotificationKind.ArrearsReminder, NotificationChannel.Email)
            .Should().BeTrue();
    }

    [Fact]
    public void A_kind_can_be_turned_off_everywhere()
    {
        var policy = new NotificationPolicy(
            sendingIsAllowed: true,
            "n/a",
            [NotificationKind.ArrearsReminder.ToString()],
            internalOnly: false);

        policy.IsEnabled(NotificationKind.ArrearsReminder, NotificationChannel.Sms)
            .Should().BeFalse();

        policy.IsEnabled(NotificationKind.ArrearsReminder, NotificationChannel.Email)
            .Should().BeFalse();
    }

    [Fact]
    public void The_long_messages_are_not_texted_by_default()
    {
        // A statement as an SMS arrives as three chargeable fragments and is unreadable.
        var policy = new NotificationPolicy(
            sendingIsAllowed: true, "n/a", internalOnly: false);

        policy.IsEnabled(NotificationKind.MonthlyStatement, NotificationChannel.Sms)
            .Should().BeFalse();

        policy.IsEnabled(NotificationKind.MonthlyStatement, NotificationChannel.Email)
            .Should().BeTrue();
    }

    [Fact]
    public void Internal_only_is_the_default()
    {
        // The thing a deployment gets when it says nothing at all. If somebody ever flips this
        // default, every member's figures start crossing a network the society does not own,
        // so it is asserted on its own rather than only in passing.
        var policy = new NotificationPolicy(sendingIsAllowed: true, "n/a");

        policy.InternalOnly.Should().BeTrue();
    }

    [Theory]
    [InlineData(NotificationChannel.Email)]
    [InlineData(NotificationChannel.Sms)]
    public void Internal_only_refuses_every_outward_channel(NotificationChannel channel)
    {
        var policy = new NotificationPolicy(sendingIsAllowed: true, "n/a", disabled: []);

        // disabled is empty, so every kind is switched on. Internal-only still refuses, which
        // is the point: an official turning a kind on for email does not get to send.
        foreach (var kind in Enum.GetValues<NotificationKind>())
        {
            policy.IsEnabled(kind, channel)
                .Should().BeFalse(because: "nothing leaves the building while internal-only is on");
        }
    }

    [Fact]
    public void Internal_only_still_lets_the_office_turn_a_kind_off()
    {
        // Internal-only decides where a message may go, not whether the society wants to say
        // it. An arrears reminder the committee has stopped is stopped at the counter too.
        var policy = new NotificationPolicy(
            sendingIsAllowed: true, "n/a", [NotificationKind.ArrearsReminder.ToString()]);

        policy.IsEnabled(NotificationKind.ArrearsReminder, NotificationChannel.Counter)
            .Should().BeFalse();

        policy.IsEnabled(NotificationKind.LoanDisbursed, NotificationChannel.Counter)
            .Should().BeTrue();
    }
}
