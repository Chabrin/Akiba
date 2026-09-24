using Akiba.Application.Abstractions;
using Akiba.Domain.Financial;
using Akiba.Domain.Lending;
using Akiba.Domain.Membership;
using MediatR;

namespace Akiba.Application.Members;

/// <summary>A member as the members list shows them.</summary>
/// <param name="MemberId">Their id.</param>
/// <param name="MembershipNumber">Their number.</param>
/// <param name="PayrollNumber">What HR matches the deduction schedule on.</param>
/// <param name="Name">Their full name.</param>
/// <param name="Shareholding">Their shares as at the date asked for, derived from the ledger.</param>
/// <param name="BorrowingLimit">Twice their shareholding.</param>
/// <param name="RunningLoans">How many loans they hold. Two is the maximum.</param>
/// <param name="IsActive">False once they have left CAL.</param>
/// <param name="IsLandlord">Whether their deductions run on the landlord schedule.</param>
public sealed record MemberSummary(
    BorrowerId MemberId,
    string MembershipNumber,
    string PayrollNumber,
    string Name,
    Money Shareholding,
    Money BorrowingLimit,
    int RunningLoans,
    bool IsActive,
    bool IsLandlord)
{
    /// <summary>
    /// Which schedule this member is deducted on.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored, so it cannot disagree with <see cref="IsLandlord"/>. It
    /// exists so that every list in Akiba filters on the same thing by the same name - the
    /// members list, the loan book and the applications queue all speak of employees and
    /// landlords, and none of them works it out for itself.
    /// </remarks>
    public BorrowerCategory Category =>
        IsLandlord ? BorrowerCategory.Landlord : BorrowerCategory.Employee;
}

/// <summary>
/// Lists members with their shareholding as at a date.
/// </summary>
/// <remarks>
/// The date is a parameter rather than "today" because every figure Akiba reports is
/// answerable as at a past date, and a list that could only ever show today would be the one
/// screen that could not answer the question the ledger was designed to answer.
/// </remarks>
public sealed record ListMembersQuery(DateOnly AsAt, bool IncludeExited = false)
    : IRequest<IReadOnlyList<MemberSummary>>;

internal sealed class ListMembersHandler
    : IRequestHandler<ListMembersQuery, IReadOnlyList<MemberSummary>>
{
    private readonly IBorrowerRepository _borrowers;
    private readonly ILoanRepository _loans;
    private readonly IBalanceQueries _balances;

    public ListMembersHandler(
        IBorrowerRepository borrowers, ILoanRepository loans, IBalanceQueries balances)
    {
        _borrowers = borrowers;
        _loans = loans;
        _balances = balances;
    }

    public async Task<IReadOnlyList<MemberSummary>> Handle(
        ListMembersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var members = await _borrowers
            .AllMembersAsync(query.IncludeExited, cancellationToken)
            .ConfigureAwait(false);

        var summaries = new List<MemberSummary>(members.Count);

        foreach (var member in members)
        {
            var shareholding = await _balances
                .NaturalBalanceAsAtAsync(member.SharesAccountId, query.AsAt, cancellationToken)
                .ConfigureAwait(false);

            var running = await _loans
                .RunningForBorrowerAsync(member.Id, cancellationToken)
                .ConfigureAwait(false);

            summaries.Add(new MemberSummary(
                member.Id,
                member.MembershipNumber.Value,
                member.PayrollNumber.Value,
                member.Name.Full,
                shareholding,
                Shareholding.BorrowingLimit(shareholding),
                running.Count,
                member.IsActive,
                member.IsLandlord));
        }

        return summaries;
    }
}

/// <summary>One line of a member's statement.</summary>
/// <param name="Date">The entry date.</param>
/// <param name="Narration">What it was.</param>
/// <param name="SourceDocument">The paper it came from.</param>
/// <param name="Movement">What it added to or took off the account.</param>
/// <param name="RunningBalance">The balance after this entry.</param>
public sealed record StatementLine(
    DateOnly Date,
    string Narration,
    string SourceDocument,
    Money Movement,
    Money RunningBalance);

