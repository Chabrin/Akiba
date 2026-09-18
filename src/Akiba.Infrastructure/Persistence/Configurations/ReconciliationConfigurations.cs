using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akiba.Infrastructure.Persistence.Configurations;

internal sealed class BankReconciliationConfiguration
    : IEntityTypeConfiguration<BankReconciliationRow>
{
    public void Configure(EntityTypeBuilder<BankReconciliationRow> builder)
    {
        builder.ToTable("bank_reconciliations");
        builder.HasKey(reconciliation => reconciliation.Id);

        builder.Property(reconciliation => reconciliation.AccountLabel)
            .HasMaxLength(200).IsRequired();

        builder.Property(reconciliation => reconciliation.OpeningBalance)
            .HasColumnType(AkibaDbContext.MoneyColumnType).IsRequired();

        builder.Property(reconciliation => reconciliation.ClosingBalance)
            .HasColumnType(AkibaDbContext.MoneyColumnType).IsRequired();

        builder.Property(reconciliation => reconciliation.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(reconciliation => reconciliation.SignedOffByName).HasMaxLength(200);

        // Two statements for the same account over the same period would be the same statement
        // imported twice, and the second import's unmatched lines would look like a genuine
        // difference.
        builder.HasIndex(reconciliation =>
            new { reconciliation.BankAccountId, reconciliation.From, reconciliation.To })
            .IsUnique();

        // The period close asks which reconciliations overlap a month.
        builder.HasIndex(reconciliation => reconciliation.To);

        builder.HasOne<AccountRow>()
            .WithMany()
            .HasForeignKey(reconciliation => reconciliation.BankAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(reconciliation => reconciliation.Lines)
            .WithOne()
            .HasForeignKey(line => line.BankReconciliationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(reconciliation => reconciliation.Lines).AutoInclude();
    }
}

internal sealed class BankStatementLineConfiguration
    : IEntityTypeConfiguration<BankStatementLineRow>
{
    public void Configure(EntityTypeBuilder<BankStatementLineRow> builder)
    {
        builder.ToTable("bank_statement_lines");
        builder.HasKey(line => line.Id);

        builder.Property(line => line.Description).HasMaxLength(500).IsRequired();

        builder.Property(line => line.Amount)
            .HasColumnType(AkibaDbContext.MoneyColumnType).IsRequired();

        builder.Property(line => line.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(line => line.BankReference).HasMaxLength(100);
        builder.Property(line => line.MatchedToDescription).HasMaxLength(500);
        builder.Property(line => line.NotOursReason).HasMaxLength(500);
        builder.Property(line => line.DecidedByName).HasMaxLength(200);

        // A line number identifies a line to whoever has the paper statement in front of them.
        builder.HasIndex(line => new { line.BankReconciliationId, line.LineNumber }).IsUnique();

        // Asked when checking whether an entry is already matched somewhere.
        builder.HasIndex(line => line.MatchedToId);
    }
}
