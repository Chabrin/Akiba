using System.Globalization;
using Akiba.Application.Abstractions;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Domain.Lending;
using Akiba.Domain.Membership;
using MediatR;

namespace Akiba.Application.Lending;

// ──────────────────────────────────────────────────────────────
// Result types
// ──────────────────────────────────────────────────────────────

/// <summary>One member's total borrowing.</summary>
public sealed record BorrowerBorrowingRow(
    string BorrowerName,
    string PayrollNumber,
    int LoanCount,
    Money TotalBorrowed);

/// <summary>One member's offset activity.</summary>
public sealed record BorrowerOffsetRow(
    string BorrowerName,
    string PayrollNumber,
    int OffsetCount,
    Money TotalOffset);

/// <summary>A member who cannot currently borrow, and why.</summary>
public sealed record IneligibleBorrowerRow(
    string BorrowerName,
    string PayrollNumber,
    IReadOnlyList<string> Reasons);

/// <summary>A guarantor ranked by total amount guaranteed.</summary>
public sealed record GuarantorRankRow(
    string GuarantorName,
    string PayrollNumber,
    int PeopleGuaranteed,
    Money TotalGuaranteed);

// ──────────────────────────────────────────────────────────────
// Query 1: Total interest income for a period
// ──────────────────────────────────────────────────────────────

/// <summary>Total interest income credited in the period (account 4000 credits).</summary>
public sealed record GetInterestIncomeQuery(DateOnly From, DateOnly To) : IRequest<Money>;

internal sealed class GetInterestIncomeHandler : IRequestHandler<GetInterestIncomeQuery, Money>
{
    private readonly IJournalRepository _journal;
    private readonly IAccountRepository _accounts;

    public GetInterestIncomeHandler(IJournalRepository journal, IAccountRepository accounts)
    {
        _journal = journal;
        _accounts = accounts;
    }

    public async Task<Money> Handle(GetInterestIncomeQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var interestAccount = await _accounts
            .FindByCodeAsync(AccountCode.Of(ChartOfAccounts.LoanInterestIncome), cancellationToken)
            .ConfigureAwait(false);

        if (interestAccount is null)
        {
            return Money.ZeroKes;
        }

        var entries = await _journal
            .BetweenAsync(query.From, query.To, cancellationToken)
            .ConfigureAwait(false);

        return entries
            .SelectMany(e => e.Lines)
            .Where(l => l.AccountId == interestAccount.Id && l.Side == BalanceSide.Credit)
            .Sum(l => l.Magnitude, Currency.Kes);
    }
}

// ──────────────────────────────────────────────────────────────
// Query 2: Interest contributed by one member
// ──────────────────────────────────────────────────────────────

/// <summary>
/// Total interest credited by the society from a specific member's loans.
/// When <see cref="From"/> is null, covers the member's entire history.
/// </summary>
public sealed record GetMemberInterestQuery(
    BorrowerId MemberId,
    DateOnly? From,
    DateOnly To) : IRequest<Money>;

internal sealed class GetMemberInterestHandler : IRequestHandler<GetMemberInterestQuery, Money>
{
    private readonly IJournalRepository _journal;
    private readonly ILoanRepository _loans;
    private readonly IAccountRepository _accounts;

    public GetMemberInterestHandler(
        IJournalRepository journal, ILoanRepository loans, IAccountRepository accounts)
    {
        _journal = journal;
        _loans = loans;
        _accounts = accounts;
    }

