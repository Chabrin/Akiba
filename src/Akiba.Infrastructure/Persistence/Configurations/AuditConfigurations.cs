using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akiba.Infrastructure.Persistence.Configurations;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntryRow>
{
    public void Configure(EntityTypeBuilder<AuditEntryRow> builder)
    {
        builder.ToTable("audit_entries");
        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.ActorName).HasMaxLength(200);
        builder.Property(entry => entry.IpAddress).HasMaxLength(64);
        builder.Property(entry => entry.TableName).HasMaxLength(128).IsRequired();
        builder.Property(entry => entry.Action).HasMaxLength(32).IsRequired();
        builder.Property(entry => entry.PrimaryKey).HasMaxLength(512);
        builder.Property(entry => entry.ErrorMessage).HasMaxLength(2000);

        // jsonb rather than text: the before-and-after values are queried by column when
        // somebody asks what changed on a particular field, and a string would make that a
        // full scan with a LIKE in it.
        builder.Property(entry => entry.Changes).HasColumnType("jsonb");
        builder.Property(entry => entry.EntityValues).HasColumnType("jsonb");

        // Clear the 256-character default the string convention applies. PostgreSQL ignores a
        // length on jsonb, but leaving it on the model makes the migration read as though the
        // before-and-after values were truncated, and somebody would eventually believe it.
        builder.Property(entry => entry.Changes).Metadata.SetMaxLength(null);
        builder.Property(entry => entry.EntityValues).Metadata.SetMaxLength(null);

        // "What happened on the 14th?" and "what has been done to this loan?" are the two
        // questions the trail is read by.
        builder.HasIndex(entry => entry.OccurredAtUtc);
        builder.HasIndex(entry => new { entry.TableName, entry.PrimaryKey });
        builder.HasIndex(entry => entry.ActorUserId);
    }
}
