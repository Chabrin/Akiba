using Akiba.Domain.Common;

namespace Akiba.Domain.Notifications;

/// <summary>Identifies a queued message.</summary>
public readonly record struct NotificationId(Guid Value)
{
    public static NotificationId New() => new(Guid.NewGuid());

    public bool IsSpecified => Value != Guid.Empty;

    public override string ToString() => Value.ToString();
}

/// <summary>What a message is about.</summary>
/// <remarks>
/// Each one can be turned off on its own - the brief asks for that, and it is the difference
/// between an office that keeps the useful messages and one that turns the whole system off
/// because the arrears reminders were too blunt.
/// </remarks>
public enum NotificationKind
{
    /// <summary>The committee has approved a loan.</summary>
    LoanApproved = 1,

    /// <summary>The money has gone out.</summary>
    LoanDisbursed = 2,

    /// <summary>The member's monthly statement.</summary>
    MonthlyStatement = 3,

    /// <summary>A loan is behind.</summary>
    ArrearsReminder = 4,

    /// <summary>HR could not deduct the full amount from a payslip.</summary>
    ShortfallNotice = 5,

    /// <summary>A loan this member guarantees has fallen into arrears.</summary>
    GuarantorArrearsAlert = 6,

    /// <summary>A co-guarantor has left CAL, so the cover on a loan has changed.</summary>
    GuarantorExitAlert = 7,

    /// <summary>The AGM has declared a dividend.</summary>
    DividendDeclared = 8,
}

/// <summary>How a message reaches somebody.</summary>
public enum NotificationChannel
{
    /// <summary>
    /// The confirmed channel for statements. There is no member portal, so email is how a
    /// member sees a figure without walking to the office.
    /// </summary>
    Email = 1,

    /// <summary>
    /// Short, and it costs money per message. Suited to "your loan was approved", not to a
    /// statement.
    /// </summary>
    Sms = 2,
}

/// <summary>How far a queued message has got.</summary>
public enum NotificationStatus
{
    /// <summary>Written down, not yet attempted.</summary>
    Pending = 1,

    /// <summary>Handed to the mail server or the SMS gateway.</summary>
    Sent = 2,

    /// <summary>Attempted and refused. It will be retried until it gives up.</summary>
    Failed = 3,

    /// <summary>
    /// Deliberately not sent. Outside Production nothing leaves the machine, and a kind the
    /// office has turned off is suppressed rather than dropped.
    /// </summary>
    Suppressed = 4,

    /// <summary>Retried enough times. It needs somebody to look at it.</summary>
    GaveUp = 5,
}

/// <summary>
/// One message, written down before any attempt is made to send it.
/// </summary>
/// <remarks>
/// <para>
/// An outbox row rather than a call to a mail server. The society's machine will lose its
/// internet connection, the SMS gateway will be down, and a message that existed only as a
/// method call while either was true is a message nobody knows was lost. Every message is a
/// row: queued, attempted, and either sent or visible on a screen with the reason it was not.
/// </para>
/// <para>
/// The body is composed and stored when the message is queued, not when it is sent. A
/// statement emailed in March says what the ledger said in March even if it is retried in
/// April - the same property the ledger itself has, for the same reason.
/// </para>
/// </remarks>
public sealed class Notification : AggregateRoot<NotificationId>
{
    /// <summary>
    /// How many times a message is attempted before it needs a person.
    /// </summary>
    /// <remarks>
    /// Five, spread over a few hours by the dispatcher's backoff. Beyond that the problem is
    /// not transient - a wrong address, a dead gateway, an expired API key - and retrying
    /// forever only hides it.
    /// </remarks>
    public const int MaximumAttempts = 5;