    public async Task<Money> Handle(GetMemberInterestQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var memberLoans = await _loans
            .AllForBorrowerAsync(query.MemberId, cancellationToken)
            .ConfigureAwait(false);

        if (memberLoans.Count == 0)
        {
            return Money.ZeroKes;
        }

        var interestAccount = await _accounts
            .FindByCodeAsync(AccountCode.Of(ChartOfAccounts.LoanInterestIncome), cancellationToken)
            .ConfigureAwait(false);

        if (interestAccount is null)
        {
            return Money.ZeroKes;
        }

        var receivableIds = memberLoans.Select(l => l.ReceivableAccountId).ToHashSet();

        IReadOnlyList<JournalEntry> entries;
        if (query.From is { } from)
        {
            entries = await _journal.BetweenAsync(from, query.To, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            entries = await _journal.AsOfAsync(query.To, cancellationToken).ConfigureAwait(false);
        }

        // Disbursement entries debit the loan receivable account; interest is credited to 4000
        return entries
            .Where(e => e.Lines.Any(l => l.Side == BalanceSide.Debit && receivableIds.Contains(l.AccountId)))
            .SelectMany(e => e.Lines)
            .Where(l => l.AccountId == interestAccount.Id && l.Side == BalanceSide.Credit)
            .Sum(l => l.Magnitude, Currency.Kes);
    }
}

// ──────────────────────────────────────────────────────────────
// Query 3 & 5: Top borrowers (optionally filtered by product)
// ──────────────────────────────────────────────────────────────

/// <summary>
/// Top borrowers ranked by total principal borrowed.
/// Pass <see cref="Product"/> to restrict to one product type.
/// </summary>
public sealed record GetTopBorrowersQuery(
    int TopN = 10,
    LoanProduct? Product = null) : IRequest<IReadOnlyList<BorrowerBorrowingRow>>;

internal sealed class GetTopBorrowersHandler
    : IRequestHandler<GetTopBorrowersQuery, IReadOnlyList<BorrowerBorrowingRow>>
{
    private readonly IBorrowerRepository _borrowers;
    private readonly ILoanRepository _loans;

    public GetTopBorrowersHandler(IBorrowerRepository borrowers, ILoanRepository loans)
    {
        _borrowers = borrowers;
        _loans = loans;
    }

    public async Task<IReadOnlyList<BorrowerBorrowingRow>> Handle(
        GetTopBorrowersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var members = await _borrowers.AllMembersAsync(true, cancellationToken).ConfigureAwait(false);
        var rows = new List<BorrowerBorrowingRow>(members.Count);

        foreach (var member in members)
        {
            var allLoans = await _loans.AllForBorrowerAsync(member.Id, cancellationToken).ConfigureAwait(false);

            var qualifying = query.Product is { } product
                ? allLoans.Where(l => l.Terms.Product == product).ToList()
                : allLoans;

            if (qualifying.Count == 0)
            {
                continue;
            }

            var total = qualifying.Sum(l => l.Terms.Principal, Currency.Kes);
            rows.Add(new BorrowerBorrowingRow(
                member.Name.Full,
                member.PayrollNumber.Value,
                qualifying.Count,
                total));
        }

        return [.. rows.OrderByDescending(r => r.TotalBorrowed.Amount).Take(query.TopN)];
    }
}

// ──────────────────────────────────────────────────────────────
// Query 4: Top offsetters
// ──────────────────────────────────────────────────────────────

/// <summary>
/// Members who have used the new-loan offset feature most, ranked by number of offsets.
/// </summary>
public sealed record GetTopOffsetsQuery(DateOnly AsAt) : IRequest<IReadOnlyList<BorrowerOffsetRow>>;

internal sealed class GetTopOffsetsHandler
    : IRequestHandler<GetTopOffsetsQuery, IReadOnlyList<BorrowerOffsetRow>>
{
    private readonly IJournalRepository _journal;
    private readonly ILoanRepository _loans;
    private readonly IBorrowerRepository _borrowers;

    public GetTopOffsetsHandler(
        IJournalRepository journal, ILoanRepository loans, IBorrowerRepository borrowers)
    {
        _journal = journal;
        _loans = loans;
        _borrowers = borrowers;
    }

    public async Task<IReadOnlyList<BorrowerOffsetRow>> Handle(
        GetTopOffsetsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var entries = await _journal.AsOfAsync(query.AsAt, cancellationToken).ConfigureAwait(false);

        const string disbPrefix = "Disbursement - ";
        const string offsetSuffix = " (offset of ";
        const string offsetLinePrefix = "Offset —"; // "Offset —"

        var aggregates = new Dictionary<BorrowerId, (string name, string payroll, int count, Money total)>();

        foreach (var entry in entries)
        {
            if (!entry.Narration.StartsWith(disbPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parenPos = entry.Narration.IndexOf(offsetSuffix, StringComparison.OrdinalIgnoreCase);
            if (parenPos < 0)
            {
                continue;
            }

            var newLoanNumber = entry.Narration[disbPrefix.Length..parenPos].Trim();
            var loan = await _loans.FindByNumberAsync(newLoanNumber, cancellationToken).ConfigureAwait(false);
            if (loan is null)
            {
                continue;
            }

            var borrower = await _borrowers.FindByIdAsync(loan.BorrowerId, cancellationToken).ConfigureAwait(false);
            if (borrower is null)
            {
                continue;
            }

            var offsetLine = entry.Lines.FirstOrDefault(l =>
                l.Side == BalanceSide.Credit
                && l.Narration?.StartsWith(offsetLinePrefix, StringComparison.OrdinalIgnoreCase) == true);

            var amount = offsetLine?.Magnitude ?? Money.ZeroKes;
            var payroll = borrower is Member offsetMember ? offsetMember.PayrollNumber.Value : string.Empty;

            if (aggregates.TryGetValue(loan.BorrowerId, out var existing))
            {
                aggregates[loan.BorrowerId] = (existing.name, existing.payroll, existing.count + 1, existing.total + amount);
            }
            else
            {
                aggregates[loan.BorrowerId] = (borrower.Name.Full, payroll, 1, amount);
            }
        }

        return [.. aggregates.Values
            .OrderByDescending(r => r.count)
            .ThenByDescending(r => r.total.Amount)
            .Select(r => new BorrowerOffsetRow(r.name, r.payroll, r.count, r.total))];
    }
}

// ──────────────────────────────────────────────────────────────
// Query 6: Members not eligible to borrow
// ──────────────────────────────────────────────────────────────

/// <summary>
/// Active members who cannot currently take out a loan, with the reason for each.
/// </summary>
public sealed record GetIneligibleBorrowersQuery(DateOnly AsAt)
    : IRequest<IReadOnlyList<IneligibleBorrowerRow>>;

internal sealed class GetIneligibleBorrowersHandler
    : IRequestHandler<GetIneligibleBorrowersQuery, IReadOnlyList<IneligibleBorrowerRow>>
{
    private readonly IBorrowerRepository _borrowers;
    private readonly ILoanRepository _loans;
    private readonly IJournalRepository _journal;

    public GetIneligibleBorrowersHandler(
        IBorrowerRepository borrowers, ILoanRepository loans, IJournalRepository journal)
    {
        _borrowers = borrowers;
        _loans = loans;
        _journal = journal;
    }

    public async Task<IReadOnlyList<IneligibleBorrowerRow>> Handle(
        GetIneligibleBorrowersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var members = await _borrowers.AllMembersAsync(false, cancellationToken).ConfigureAwait(false);
        var rows = new List<IneligibleBorrowerRow>(members.Count / 2);

        foreach (var member in members)
        {
            var reasons = new List<string>(2);

            var running = await _loans
                .RunningForBorrowerAsync(member.Id, cancellationToken)
                .ConfigureAwait(false);

            if (running.Count >= LoanProductCatalogue.MaximumConcurrentLoansPerMember)
            {
                reasons.Add(
                    $"Already holds {running.Count} running loan{(running.Count == 1 ? string.Empty : "s")} " +
                    $"(maximum is {LoanProductCatalogue.MaximumConcurrentLoansPerMember})");
            }

            var shareEntries = await _journal
                .ForAccountAsOfAsync(member.SharesAccountId, query.AsAt, cancellationToken)
                .ConfigureAwait(false);

            var membershipStart = Shareholding.MembershipStartDate(shareEntries, member.SharesAccountId);

            if (membershipStart is null)
            {
                reasons.Add("Has not yet made a share contribution (membership has not begun)");
            }
            else if (query.AsAt < membershipStart.Value.AddMonths(6))
            {
                var eligibleFrom = membershipStart.Value.AddMonths(6);
                reasons.Add(
                    $"Joined on {membershipStart.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}, " +
                    $"eligible to borrow from {eligibleFrom.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}");
            }

            if (reasons.Count > 0)
            {
                rows.Add(new IneligibleBorrowerRow(member.Name.Full, member.PayrollNumber.Value, reasons));
            }
        }

        return [.. rows.OrderBy(r => r.BorrowerName, StringComparer.Ordinal)];
    }
}

// ──────────────────────────────────────────────────────────────
// Query 7: Top guarantors
// ──────────────────────────────────────────────────────────────

/// <summary>
/// Members who have guaranteed others most, ranked by total amount guaranteed.
/// </summary>
public sealed record GetTopGuarantorsQuery(int TopN = 10)
    : IRequest<IReadOnlyList<GuarantorRankRow>>;

internal sealed class GetTopGuarantorsHandler
    : IRequestHandler<GetTopGuarantorsQuery, IReadOnlyList<GuarantorRankRow>>
{
    private readonly ILoanApplicationRepository _applications;

    public GetTopGuarantorsHandler(ILoanApplicationRepository applications)
        => _applications = applications;

    public async Task<IReadOnlyList<GuarantorRankRow>> Handle(
        GetTopGuarantorsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var aggregates = new Dictionary<BorrowerId, (string name, string payroll, HashSet<BorrowerId> borrowers, Money total)>();

        foreach (var status in Enum.GetValues<LoanApplicationStatus>())
        {
            var applications = await _applications
                .ByStatusAsync(status, cancellationToken)
                .ConfigureAwait(false);

            foreach (var application in applications)
            {
                foreach (var guarantee in application.Guarantees.Where(g => !g.IsReleased))
                {
                    if (aggregates.TryGetValue(guarantee.GuarantorId, out var existing))
                    {
                        existing.borrowers.Add(application.BorrowerId);
                        aggregates[guarantee.GuarantorId] = (
                            existing.name,
                            existing.payroll,
                            existing.borrowers,
                            existing.total + guarantee.GuaranteedAmount);
                    }
                    else
                    {
                        aggregates[guarantee.GuarantorId] = (
                            guarantee.GuarantorName,
                            guarantee.GuarantorPayrollNumber.Value,
                            [application.BorrowerId],
                            guarantee.GuaranteedAmount);
                    }
                }
            }
        }

        return [.. aggregates.Values
            .OrderByDescending(r => r.total.Amount)
            .Take(query.TopN)
            .Select(r => new GuarantorRankRow(r.name, r.payroll, r.borrowers.Count, r.total))];
    }
}
