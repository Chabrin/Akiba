namespace Akiba.Domain.Ledger;

/// <summary>The five account classes of double-entry bookkeeping.</summary>
public enum AccountType
{
    /// <summary>What Akiba owns or is owed: Bank, Loans Receivable, Interest Receivable.</summary>
    Asset = 1,

    /// <summary>
    /// What Akiba owes: Member Shares, Dividends Payable, Unallocated Receipts.
    /// </summary>
    /// <remarks>
    /// Member shares are a liability, not equity. Akiba owes that money back to the member,
    /// and reading it any other way makes the balance sheet wrong.
    /// </remarks>
    Liability = 2,

    /// <summary>Opening Balance Equity, Retained Surplus.</summary>
    Equity = 3,

    /// <summary>Loan Interest Income, Restructuring Fees.</summary>
    Income = 4,

    /// <summary>Bank Charges.</summary>
    Expense = 5,
}

/// <summary>Which side of an account increases it.</summary>
public enum BalanceSide
{
    Debit = 1,
    Credit = 2,
}

public static class AccountTypeExtensions
{
    /// <summary>
    /// The side on which this kind of account normally carries a positive balance.
    /// </summary>
    /// <remarks>
    /// Journal lines are stored as signed amounts with debits positive, so a raw sum over a
    /// credit-normal account comes out negative. This is what turns that raw sum back into
    /// the number a treasurer expects to read.
    /// </remarks>
    public static BalanceSide NormalBalance(this AccountType type) => type switch
    {
        AccountType.Asset or AccountType.Expense => BalanceSide.Debit,
        AccountType.Liability or AccountType.Equity or AccountType.Income => BalanceSide.Credit,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown account type."),
    };

    /// <summary>
    /// Appears on the balance sheet (assets, liabilities, equity) rather than in the income
    /// and expenditure statement.
    /// </summary>
    public static bool IsBalanceSheetAccount(this AccountType type) =>
        type is AccountType.Asset or AccountType.Liability or AccountType.Equity;
}
