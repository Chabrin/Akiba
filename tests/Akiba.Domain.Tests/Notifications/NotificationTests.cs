using Akiba.Domain.Notifications;

namespace Akiba.Domain.Tests.Notifications;

/// <summary>
/// A message is written down before anything is attempted, and every outcome is recorded.
/// </summary>
public sealed class NotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_email_needs_a_subject_and_an_SMS_does_not()
    {
        var withoutSubject = () => Queue(NotificationChannel.Email, subject: "   ");

        withoutSubject.Should().Throw<ArgumentException>().WithMessage("*subject*");

        // An SMS has no subject line, so demanding one would be inventing a rule.
        var text = Queue(NotificationChannel.Sms, subject: string.Empty);

        text.Subject.Should().BeEmpty();
    }

    [Fact]
    public void A_queued_message_has_not_been_attempted()
    {
        var notification = Queue(NotificationChannel.Email);

        notification.Status.Should().Be(NotificationStatus.Pending);
        notification.Attempts.Should().Be(0);
        notification.IsWaiting.Should().BeTrue();
        notification.IsFinished.Should().BeFalse();
    }

    [Fact]
    public void A_failure_keeps_the_reason_and_is_tried_again()
    {
        // "It did not send" is not something an official can act on. "Mailbox unavailable" is.
        var notification = Queue(NotificationChannel.Email);

        notification.MarkFailed("Mailbox unavailable.", Now);

        notification.Status.Should().Be(NotificationStatus.Failed);
        notification.Attempts.Should().Be(1);
        notification.Note.Should().Be("Mailbox unavailable.");
        notification.IsWaiting.Should().BeTrue(because: "it will be tried again");
        notification.Verdict.Should().Contain("Mailbox unavailable");
    }

    [Fact]
    public void It_gives_up_rather_than_retrying_for_ever()
    {
        // Each SMS attempt costs money. A loop against a gateway that accepts and charges for
        // messages it then fails to deliver would spend the society's money unwatched.
        var notification = Queue(NotificationChannel.Sms);

        for (var attempt = 0; attempt < Notification.MaximumAttempts; attempt++)
        {
            notification.MarkFailed("The gateway is not answering.", Now);
        }

        notification.Status.Should().Be(NotificationStatus.GaveUp);
        notification.IsWaiting.Should().BeFalse();
        notification.IsFinished.Should().BeTrue();
        notification.Verdict.Should().Contain("needs somebody to look at it");
    }

    [Fact]
    public void A_suppressed_message_is_kept_with_the_reason_rather_than_dropped()
    {
        // Outside Production every message ends here, and the screen showing them is how
        // somebody testing a change sees what would have gone out.
        var notification = Queue(NotificationChannel.Sms);

        notification.Suppress("Nothing is sent from the Development environment.", Now);

        notification.Status.Should().Be(NotificationStatus.Suppressed);
        notification.IsFinished.Should().BeTrue();
        notification.Body.Should().NotBeEmpty(because: "the message is kept, not discarded");
        notification.Verdict.Should().Contain("Development");
    }

    [Fact]
    public void A_sent_message_records_when()
    {
        var notification = Queue(NotificationChannel.Email);

        notification.MarkSent(Now);

        notification.Status.Should().Be(NotificationStatus.Sent);
        notification.SentAtUtc.Should().Be(Now);
        notification.Attempts.Should().Be(1);
        notification.Note.Should().BeNull();
    }

    [Fact]
    public void A_message_that_failed_and_then_sent_forgets_the_failure_but_not_the_attempts()
    {
        var notification = Queue(NotificationChannel.Email);

        notification.MarkFailed("Temporarily unavailable.", Now);
        notification.MarkSent(Now.AddMinutes(2));

        notification.Status.Should().Be(NotificationStatus.Sent);
        notification.Attempts.Should().Be(2);
        notification.Note.Should().BeNull(because: "it did eventually send");
    }

    [Fact]
    public void A_counter_message_is_delivered_by_a_named_official()
    {
        var notification = Queue(NotificationChannel.Counter);

        notification.MarkGivenAtCounter("Mary Wanjiru", Now);

        notification.Status.Should().Be(NotificationStatus.Sent);
        notification.SentAtUtc.Should().Be(Now);

        // Who handed it over is the whole point. An email records that a machine accepted a
        // message; this records that a person gave a member a document.
        notification.Note.Should().Contain("Mary Wanjiru");
    }

    [Fact]
    public void A_counter_message_cannot_be_given_twice()
    {
        var notification = Queue(NotificationChannel.Counter);

        notification.MarkGivenAtCounter("Mary Wanjiru", Now);

        var again = () => notification.MarkGivenAtCounter("Peter Otieno", Now.AddMinutes(5));

        again.Should().Throw<InvalidOperationException>().WithMessage("*already*");
    }

    [Fact]
    public void A_hand_over_records_somebody()
    {
        var notification = Queue(NotificationChannel.Counter);

        var anonymous = () => notification.MarkGivenAtCounter("  ", Now);

        anonymous.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(NotificationChannel.Email)]
    [InlineData(NotificationChannel.Sms)]
    public void Only_a_counter_message_is_handed_over(NotificationChannel channel)
    {
        // An official cannot mark an email as given. It is delivered by a mail server or it is
        // not delivered, and letting somebody say otherwise would put a claim in the record
        // that nobody could stand behind.
        var notification = Queue(channel);

        var byHand = () => notification.MarkGivenAtCounter("Mary Wanjiru", Now);

        byHand.Should().Throw<InvalidOperationException>().WithMessage("*counter*");
    }

    [Fact]
    public void A_counter_message_needs_a_subject()
    {
        // It is a written notice an official reads off a screen to route, so it needs a
        // heading for the same reason an email does.
        var withoutSubject = () => Queue(NotificationChannel.Counter, subject: " ");

        withoutSubject.Should().Throw<ArgumentException>().WithMessage("*subject*");
    }

    private static Notification Queue(
        NotificationChannel channel, string subject = "Your loan has been paid out") =>
        Notification.Queue(
            NotificationKind.LoanDisbursed,
            channel,
            "Grace Njeri",
            channel switch
            {
                NotificationChannel.Email => "grace@example.test",
                NotificationChannel.Counter => "Membership no. 0042",
                _ => "+254700000000",
            },
            subject,
            "Dear Grace Njeri,\n\nKES 60,000.00 has been paid out.",
            Now,
            Guid.NewGuid());
}
