using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;

namespace Akiba.Domain.Tests.Ledger;

/// <summary>
/// Shared setup for the ledger tests: a small chart of accounts and a clerk to post with.
/// </summary>
/// <remarks>
/// Names are realistic Kenyan names and the amounts are plausible, but none of this is real
/// member data and none of it ever will be.
/// </remarks>
internal static class LedgerFixture
{
    public static Actor Clerk { get; } = new(Guid.Parse("0000A11B-0000-0000-0000-000000000001"), "Oliver Kamau");

    public static Actor Treasurer { get; } = new(Guid.Parse("0000A11B-0000-0000-0000-000000000002"), "Wilfred Wamai");

    public static Actor Chairman { get; } = new(Guid.Parse("0000A11B-0000-0000-0000-000000000003"), "Dennis Gitonga");

    public static DateTimeOffset Now { get; } = new(2026, 9, 17, 9, 30, 0, TimeSpan.Zero);

    public static Account Bank { get; } =
        Account.OpenSocietyAccount(AccountCode.Of(ChartOfAccounts.Bank), "Bank", AccountType.Asset);

    public static Account InterestIncome { get; } = Account.OpenSocietyAccount(
        AccountCode.Of(ChartOfAccounts.LoanInterestIncome), "Loan Interest Income", AccountType.Income);

    public static Account UnallocatedReceipts { get; } = Account.OpenSocietyAccount(
        AccountCode.Of(ChartOfAccounts.UnallocatedReceipts), "Unallocated Receipts", AccountType.Liability);

    public static Account BankCharges { get; } = Account.OpenSocietyAccount(
        AccountCode.Of(ChartOfAccounts.BankCharges), "Bank Charges", AccountType.Expense);

    public static Account SharesOf(string memberName, Guid memberId) =>
        Account.OpenMemberSharesAccount(
            ChartOfAccounts.MemberSharesCode(memberId.ToString()[..8]), memberName, memberId);

    public static Account ReceivableFor(string loanNumber, Guid loanId) =>
        Account.OpenLoanReceivableAccount(
            ChartOfAccounts.LoanReceivableCode(loanNumber), loanNumber, loanId);

    /// <summary>A share contribution: money into the bank, owed back to the member.</summary>
    public static JournalEntry ShareContribution(
        Account memberShares, decimal amount, DateOnly on) =>
        JournalEntry.Post(
            on,
            $"Share contribution - {memberShares.Name}",
            SourceDocument.PayrollSchedule(on.Year, on.Month),
            Clerk,
            Now,
            [
                JournalLine.Debit(Bank.Id, Money.Kes(amount)),
                JournalLine.Credit(memberShares.Id, Money.Kes(amount)),
            ]);

    /// <summary>
    /// A loan disbursement with its flat interest recognised at once: the member owes
    /// principal plus interest, the bank pays out the principal, and the difference is income.
    /// </summary>
    public static JournalEntry Disbursement(
        Account loanReceivable, decimal principal, decimal interest, DateOnly on) =>
        JournalEntry.Post(
            on,
            $"Disbursement - {loanReceivable.Name}",
            SourceDocument.Cheque("000431"),
            Clerk,
            Now,
            [
                JournalLine.Debit(loanReceivable.Id, Money.Kes(principal + interest)),
                JournalLine.Credit(Bank.Id, Money.Kes(principal)),
                JournalLine.Credit(InterestIncome.Id, Money.Kes(interest)),
            ]);

    /// <summary>A repayment: money in, the member owes less.</summary>
    public static JournalEntry Repayment(
        Account loanReceivable, decimal amount, DateOnly on) =>
        JournalEntry.Post(
            on,
            $"Instalment - {loanReceivable.Name}",
            SourceDocument.PayrollSchedule(on.Year, on.Month),
            Clerk,
            Now,
            [
                JournalLine.Debit(Bank.Id, Money.Kes(amount)),
                JournalLine.Credit(loanReceivable.Id, Money.Kes(amount)),
            ]);
}