/// <summary>A loan as it appears on a member's statement.</summary>
public sealed record LoanOnStatement(
    string LoanNumber,
    LoanProduct Product,
    Money Principal,
    Money TotalRepayable,
    Money OutstandingBalance,
    DateOnly DisbursedOn,
    DateOnly FirstDueDate,
    Money MonthlyInstalment,
    LoanStatus Status);

/// <summary>
/// A member's statement as at a date: shares, loans, and every movement behind them.
/// </summary>
/// <param name="MemberName">Who it is for.</param>
/// <param name="MembershipNumber">Their number.</param>
/// <param name="AsAt">The date everything is stated as at.</param>
/// <param name="MembershipSince">Their first share contribution, or null if they have none yet.</param>
/// <param name="Shareholding">Shares as at the date.</param>
/// <param name="BorrowingLimit">Twice the shareholding.</param>
/// <param name="ShareMovements">Every entry on the share account, with a running balance.</param>
/// <param name="Loans">Their loans, running and settled.</param>
/// <param name="Category">
/// Which schedule they are deducted on. For the official reading this on screen, not for the
/// member - it is how you tell whether this person's contribution should have arrived on the
/// payroll cheque or the landlord one.
/// </param>
public sealed record MemberStatement(
    string MemberName,
    string MembershipNumber,
    DateOnly AsAt,
    DateOnly? MembershipSince,
    Money Shareholding,
    Money BorrowingLimit,
    IReadOnlyList<StatementLine> ShareMovements,
    IReadOnlyList<LoanOnStatement> Loans,
    BorrowerCategory Category = BorrowerCategory.Employee);

/// <summary>
/// Builds a member's statement as at any date.
/// </summary>
/// <remarks>
/// This is the query the whole append-only design exists for. Asked in December for a June
/// statement, it sums entries to 30 June and produces the figures a June statement would have
/// shown - because nothing that made them up was ever overwritten.
/// </remarks>
public sealed record GetMemberStatementQuery(BorrowerId MemberId, DateOnly AsAt)
    : IRequest<MemberStatement>;

