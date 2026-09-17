using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akiba.Infrastructure.Persistence.Configurations;

internal sealed class LoanApplicationConfiguration : IEntityTypeConfiguration<LoanApplicationRow>
{
    public void Configure(EntityTypeBuilder<LoanApplicationRow> builder)
    {
        builder.ToTable("loan_applications");
        builder.HasKey(application => application.Id);

        builder.Property(application => application.RequestedPrincipal)
            .HasColumnType(AkibaDbContext.MoneyColumnType);
        builder.Property(application => application.DeclaredGrossSalary)
            .HasColumnType(AkibaDbContext.MoneyColumnType);
        builder.Property(application => application.ApprovedPrincipal)
            .HasColumnType(AkibaDbContext.MoneyColumnType);
        builder.Property(application => application.ApprovedInterest)
            .HasColumnType(AkibaDbContext.MoneyColumnType);
        builder.Property(application => application.MonthlyRentalIncome)
            .HasColumnType(AkibaDbContext.MoneyColumnType);

        builder.Property(application => application.RejectionReason).HasMaxLength(1000);
        builder.Property(application => application.PropertyName).HasMaxLength(200);
        builder.Property(application => application.PropertyLocation).HasMaxLength(300);

        // The queue the office actually watches: approved but not yet disbursed, often
        // because the form arrived after the 15th.
        builder.HasIndex(application => new { application.Status, application.ConsiderationMonth });
        builder.HasIndex(application => application.BorrowerId);

        builder.HasMany(application => application.Decisions)
            .WithOne()
            .HasForeignKey(decision => decision.LoanApplicationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(application => application.Guarantees)
            .WithOne()
            .HasForeignKey(guarantee => guarantee.LoanApplicationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(application => application.Security)
            .WithOne()
            .HasForeignKey(security => security.LoanApplicationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(application => application.Documents)
            .WithOne()
            .HasForeignKey(document => document.LoanApplicationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(application => application.Decisions).AutoInclude();
        builder.Navigation(application => application.Guarantees).AutoInclude();
        builder.Navigation(application => application.Security).AutoInclude();
        builder.Navigation(application => application.Documents).AutoInclude();
    }
}

internal sealed class ApprovalDecisionConfiguration : IEntityTypeConfiguration<ApprovalDecisionRow>
{
    public void Configure(EntityTypeBuilder<ApprovalDecisionRow> builder)
    {
        builder.ToTable("approval_decisions");
        builder.HasKey(decision => decision.Id);

        builder.Property(decision => decision.ApproverName).HasMaxLength(200).IsRequired();
        builder.Property(decision => decision.Comment).HasMaxLength(1000);

        // One decision per representative per application.
        builder.HasIndex(decision => new { decision.LoanApplicationId, decision.ApproverUserId })
            .IsUnique();
    }
}

internal sealed class GuaranteeConfiguration : IEntityTypeConfiguration<GuaranteeRow>
{
    public void Configure(EntityTypeBuilder<GuaranteeRow> builder)
    {
        builder.ToTable("guarantees");
        builder.HasKey(guarantee => guarantee.Id);

        builder.Property(guarantee => guarantee.GuarantorName).HasMaxLength(200).IsRequired();
        builder.Property(guarantee => guarantee.GuarantorPayrollNumber).HasMaxLength(20).IsRequired();
        builder.Property(guarantee => guarantee.GuaranteedAmount)
            .HasColumnType(AkibaDbContext.MoneyColumnType);
        builder.Property(guarantee => guarantee.ShareValueAtSigning)
            .HasColumnType(AkibaDbContext.MoneyColumnType);

        // The guarantor exposure report reads by guarantor, and the exit review reads it for
        // one member at a time, so this is the index that matters.
        builder.HasIndex(guarantee => guarantee.GuarantorId);
    }
}

internal sealed class LoanSecurityConfiguration : IEntityTypeConfiguration<LoanSecurityRow>
{
    public void Configure(EntityTypeBuilder<LoanSecurityRow> builder)
    {
        builder.ToTable("loan_security");
        builder.HasKey(security => security.Id);

        builder.Property(security => security.Details).HasMaxLength(500).IsRequired();
    }
}

internal sealed class LoanConfiguration : IEntityTypeConfiguration<LoanRow>
{
    public void Configure(EntityTypeBuilder<LoanRow> builder)
    {
        builder.ToTable("loans");
        builder.HasKey(loan => loan.Id);

        builder.Property(loan => loan.LoanNumber).HasMaxLength(40).IsRequired();
        builder.Property(loan => loan.Principal).HasColumnType(AkibaDbContext.MoneyColumnType);
        builder.Property(loan => loan.Interest).HasColumnType(AkibaDbContext.MoneyColumnType);
        builder.Property(loan => loan.ChequeAmount).HasColumnType(AkibaDbContext.MoneyColumnType);
        builder.Property(loan => loan.ChequeNumber).HasMaxLength(40).IsRequired();
        builder.Property(loan => loan.VoucherReference).HasMaxLength(40).IsRequired();
        builder.Property(loan => loan.ChequeSignatories).HasMaxLength(400).IsRequired();

        // The number written in the LOAN NO. box. Officials quote it, so it is unique.
        builder.HasIndex(loan => loan.LoanNumber).IsUnique();

        // "How many loans does this member have running?" is asked on every application,
        // because a member may hold two and no more.
        builder.HasIndex(loan => new { loan.BorrowerId, loan.Status });

        builder.HasOne<LoanRow>()
            .WithMany()
            .HasForeignKey(loan => loan.RestructuresLoanId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(loan => loan.Guarantees)
            .WithOne()
            .HasForeignKey(guarantee => guarantee.LoanId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(loan => loan.Guarantees).AutoInclude();
    }
}

internal sealed class ReceiptConfiguration : IEntityTypeConfiguration<ReceiptRow>
{
    public void Configure(EntityTypeBuilder<ReceiptRow> builder)
    {
        builder.ToTable("receipts");
        builder.HasKey(receipt => receipt.Id);

        builder.Property(receipt => receipt.Amount).HasColumnType(AkibaDbContext.MoneyColumnType);
        builder.Property(receipt => receipt.Reference).HasMaxLength(100).IsRequired();
        builder.Property(receipt => receipt.PayerNameOnSlip).HasMaxLength(200);
        builder.Property(receipt => receipt.BankStatementReference).HasMaxLength(200);

        // The two queues the clerk works from: what has not cleared, and what has cleared but
        // has not been allocated.
        builder.HasIndex(receipt => new { receipt.Status, receipt.ReceivedOn });
        builder.HasIndex(receipt => receipt.IdentifiedBorrowerId);

        builder.HasMany(receipt => receipt.Allocations)
            .WithOne()
            .HasForeignKey(allocation => allocation.ReceiptId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(receipt => receipt.Allocations).AutoInclude();
    }
}

internal sealed class ReceiptAllocationConfiguration : IEntityTypeConfiguration<ReceiptAllocationRow>
{
    public void Configure(EntityTypeBuilder<ReceiptAllocationRow> builder)
    {
        builder.ToTable("receipt_allocations");
        builder.HasKey(allocation => allocation.Id);

        builder.Property(allocation => allocation.Amount).HasColumnType(AkibaDbContext.MoneyColumnType);
        builder.Property(allocation => allocation.AllocatedByName).HasMaxLength(200).IsRequired();
        builder.Property(allocation => allocation.ReversedReason).HasMaxLength(500);

        builder.HasIndex(allocation => new { allocation.ReceiptId, allocation.Sequence });
    }
}
