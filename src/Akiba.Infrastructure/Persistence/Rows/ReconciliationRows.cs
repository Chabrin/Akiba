namespace Akiba.Infrastructure.Persistence.Rows;

/// <summary>A bank statement imported for reconciliation, and how far it has got.</summary>
internal sealed class BankReconciliationRow
{
    public Guid Id { get; set; }

    public Guid BankAccountId { get; set; }

    public string AccountLabel { get; set; } = string.Empty;

    public DateOnly From { get; set; }

    public DateOnly To { get; set; }

    public decimal OpeningBalance { get; set; }

    public decimal ClosingBalance { get; set; }

    public string CurrencyCode { get; set; } = "KES";

    public int Status { get; set; }

    public Guid? SignedOffByUserId { get; set; }

    public string? SignedOffByName { get; set; }

    public DateTimeOffset? SignedOffAtUtc { get; set; }

    public List<BankStatementLineRow> Lines { get; set; } = [];
}

/// <summary>
/// One line of a bank statement, stored as the bank wrote it.
/// </summary>
/// <remarks>
/// <see cref="Amount"/> is always positive and <see cref="Direction"/> carries the sign, which
/// is how a bank prints a statement. The signed figure the reconciliation arithmetic uses is
/// derived from the two, so there is no second copy to disagree with the first.
/// </remarks>
internal sealed class BankStatementLineRow
{
    public Guid Id { get; set; }

    public Guid BankReconciliationId { get; set; }

    public int LineNumber { get; set; }

    public DateOnly ValueDate { get; set; }

    public string Description { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string CurrencyCode { get; set; } = "KES";

    public int Direction { get; set; }

    public string? BankReference { get; set; }

    public int State { get; set; }

    public Guid? MatchedToId { get; set; }

    public string? MatchedToDescription { get; set; }

    public string? NotOursReason { get; set; }

    public Guid? DecidedByUserId { get; set; }

    public string? DecidedByName { get; set; }
}