internal sealed class GetMemberStatementHandler
    : IRequestHandler<GetMemberStatementQuery, MemberStatement>
{
    private readonly IBorrowerRepository _borrowers;
    private readonly ILoanRepository _loans;
    private readonly IJournalRepository _journal;
    private readonly IAccountRepository _accounts;
    private readonly IBalanceQueries _balances;

    public GetMemberStatementHandler(
        IBorrowerRepository borrowers,
        ILoanRepository loans,
        IJournalRepository journal,
        IAccountRepository accounts,
        IBalanceQueries balances)
    {
        _borrowers = borrowers;
        _loans = loans;
        _journal = journal;
        _accounts = accounts;
        _balances = balances;
    }

    public async Task<MemberStatement> Handle(
        GetMemberStatementQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var member = await _borrowers.FindMemberAsync(query.MemberId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No member with id {query.MemberId}.");

        var sharesAccount = await _accounts
            .FindByIdAsync(member.SharesAccountId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{member.Name} has no share account. Their shareholding is that account's balance.");

        var entries = await _journal
            .ForAccountAsOfAsync(member.SharesAccountId, query.AsAt, cancellationToken)
            .ConfigureAwait(false);

        var shareholding = Shareholding.AsAt(entries, sharesAccount, query.AsAt);

        // Build the running balance the way a person reads a statement: down the page, each
        // line showing where the balance stood after it.
        var movements = new List<StatementLine>();
        var running = Money.ZeroKes;

        foreach (var entry in entries.OrderBy(entry => entry.EntryDate).ThenBy(entry => entry.PostedAtUtc))
        {
            foreach (var line in entry.Lines.Where(line => line.AccountId == member.SharesAccountId))
            {
                // Shares are a liability, so a credit increases what the member holds.
                var movement = -line.SignedAmount;
                running += movement;

                movements.Add(new StatementLine(
                    entry.EntryDate,
                    entry.Narration,
                    entry.SourceDocument.ToString(),
                    movement,
                    running));
            }
        }

        var loans = await BuildLoansAsync(member.Id, query.AsAt, cancellationToken)
            .ConfigureAwait(false);

        return new MemberStatement(
            member.Name.Full,
            member.MembershipNumber.Value,
            query.AsAt,
            Shareholding.MembershipStartDate(entries, member.SharesAccountId),
            shareholding,
            Shareholding.BorrowingLimit(shareholding),
            movements,
            loans,
            BorrowerCategories.Of(member));
    }

    /// <summary>
    /// The loans that were running as at the date, not the loans running today.
    /// </summary>
    /// <remarks>
    /// These are different sets, and the difference is the whole point of an "as at" statement.
    /// A June statement must not show a loan disbursed in September, and it must still show one
    /// that was running in June and has been settled since. Filtering on the loan's own dates
    /// rather than on its current status is what makes both true.
    /// </remarks>
    private async Task<IReadOnlyList<LoanOnStatement>> BuildLoansAsync(
        BorrowerId memberId, DateOnly asAt, CancellationToken cancellationToken)
    {
        var all = await _loans.AllForBorrowerAsync(memberId, cancellationToken)
            .ConfigureAwait(false);

        var runningThen = all
            .Where(loan => loan.DisbursedOn <= asAt)
            .Where(loan => loan.SettledOn is null || loan.SettledOn > asAt)
            .ToList();

        var loans = new List<LoanOnStatement>(runningThen.Count);

        foreach (var loan in runningThen)
        {
            var outstanding = await _balances
                .NaturalBalanceAsAtAsync(loan.ReceivableAccountId, asAt, cancellationToken)
                .ConfigureAwait(false);

            var schedule = loan.Schedule;

            loans.Add(new LoanOnStatement(
                loan.LoanNumber,
                loan.Terms.Product,
                loan.Terms.Principal,
                loan.Terms.TotalRepayable,
                outstanding,
                loan.DisbursedOn,
                schedule.FirstDueDate,
                schedule.Instalments[0].Amount,
                loan.Status));
        }

        return loans;
    }
}

/// <summary>A zone, as the enrolment form lists them.</summary>
/// <param name="Id">The zone.</param>
/// <param name="Code">Its short code, which is what the office says out loud.</param>
/// <param name="Name">Its name.</param>
/// <param name="IsOffice">Whether this is the office rather than a field zone.</param>
public sealed record ZoneOption(ZoneId Id, string Code, string Name, bool IsOffice)
{
    /// <summary>Code and name together, because neither alone identifies one to a clerk.</summary>
    public string Label => $"{Code} — {Name}";
}

/// <summary>The zones a member can be enrolled into.</summary>
/// <remarks>
/// Active zones only. A member cannot be enrolled into a zone the society has closed, and
/// offering one in a list is how somebody ends up doing it.
/// </remarks>
public sealed record ListZonesQuery : IRequest<IReadOnlyList<ZoneOption>>;

internal sealed class ListZonesHandler : IRequestHandler<ListZonesQuery, IReadOnlyList<ZoneOption>>
{
    private readonly IZoneRepository _zones;

    public ListZonesHandler(IZoneRepository zones) => _zones = zones;

    public async Task<IReadOnlyList<ZoneOption>> Handle(
        ListZonesQuery query, CancellationToken cancellationToken)
    {
        var zones = await _zones.AllAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. zones
                .Where(zone => zone.IsActive)
                .Select(zone => new ZoneOption(zone.Id, zone.Code, zone.Name, zone.IsOffice)),
        ];
    }
}
