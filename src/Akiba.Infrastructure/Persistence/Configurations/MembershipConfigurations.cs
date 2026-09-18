using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akiba.Infrastructure.Persistence.Configurations;

internal sealed class BorrowerConfiguration : IEntityTypeConfiguration<BorrowerRow>
{
    public void Configure(EntityTypeBuilder<BorrowerRow> builder)
    {
        builder.ToTable("borrowers");
        builder.HasKey(borrower => borrower.Id);

        builder.Property(borrower => borrower.GivenName).HasMaxLength(100).IsRequired();
        builder.Property(borrower => borrower.FamilyName).HasMaxLength(100).IsRequired();
        builder.Property(borrower => borrower.OtherNames).HasMaxLength(100);
        // Not required. A member imported from the deduction register has neither until the
        // clerk enters them, and refusing to store the member until then would mean refusing to
        // migrate the society.
        builder.Property(borrower => borrower.NationalId).HasMaxLength(12);
        builder.Property(borrower => borrower.Phone).HasMaxLength(20);
        builder.Property(borrower => borrower.Email).HasMaxLength(256);
        builder.Property(borrower => borrower.MembershipNumber).HasMaxLength(20);
        builder.Property(borrower => borrower.PayrollNumber).HasMaxLength(20);
        builder.Property(borrower => borrower.IntroducedBy).HasMaxLength(200);

        // HR matches the monthly deduction schedule on the payroll number, so two members
        // sharing one would be a real-world collision - but only where there IS one. The
        // society's own register has two shareholders who are not on the payroll, and an
        // unfiltered unique index would have refused to load it.
        builder.HasIndex(borrower => borrower.PayrollNumber)
            .IsUnique()
            .HasFilter("\"PayrollNumber\" IS NOT NULL AND \"PayrollNumber\" <> ''");

        builder.HasIndex(borrower => borrower.MembershipNumber)
            .IsUnique()
            .HasFilter("\"MembershipNumber\" IS NOT NULL");

        builder.HasIndex(borrower => borrower.NationalId);

        builder.HasMany(borrower => borrower.Documents)
            .WithOne()
            .HasForeignKey(document => document.BorrowerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(borrower => borrower.Documents).AutoInclude();
    }
}

internal sealed class AttachedDocumentConfiguration : IEntityTypeConfiguration<AttachedDocumentRow>
{
    public void Configure(EntityTypeBuilder<AttachedDocumentRow> builder)
    {
        builder.ToTable("attached_documents");
        builder.HasKey(document => document.Id);

        builder.Property(document => document.FileName).HasMaxLength(260).IsRequired();
        builder.Property(document => document.StoragePath).HasMaxLength(500).IsRequired();
        builder.Property(document => document.Note).HasMaxLength(500);
    }
}

internal sealed class ZoneConfiguration : IEntityTypeConfiguration<ZoneRow>
{
    public void Configure(EntityTypeBuilder<ZoneRow> builder)
    {
        builder.ToTable("zones");
        builder.HasKey(zone => zone.Id);

        builder.Property(zone => zone.Code).HasMaxLength(20).IsRequired();
        builder.Property(zone => zone.Name).HasMaxLength(200).IsRequired();

        builder.HasIndex(zone => zone.Code).IsUnique();

        builder.HasMany(zone => zone.Representatives)
            .WithOne()
            .HasForeignKey(representative => representative.ZoneId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(zone => zone.Representatives).AutoInclude();
    }
}

internal sealed class ZoneRepresentativeConfiguration : IEntityTypeConfiguration<ZoneRepresentativeRow>
{
    public void Configure(EntityTypeBuilder<ZoneRepresentativeRow> builder)
    {
        builder.ToTable("zone_representatives");

        // A representative appears once per zone. The composite key says so rather than
        // leaving it to the code that adds them.
        builder.HasKey(representative => new { representative.ZoneId, representative.MemberId });
    }
}
