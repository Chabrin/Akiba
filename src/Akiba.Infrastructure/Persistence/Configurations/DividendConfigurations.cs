using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akiba.Infrastructure.Persistence.Configurations;

internal sealed class DividendRunConfiguration : IEntityTypeConfiguration<DividendRunRow>
{
    public void Configure(EntityTypeBuilder<DividendRunRow> builder)
    {
        builder.ToTable("dividend_runs");
        builder.HasKey(run => run.Id);

        builder.Property(run => run.InterestEarned)
            .HasColumnType(AkibaDbContext.MoneyColumnType).IsRequired();

        builder.Property(run => run.BankCharges)
            .HasColumnType(AkibaDbContext.MoneyColumnType).IsRequired();

        builder.Property(run => run.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(run => run.BasisName).HasMaxLength(100).IsRequired();
        builder.Property(run => run.BasisExplanation).HasMaxLength(500);
        builder.Property(run => run.ComputedByName).HasMaxLength(200);
        builder.Property(run => run.ReviewedByName).HasMaxLength(200);
        builder.Property(run => run.ApprovedByName).HasMaxLength(200);
        builder.Property(run => run.WithdrawnReason).HasMaxLength(500);

        // Several runs a year are allowed - a computation can be withdrawn and redone - so this
        // is not unique. Only one of them may reach Posted, and the handler enforces that.
        builder.HasIndex(run => run.Year);

        builder.HasMany(run => run.Lines)
            .WithOne()
            .HasForeignKey(line => line.DividendRunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(run => run.Lines).AutoInclude();
    }
}

internal sealed class DividendLineConfiguration : IEntityTypeConfiguration<DividendLineRow>
{
    public void Configure(EntityTypeBuilder<DividendLineRow> builder)
    {
        builder.ToTable("dividend_lines");
        builder.HasKey(line => line.Id);

        builder.Property(line => line.MembershipNumber).HasMaxLength(32).IsRequired();
        builder.Property(line => line.FullName).HasMaxLength(200).IsRequired();

        builder.Property(line => line.BasisAmount)
            .HasColumnType(AkibaDbContext.MoneyColumnType).IsRequired();

        builder.Property(line => line.Amount)
            .HasColumnType(AkibaDbContext.MoneyColumnType).IsRequired();

        builder.Property(line => line.CurrencyCode).HasMaxLength(3).IsRequired();

        builder.HasIndex(line => new { line.DividendRunId, line.Sequence }).IsUnique();

        // "What did this member get in 2026?" is the question a member asks, and the one the
        // office should not have to scan a whole run to answer.
        builder.HasIndex(line => line.MemberId);
    }
}
