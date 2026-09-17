using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akiba.Infrastructure.Persistence.Configurations;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<AccountRow>
{
    public void Configure(EntityTypeBuilder<AccountRow> builder)
    {
        builder.ToTable("accounts");
        builder.HasKey(account => account.Id);

        builder.Property(account => account.Code).HasMaxLength(32).IsRequired();
        builder.Property(account => account.Name).HasMaxLength(200).IsRequired();

        // Officials refer to accounts by code, on paper and in conversation, so a duplicate
        // would be a real ambiguity rather than a technical one.
        builder.HasIndex(account => account.Code).IsUnique();

        // Finding a member's shares or a loan's receivable is the most common lookup in the
        // system, because every derived balance starts with it.
        builder.HasIndex(account => new { account.OwnerKind, account.OwnerId });
    }
}

internal sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntryRow>
{
    public void Configure(EntityTypeBuilder<JournalEntryRow> builder)
    {
        builder.ToTable("journal_entries");
        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Narration).HasMaxLength(500).IsRequired();
        builder.Property(entry => entry.SourceDocumentReference).HasMaxLength(100);
        builder.Property(entry => entry.PostedByName).HasMaxLength(200).IsRequired();

        // Entry date drives every "as at" balance and every period close test, so it carries
        // the index the ledger is actually read by.
        builder.HasIndex(entry => entry.EntryDate);

        builder.HasOne<JournalEntryRow>()
            .WithMany()
            .HasForeignKey(entry => entry.ReversesEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(entry => entry.Lines)
            .WithOne()
            .HasForeignKey(line => line.JournalEntryId)
            // Restrict, not Cascade. Financial records are never hard-deleted, so a cascade
            // path is a route that should not exist for anything to travel down.
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(entry => entry.Lines).AutoInclude();
    }
}

internal sealed class JournalLineConfiguration : IEntityTypeConfiguration<JournalLineRow>
{
    public void Configure(EntityTypeBuilder<JournalLineRow> builder)
    {
        builder.ToTable("journal_lines");
        builder.HasKey(line => line.Id);

        builder.Property(line => line.SignedAmount)
            .HasColumnType(AkibaDbContext.MoneyColumnType)
            .IsRequired();

        builder.Property(line => line.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(line => line.Narration).HasMaxLength(500);

        // Deriving a balance means summing every line on one account up to a date. This index
        // is the one that makes that the cheap operation it needs to be, given there is no
        // stored balance to fall back on.
        builder.HasIndex(line => line.AccountId);
        builder.HasIndex(line => new { line.JournalEntryId, line.Sequence });

        builder.HasOne<AccountRow>()
            .WithMany()
            .HasForeignKey(line => line.AccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AccountingPeriodConfiguration : IEntityTypeConfiguration<AccountingPeriodRow>
{
    public void Configure(EntityTypeBuilder<AccountingPeriodRow> builder)
    {
        builder.ToTable("accounting_periods");
        builder.HasKey(period => period.Id);

        builder.Property(period => period.ClosedByName).HasMaxLength(200);
        builder.Property(period => period.ReopenedByName).HasMaxLength(200);
        builder.Property(period => period.ReopenedReason).HasMaxLength(1000);

        // One month period and one year period per span. Two open months covering September
        // would let an entry be simultaneously in a closed period and an open one.
        builder.HasIndex(period => new { period.Kind, period.Start }).IsUnique();
    }
}
