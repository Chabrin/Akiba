namespace Akiba.Domain.Ledger;

/// <summary>
/// The society-wide accounts Akiba is seeded with, and the code prefixes used for the
/// per-member and per-loan accounts.
/// </summary>
/// <remarks>
/// Codes follow the usual convention - 1000s assets, 2000s liabilities, 3000s equity,
/// 4000s income, 5000s expenses - because officials read codes on paper and expect them to
/// sort that way. The chart is extensible: adding an account is adding a row here, not a
/// change to any calculation.
/// </remarks>
public static class ChartOfAccounts
{
    // Assets
    public const string Bank = "1000";
    public const string InterestReceivable = "1100";

    /// <summary>Prefix for per-loan receivable accounts, e.g. <c>1200-AKB-2026-0007</c>.</summary>
    public const string LoansReceivablePrefix = "1200";

    // Liabilities
    /// <summary>Prefix for per-member share accounts, e.g. <c>2100-0042</c>.</summary>
    public const string MemberSharesPrefix = "2100";

    public const string DividendsPayable = "2200";

    /// <summary>
    /// Where every receipt lands before it is allocated.
    /// </summary>
    /// <remarks>
    /// A liability because the money has arrived but Akiba has not yet said what it is for -
    /// until the accounts clerk allocates it, it is still the member's.
    /// </remarks>
    public const string UnallocatedReceipts = "2300";

    // Equity
    public const string OpeningBalanceEquity = "3000";
    public const string RetainedSurplus = "3100";

    // Income
    public const string LoanInterestIncome = "4000";
    public const string RestructuringFees = "4100";

    // Expenses
    public const string BankCharges = "5000";

    /// <summary>The society-wide accounts created at go-live.</summary>
    public static IReadOnlyList<SeedAccount> SocietyAccounts { get; } =
    [
        new(Bank, "Bank", AccountType.Asset),
        new(InterestReceivable, "Interest Receivable", AccountType.Asset),
        new(DividendsPayable, "Dividends Payable", AccountType.Liability),
        new(UnallocatedReceipts, "Unallocated Receipts", AccountType.Liability),
        new(OpeningBalanceEquity, "Opening Balance Equity", AccountType.Equity),
        new(RetainedSurplus, "Retained Surplus", AccountType.Equity),
        new(LoanInterestIncome, "Loan Interest Income", AccountType.Income),
        new(RestructuringFees, "Restructuring Fees", AccountType.Income),
        new(BankCharges, "Bank Charges", AccountType.Expense),
    ];

    /// <summary>The account code for a member's shares, from their membership number.</summary>
    public static AccountCode MemberSharesCode(string membershipNumber) =>
        AccountCode.Of($"{MemberSharesPrefix}-{membershipNumber}");

    /// <summary>The account code for a loan's receivable, from its loan number.</summary>
    public static AccountCode LoanReceivableCode(string loanNumber) =>
        AccountCode.Of($"{LoansReceivablePrefix}-{loanNumber}");

    /// <summary>One account in the seeded chart.</summary>
    public sealed record SeedAccount(string Code, string Name, AccountType Type)
    {
        public Account ToAccount() => Account.OpenSocietyAccount(AccountCode.Of(Code), Name, Type);
    }
}
