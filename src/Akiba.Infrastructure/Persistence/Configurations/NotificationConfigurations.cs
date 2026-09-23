using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akiba.Infrastructure.Persistence.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<NotificationRow>
{
    public void Configure(EntityTypeBuilder<NotificationRow> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(notification => notification.Id);

        builder.Property(notification => notification.RecipientName).HasMaxLength(200).IsRequired();
        builder.Property(notification => notification.RecipientAddress).HasMaxLength(200).IsRequired();
        builder.Property(notification => notification.Subject).HasMaxLength(300);
        builder.Property(notification => notification.Note).HasMaxLength(1000);

        // A statement runs to more than the 256 characters the string convention gives, and a
        // body cut off halfway is a message the member cannot act on.
        builder.Property(notification => notification.Body).HasMaxLength(4000).IsRequired();

        // The dispatcher's only query: what is waiting, oldest first.
        builder.HasIndex(notification => new { notification.Status, notification.QueuedAtUtc });

        // "What have we sent this member?", asked whenever somebody queries a figure.
        builder.HasIndex(notification => notification.AboutBorrowerId);
    }
}
