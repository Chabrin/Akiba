using Akiba.Domain.Notifications;
using Akiba.Infrastructure.Persistence.Rows;

namespace Akiba.Infrastructure.Persistence;

/// <summary>Translates between the stored outbox rows and the aggregate.</summary>
internal static class NotificationMapper
{
    public static NotificationRow ToRow(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return new NotificationRow
        {
            Id = notification.Id.Value,
            Kind = (int)notification.Kind,
            Channel = (int)notification.Channel,
            RecipientName = notification.RecipientName,
            RecipientAddress = notification.RecipientAddress,
            Subject = notification.Subject,
            Body = notification.Body,
            AboutBorrowerId = notification.AboutBorrowerId,
            QueuedAtUtc = notification.QueuedAtUtc,
            Status = (int)notification.Status,
            Attempts = notification.Attempts,
            LastAttemptedAtUtc = notification.LastAttemptedAtUtc,
            SentAtUtc = notification.SentAtUtc,
            Note = notification.Note,
        };
    }

    public static Notification ToDomain(NotificationRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return Notification.Rehydrate(
            new NotificationId(row.Id),
            (NotificationKind)row.Kind,
            (NotificationChannel)row.Channel,
            row.RecipientName,
            row.RecipientAddress,
            row.Subject,
            row.Body,
            row.AboutBorrowerId,
            row.QueuedAtUtc,
            (NotificationStatus)row.Status,
            row.Attempts,
            row.LastAttemptedAtUtc,
            row.SentAtUtc,
            row.Note);
    }
}
