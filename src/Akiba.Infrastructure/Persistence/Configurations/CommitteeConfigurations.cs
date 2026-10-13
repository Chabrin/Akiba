using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akiba.Infrastructure.Persistence.Configurations;

internal sealed class CommitteePositionConfiguration
    : IEntityTypeConfiguration<CommitteePositionRow>
{
    public void Configure(EntityTypeBuilder<CommitteePositionRow> builder)
    {
        builder.ToTable("committee_positions");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(500);
        builder.Property(p => p.AllowanceAmount)
            .HasColumnType(AkibaDbContext.MoneyColumnType).IsRequired();
        builder.Property(p => p.AllowanceCurrencyCode).HasMaxLength(3).IsRequired();

        builder.HasIndex(p => p.Name).IsUnique();
    }
}

internal sealed class CommitteeAppointmentConfiguration
    : IEntityTypeConfiguration<CommitteeAppointmentRow>
{
    public void Configure(EntityTypeBuilder<CommitteeAppointmentRow> builder)
    {
        builder.ToTable("committee_appointments");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.PositionName).HasMaxLength(100).IsRequired();
        builder.Property(a => a.HolderName).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Term).HasMaxLength(50).IsRequired();
        builder.Property(a => a.SuspendedReason).HasMaxLength(500);

        // Most common query: who is currently active?
        builder.HasIndex(a => new { a.PositionId, a.Status });
    }
}

internal sealed class ElectionRecordConfiguration
    : IEntityTypeConfiguration<ElectionRecordRow>
{
    public void Configure(EntityTypeBuilder<ElectionRecordRow> builder)
    {
        builder.ToTable("election_records");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Term).HasMaxLength(50).IsRequired();
        builder.Property(e => e.Notes).HasMaxLength(1000);

        builder.HasMany(e => e.Outcomes)
            .WithOne()
            .HasForeignKey(o => o.ElectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(e => e.Outcomes).AutoInclude();
    }
}

internal sealed class ElectionOutcomeConfiguration
    : IEntityTypeConfiguration<ElectionOutcomeRow>
{
    public void Configure(EntityTypeBuilder<ElectionOutcomeRow> builder)
    {
        builder.ToTable("election_outcomes");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.PositionName).HasMaxLength(100).IsRequired();
        builder.Property(o => o.ElectedName).HasMaxLength(200).IsRequired();

        // One elected person per position per election.
        builder.HasIndex(o => new { o.ElectionId, o.PositionId }).IsUnique();
    }
}
