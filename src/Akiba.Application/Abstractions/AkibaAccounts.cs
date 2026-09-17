using Akiba.Domain.Ledger;

namespace Akiba.Application.Abstractions;

/// <summary>
/// The society-wide accounts a handler needs, looked up once.
/// </summary>
/// <remarks>
/// Every posting needs Bank or Unallocated Receipts, and fetching them by code at each call
/// site would put the chart of accounts' string constants into a dozen handlers. This fetches
/// them once and fails clearly if the chart was never seeded.
/// </remarks>
public interface IAkibaAccounts
{
    Task<AccountId> BankAsync(CancellationToken cancellationToken = default);

    Task<AccountId> UnallocatedReceiptsAsync(CancellationToken cancellationToken = default);

    Task<AccountId> LoanInterestIncomeAsync(CancellationToken cancellationToken = default);

    Task<AccountId> RestructuringFeesAsync(CancellationToken cancellationToken = default);

    Task<AccountId> BankChargesAsync(CancellationToken cancellationToken = default);

    Task<AccountId> OpeningBalanceEquityAsync(CancellationToken cancellationToken = default);

    Task<AccountId> DividendsPayableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a share account for a new member, or a receivable for a new loan. These are the
    /// two account kinds that are created as the society goes along rather than seeded.
    /// </summary>
    Task<Account> OpenMemberSharesAccountAsync(
        string membershipNumber, string memberName, Guid memberId,
        CancellationToken cancellationToken = default);

    Task<Account> OpenLoanReceivableAccountAsync(
        string loanNumber, Guid loanId, CancellationToken cancellationToken = default);
}
