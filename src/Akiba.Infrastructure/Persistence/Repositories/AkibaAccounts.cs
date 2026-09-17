using Akiba.Application.Abstractions;
using Akiba.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Akiba.Infrastructure.Persistence.Repositories;

/// <summary>
/// Looks up the society-wide accounts by code, and opens the per-member and per-loan ones.
/// </summary>
/// <remarks>
/// Caches the society accounts for the lifetime of the request. They are created once at
/// startup and never change, so re-reading them for every posting in a command would be
/// several round trips to learn the same thing.
/// </remarks>
internal sealed class AkibaAccounts : IAkibaAccounts
{
    private readonly AkibaDbContext _context;
    private readonly Dictionary<string, AccountId> _cache = new(StringComparer.Ordinal);

    public AkibaAccounts(AkibaDbContext context) => _context = context;

    public Task<AccountId> BankAsync(CancellationToken cancellationToken = default) =>
        ByCodeAsync(ChartOfAccounts.Bank, cancellationToken);

    public Task<AccountId> UnallocatedReceiptsAsync(CancellationToken cancellationToken = default) =>
        ByCodeAsync(ChartOfAccounts.UnallocatedReceipts, cancellationToken);

    public Task<AccountId> LoanInterestIncomeAsync(CancellationToken cancellationToken = default) =>
        ByCodeAsync(ChartOfAccounts.LoanInterestIncome, cancellationToken);

    public Task<AccountId> RestructuringFeesAsync(CancellationToken cancellationToken = default) =>
        ByCodeAsync(ChartOfAccounts.RestructuringFees, cancellationToken);

    public Task<AccountId> BankChargesAsync(CancellationToken cancellationToken = default) =>
        ByCodeAsync(ChartOfAccounts.BankCharges, cancellationToken);

    public Task<AccountId> OpeningBalanceEquityAsync(CancellationToken cancellationToken = default) =>
        ByCodeAsync(ChartOfAccounts.OpeningBalanceEquity, cancellationToken);

    public Task<AccountId> DividendsPayableAsync(CancellationToken cancellationToken = default) =>
        ByCodeAsync(ChartOfAccounts.DividendsPayable, cancellationToken);

    public async Task<Account> OpenMemberSharesAccountAsync(
        string membershipNumber,
        string memberName,
        Guid memberId,
        CancellationToken cancellationToken = default)
    {
        var account = Account.OpenMemberSharesAccount(
            ChartOfAccounts.MemberSharesCode(membershipNumber), memberName, memberId);

        await AddAsync(account, cancellationToken).ConfigureAwait(false);

        return account;
    }

    public async Task<Account> OpenLoanReceivableAccountAsync(
        string loanNumber,
        Guid loanId,
        CancellationToken cancellationToken = default)
    {
        var account = Account.OpenLoanReceivableAccount(
            ChartOfAccounts.LoanReceivableCode(loanNumber), loanNumber, loanId);

        await AddAsync(account, cancellationToken).ConfigureAwait(false);

        return account;
    }

    private async Task AddAsync(Account account, CancellationToken cancellationToken)
    {
        var exists = await _context.Accounts
            .AnyAsync(row => row.Code == account.Code.Value, cancellationToken)
            .ConfigureAwait(false);

        if (exists)
        {
            throw new InvalidOperationException(
                $"Account code {account.Code} is already in use. Officials refer to accounts by " +
                "code on paper, so a duplicate is a real ambiguity.");
        }

        _context.Accounts.Add(LedgerMapper.ToRow(account));
    }

    private async Task<AccountId> ByCodeAsync(string code, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(code, out var cached))
        {
            return cached;
        }

        var row = await _context.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(account => account.Code == code, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"The ledger account {code} does not exist. The chart of accounts is seeded at " +
                "startup - if it is missing, the database was not brought up correctly.");

        var id = new AccountId(row.Id);
        _cache[code] = id;

        return id;
    }
}
