using Akiba.Application.Abstractions;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using MediatR;

namespace Akiba.Application.Ledger;

/// <summary>One account on the trial balance.</summary>
/// <param name="Code">The code officials quote.</param>
/// <param name="Name">What it is.</param>
/// <param name="Type">Asset, liability, equity, income or expense.</param>
/// <param name="Balance">Its balance as an official reads it - positive on its normal side.</param>
/// <param name="SignedBalance">Debits positive, so the column sums to zero.</param>
public sealed record AccountBalance(
    string Code,
    string Name,
    AccountType Type,
    Money Balance,
    Money SignedBalance);

/// <summary>
/// The trial balance as at a date.
/// </summary>
/// <param name="AsAt">The date.</param>
/// <param name="Accounts">Every account with a balance, in code order.</param>
/// <param name="Difference">
/// The sum of the signed balances. Zero, or something wrote to the database directly.
/// </param>
public sealed record TrialBalance(
    DateOnly AsAt,
    IReadOnlyList<AccountBalance> Accounts,
    Money Difference)
{
    public bool Balances => Difference.IsZero;

    public Money TotalDebits =>
        Accounts.Where(a => a.SignedBalance.IsPositive).Sum(a => a.SignedBalance, Currency.Kes);

    public Money TotalCredits =>
        Accounts.Where(a => a.SignedBalance.IsNegative).Sum(a => -a.SignedBalance, Currency.Kes);
}

/// <summary>Builds the trial balance as at any date.</summary>
public sealed record GetTrialBalanceQuery(DateOnly AsAt) : IRequest<TrialBalance>;

internal sealed class GetTrialBalanceHandler : IRequestHandler<GetTrialBalanceQuery, TrialBalance>
{
    private readonly IAccountRepository _accounts;
    private readonly IJournalRepository _journal;

    public GetTrialBalanceHandler(IAccountRepository accounts, IJournalRepository journal)
    {
        _accounts = accounts;
        _journal = journal;
    }

    public async Task<TrialBalance> Handle(
        GetTrialBalanceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var accounts = await _accounts.AllAsync(cancellationToken).ConfigureAwait(false);
        var entries = await _journal.AsOfAsync(query.AsAt, cancellationToken).ConfigureAwait(false);

        var signed = LedgerBalances.AllSignedBalancesAsAt(entries, query.AsAt, Currency.Kes);

        var rows = accounts
            .Select(account => new
            {
                Account = account,
                Signed = signed.TryGetValue(account.Id, out var balance) ? balance : Money.ZeroKes,
            })
            // An account nobody has posted to is not interesting on a trial balance, and there
            // is one per member and one per loan.
            .Where(row => !row.Signed.IsZero)
            .Select(row => new AccountBalance(
                row.Account.Code.Value,
                row.Account.Name,
                row.Account.Type,
                row.Account.NormalBalance == BalanceSide.Debit ? row.Signed : -row.Signed,
                row.Signed))
            .OrderBy(row => row.Code, StringComparer.Ordinal)
            .ToList();

        return new TrialBalance(
            query.AsAt,
            rows,
            LedgerBalances.TrialBalanceDifferenceAsAt(entries, query.AsAt, Currency.Kes));
    }
}

/// <summary>An account in the chart, with what it currently holds.</summary>
public sealed record ChartAccount(
    AccountId Id,
    string Code,
    string Name,
    AccountType Type,
    BalanceSide NormalBalance,
    AccountOwnerKind OwnerKind,
    Money Balance,
    bool IsOpen);

/// <summary>
/// The chart of accounts as at a date.
/// </summary>
/// <param name="AsAt">The date balances are stated as at.</param>
/// <param name="IncludeMemberAndLoanAccounts">
/// Whether to include the per-member and per-loan accounts. Off by default - there is one for
/// every member and every loan, and the chart itself is the nine society accounts.
/// </param>
public sealed record GetChartOfAccountsQuery(DateOnly AsAt, bool IncludeMemberAndLoanAccounts = false)
    : IRequest<IReadOnlyList<ChartAccount>>;