    private Notification(
        NotificationId id,
        NotificationKind kind,
        NotificationChannel channel,
        string recipientName,
        string recipientAddress,
        string subject,
        string body,
        Guid? aboutBorrowerId,
        DateTimeOffset queuedAtUtc)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientName);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        if (channel == NotificationChannel.Email && string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("An email needs a subject line.", nameof(subject));
        }

        Kind = kind;
        Channel = channel;
        RecipientName = recipientName.Trim();
        RecipientAddress = recipientAddress.Trim();
        Subject = subject?.Trim() ?? string.Empty;
        Body = body.Trim();
        AboutBorrowerId = aboutBorrowerId;
        QueuedAtUtc = queuedAtUtc;
        Status = NotificationStatus.Pending;
    }

    public NotificationKind Kind { get; }

    public NotificationChannel Channel { get; }

    /// <summary>Who it is for, for the screen.</summary>
    public string RecipientName { get; }

    /// <summary>The email address or the phone number, as it was when the message was queued.</summary>
    public string RecipientAddress { get; }

    /// <summary>Empty for an SMS, which has no subject.</summary>
    public string Subject { get; }

    public string Body { get; }

    /// <summary>Which member or client this is about, where it is about one.</summary>
    public Guid? AboutBorrowerId { get; }

    public DateTimeOffset QueuedAtUtc { get; }

    public NotificationStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? LastAttemptedAtUtc { get; private set; }

    public DateTimeOffset? SentAtUtc { get; private set; }

    /// <summary>Why the last attempt failed, or why it was never made.</summary>
    public string? Note { get; private set; }

    public bool IsFinished =>
        Status is NotificationStatus.Sent or NotificationStatus.Suppressed or NotificationStatus.GaveUp;

    /// <summary>Whether the dispatcher should pick this up.</summary>
    public bool IsWaiting => Status is NotificationStatus.Pending or NotificationStatus.Failed;

    /// <summary>Queues a message.</summary>
    public static Notification Queue(
        NotificationKind kind,
        NotificationChannel channel,
        string recipientName,
        string recipientAddress,
        string subject,
        string body,
        DateTimeOffset queuedAtUtc,
        Guid? aboutBorrowerId = null) =>
        new(
            NotificationId.New(), kind, channel, recipientName, recipientAddress,
            subject, body, aboutBorrowerId, queuedAtUtc);

    /// <summary>Rebuilds a message from storage. For the persistence layer only.</summary>
    public static Notification Rehydrate(
        NotificationId id,
        NotificationKind kind,
        NotificationChannel channel,
        string recipientName,
        string recipientAddress,
        string subject,
        string body,
        Guid? aboutBorrowerId,
        DateTimeOffset queuedAtUtc,
        NotificationStatus status,
        int attempts,
        DateTimeOffset? lastAttemptedAtUtc,
        DateTimeOffset? sentAtUtc,
        string? note) =>
        new(id, kind, channel, recipientName, recipientAddress, subject, body,
            aboutBorrowerId, queuedAtUtc)
        {
            Status = status,
            Attempts = attempts,
            LastAttemptedAtUtc = lastAttemptedAtUtc,
            SentAtUtc = sentAtUtc,
            Note = note,
        };

    /// <summary>The mail server or the gateway accepted it.</summary>
    public void MarkSent(DateTimeOffset sentAtUtc)
    {
        Attempts++;
        Status = NotificationStatus.Sent;
        SentAtUtc = sentAtUtc;
        LastAttemptedAtUtc = sentAtUtc;
        Note = null;
    }

    /// <summary>
    /// The attempt failed.
    /// </summary>
    /// <remarks>
    /// Kept as Failed and retried, until the attempts run out and it becomes somebody's job.
    /// The reason is stored, because "it did not send" is not something an official can act on
    /// and "mailbox unavailable" is.
    /// </remarks>
    public void MarkFailed(string reason, DateTimeOffset attemptedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Attempts++;
        LastAttemptedAtUtc = attemptedAtUtc;
        Note = reason.Trim();

        Status = Attempts >= MaximumAttempts
            ? NotificationStatus.GaveUp
            : NotificationStatus.Failed;
    }

    /// <summary>
    /// Deliberately not sent, with the reason.
    /// </summary>
    /// <remarks>
    /// Suppressed, not deleted. Outside Production every message ends here, and the screen
    /// showing them is how somebody testing a change sees what <i>would</i> have gone out -
    /// which is more useful than a mailbox full of test messages and much safer than a real
    /// one sent to a real member by accident.
    /// </remarks>
    public void Suppress(string reason, DateTimeOffset atUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Status = NotificationStatus.Suppressed;
        Note = reason.Trim();
        LastAttemptedAtUtc = atUtc;
    }

    /// <summary>What happened to it, in words for the screen.</summary>
    public string Verdict => Status switch
    {
        NotificationStatus.Pending => "Waiting to be sent.",
        NotificationStatus.Sent => $"Sent {SentAtUtc:d MMM yyyy HH:mm} UTC.",
        NotificationStatus.Failed =>
            $"Attempt {Attempts} of {MaximumAttempts} failed: {Note} It will be tried again.",
        NotificationStatus.GaveUp =>
            $"Gave up after {Attempts} attempts: {Note} This one needs somebody to look at it.",
        NotificationStatus.Suppressed => $"Not sent: {Note}",
        _ => "Unrecognised state.",
    };

    public override string ToString() =>
        $"{Kind} to {RecipientName} by {Channel} ({Status})";
}
