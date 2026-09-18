using Akiba.Application.Abstractions;
using Akiba.Infrastructure.Identity;
using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Akiba.Infrastructure.Persistence;

/// <summary>
/// Akiba's database.
/// </summary>
/// <remarks>
/// <para>
/// Every monetary column is <c>numeric(19,4)</c>. The four decimal places are headroom for
/// intermediate rate arithmetic; money is rounded to two at the point of posting. There is no
/// <c>double precision</c> column anywhere in this schema and there must never be one - see
/// ARCHITECTURE.md section 3.
/// </para>
/// <para>
/// There are no balance columns either. A member's shareholding, a loan's outstanding balance
/// and the bank position are derived by summing journal lines, and adding a column to cache
/// any of them would mean holding two numbers with no way to tell which is real.
/// </para>
/// </remarks>
public sealed class AkibaDbContext : IdentityDbContext<AkibaUser, AkibaRole, Guid>, IUnitOfWork
{
    /// <summary>The schema every Akiba table lives in.</summary>
    public const string Schema = "akiba";

    /// <summary>The type every monetary column uses.</summary>
    internal const string MoneyColumnType = "numeric(19,4)";

    public AkibaDbContext(DbContextOptions<AkibaDbContext> options)
        : base(options)
    {
    }

    internal DbSet<AccountRow> Accounts => Set<AccountRow>();

    internal DbSet<JournalEntryRow> JournalEntries => Set<JournalEntryRow>();

    internal DbSet<JournalLineRow> JournalLines => Set<JournalLineRow>();

    internal DbSet<AccountingPeriodRow> AccountingPeriods => Set<AccountingPeriodRow>();

    internal DbSet<BorrowerRow> Borrowers => Set<BorrowerRow>();

    internal DbSet<AttachedDocumentRow> AttachedDocuments => Set<AttachedDocumentRow>();

    internal DbSet<ZoneRow> Zones => Set<ZoneRow>();

    internal DbSet<ZoneRepresentativeRow> ZoneRepresentatives => Set<ZoneRepresentativeRow>();

    internal DbSet<LoanApplicationRow> LoanApplications => Set<LoanApplicationRow>();

    internal DbSet<ApprovalDecisionRow> ApprovalDecisions => Set<ApprovalDecisionRow>();

    internal DbSet<GuaranteeRow> Guarantees => Set<GuaranteeRow>();

    internal DbSet<LoanSecurityRow> LoanSecurity => Set<LoanSecurityRow>();

    internal DbSet<LoanRow> Loans => Set<LoanRow>();

    internal DbSet<ReceiptRow> Receipts => Set<ReceiptRow>();

    internal DbSet<ReceiptAllocationRow> ReceiptAllocations => Set<ReceiptAllocationRow>();

    internal DbSet<BankReconciliationRow> BankReconciliations => Set<BankReconciliationRow>();

    internal DbSet<BankStatementLineRow> BankStatementLines => Set<BankStatementLineRow>();

    internal DbSet<DividendRunRow> DividendRuns => Set<DividendRunRow>();

    internal DbSet<DividendLineRow> DividendLines => Set<DividendLineRow>();

    /// <summary>
    /// The audit trail, mapped so it can be read and exported.
    /// </summary>
    /// <remarks>
    /// Written by the audit provider through raw SQL on the connection that made the change,
    /// never through this change tracker - auditing the audit would be a loop. The table also
    /// carries a trigger refusing UPDATE and DELETE.
    /// </remarks>
    internal DbSet<AuditEntryRow> AuditEntries => Set<AuditEntryRow>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.HasDefaultSchema(Schema);

        // IdentityDbContext maps the user and role tables; this must run before Akiba's own
        // configurations so they can override anything they need to.
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(AkibaDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        base.ConfigureConventions(configurationBuilder);

        // Belt and braces: even a decimal property somebody forgets to configure gets the
        // right column type rather than silently defaulting to a lower precision.
        configurationBuilder.Properties<decimal>().HaveColumnType(MoneyColumnType);

        configurationBuilder.Properties<string>().HaveMaxLength(256);
    }
}