internal sealed class GetChartOfAccountsHandler
    : IRequestHandler<GetChartOfAccountsQuery, IReadOnlyList<ChartAccount>>
{
    private readonly IAccountRepository _accounts;
    private readonly IJournalRepository _journal;

    public GetChartOfAccountsHandler(IAccountRepository accounts, IJournalRepository journal)
    {
        _accounts = accounts;
        _journal = journal;
    }

    public async Task<IReadOnlyList<ChartAccount>> Handle(
        GetChartOfAccountsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var accounts = await _accounts.AllAsync(cancellationToken).ConfigureAwait(false);
        var entries = await _journal.AsOfAsync(query.AsAt, cancellationToken).ConfigureAwait(false);
        var signed = LedgerBalances.AllSignedBalancesAsAt(entries, query.AsAt, Currency.Kes);

        return
        [
            .. accounts
                .Where(account => query.IncludeMemberAndLoanAccounts
                    || account.Owner.Kind == AccountOwnerKind.Society)
                .Select(account =>
                {
                    var balance = signed.TryGetValue(account.Id, out var value) ? value : Money.ZeroKes;

                    return new ChartAccount(
                        account.Id,
                        account.Code.Value,
                        account.Name,
                        account.Type,
                        account.NormalBalance,
                        account.Owner.Kind,
                        account.NormalBalance == BalanceSide.Debit ? balance : -balance,
                        account.IsOpen);
                })
                .OrderBy(account => account.Code, StringComparer.Ordinal),
        ];
    }
}

/// <summary>One line of the journal, as the journal page shows it.</summary>
public sealed record JournalLineView(string AccountCode, string AccountName, Money SignedAmount);

/// <summary>One entry in the journal.</summary>
public sealed record JournalEntryView(
    JournalEntryId Id,
    DateOnly EntryDate,
    DateOnly ValueDate,
    string Narration,
    string SourceDocument,
    string PostedBy,
    Money Total,
    bool IsReversal,
    IReadOnlyList<JournalLineView> Lines);

/// <summary>
/// The journal between two dates.
/// </summary>
/// <remarks>
/// Entries in the order they were posted, with every line, because this is the screen an
/// official uses to walk back from a figure to the posting that made it.
/// </remarks>
public sealed record ListJournalEntriesQuery(DateOnly From, DateOnly To)
    : IRequest<IReadOnlyList<JournalEntryView>>;

internal sealed class ListJournalEntriesHandler
    : IRequestHandler<ListJournalEntriesQuery, IReadOnlyList<JournalEntryView>>
{
    private readonly IJournalRepository _journal;
    private readonly IAccountRepository _accounts;

    public ListJournalEntriesHandler(IJournalRepository journal, IAccountRepository accounts)
    {
        _journal = journal;
        _accounts = accounts;
    }

    public async Task<IReadOnlyList<JournalEntryView>> Handle(
        ListJournalEntriesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var entries = await _journal
            .BetweenAsync(query.From, query.To, cancellationToken)
            .ConfigureAwait(false);

        var accounts = (await _accounts.AllAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(account => account.Id);

        return
        [
            .. entries
                .OrderByDescending(entry => entry.EntryDate)
                .ThenByDescending(entry => entry.PostedAtUtc)
                .Select(entry => new JournalEntryView(
                    entry.Id,
                    entry.EntryDate,
                    entry.ValueDate,
                    entry.Narration,
                    entry.SourceDocument.ToString(),
                    entry.PostedBy.DisplayName,
                    entry.Total,
                    entry.IsReversal,
                    [
                        .. entry.Lines.Select(line => new JournalLineView(
                            accounts.TryGetValue(line.AccountId, out var account)
                                ? account.Code.Value
                                : "?",
                            accounts.TryGetValue(line.AccountId, out var named)
                                ? named.Name
                                : "(unknown account)",
                            line.SignedAmount)),
                    ])),
        ];
    }
}
