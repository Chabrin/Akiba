namespace Akiba.Infrastructure.Persistence.Rows;

/// <summary>One message in the outbox: what it says, who it is for, and what became of it.</summary>
internal sealed class NotificationRow
{
    public Guid Id { get; set; }

    public int Kind { get; set; }

    public int Channel { get; set; }

    public string RecipientName { get; set; } = string.Empty;

    /// <summary>
    /// The email address or phone number as it was when the message was queued.
    /// </summary>
    /// <remarks>
    /// Stored rather than looked up at send time. A member who changes their number should not
    /// find that a message queued last week silently goes somewhere new - and the outbox should
    /// record where a message actually went.
    /// </remarks>
    public string RecipientAddress { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public Guid? AboutBorrowerId { get; set; }

    public DateTimeOffset QueuedAtUtc { get; set; }

    public int Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? LastAttemptedAtUtc { get; set; }

    public DateTimeOffset? SentAtUtc { get; set; }

    /// <summary>Why the last attempt failed, or why none was made.</summary>
    public string? Note { get; set; }
}
