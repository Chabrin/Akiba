using Akiba.Domain.Dividends;
using Akiba.Domain.Financial;
using MediatR;

namespace Akiba.Application.Dividends;

/// <summary>One run on the list.</summary>
/// <param name="RunId">Which run.</param>
/// <param name="Year">The year it distributes.</param>
/// <param name="Distributable">Interest earned net of bank charges.</param>
/// <param name="MemberCount">How many members share it.</param>
/// <param name="BasisName">Which basis was used.</param>
/// <param name="Status">How far it has got.</param>
/// <param name="Verdict">What is outstanding, in words.</param>
public sealed record DividendRunSummary(
    DividendRunId RunId,
    int Year,
    Money Distributable,
    int MemberCount,
    string BasisName,
    DividendRunStatus Status,
    string Verdict);

/// <summary>Every dividend run, most recent year first.</summary>
public sealed record ListDividendRunsQuery : IRequest<IReadOnlyList<DividendRunSummary>>;

internal sealed class ListDividendRunsHandler
    : IRequestHandler<ListDividendRunsQuery, IReadOnlyList<DividendRunSummary>>
{
    private readonly IDividendRunRepository _runs;

    public ListDividendRunsHandler(IDividendRunRepository runs) => _runs = runs;

    public async Task<IReadOnlyList<DividendRunSummary>> Handle(
        ListDividendRunsQuery query, CancellationToken cancellationToken)
    {
        var all = await _runs.AllAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. all.Select(run => new DividendRunSummary(
                run.Id,
                run.Year,
                run.Distributable,
                run.Lines.Count,
                run.BasisName,
                run.Status,
                run.Verdict)),
        ];
    }
}

/// <summary>Reads one run, with every member's line.</summary>
public sealed record GetDividendRunQuery(DividendRunId RunId) : IRequest<DividendRun>;

internal sealed class GetDividendRunHandler : IRequestHandler<GetDividendRunQuery, DividendRun>
{
    private readonly IDividendRunRepository _runs;

    public GetDividendRunHandler(IDividendRunRepository runs) => _runs = runs;

    public async Task<DividendRun> Handle(
        GetDividendRunQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await _runs.FindByIdAsync(query.RunId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No dividend run with id {query.RunId}.");
    }
}

/// <summary>
/// Compares what the two unadopted bases would pay, member by member.
/// </summary>
/// <remarks>
/// Built for one purpose: to put in front of the committee so they can settle open question 11
/// by looking at what each answer actually costs a real member, rather than in the abstract.
/// It computes both and posts neither.
/// </remarks>
public sealed record CompareDividendBasesQuery(DividendRunId RunId)
    : IRequest<DividendBasisComparison>;

/// <summary>One member under both bases.</summary>
/// <param name="MembershipNumber">Their number.</param>
/// <param name="FullName">Their name.</param>
/// <param name="OnTheRunsBasis">What the run as computed gives them.</param>
/// <param name="OnTheOtherBasis">What the other basis would give them.</param>
public sealed record DividendBasisComparisonLine(
    string MembershipNumber,
    string FullName,
    Money OnTheRunsBasis,
    Money OnTheOtherBasis)
{
    public Money Difference => OnTheOtherBasis - OnTheRunsBasis;
}

/// <summary>What the choice of basis is worth, member by member.</summary>
/// <param name="Year">The year.</param>
/// <param name="RunsBasisName">The basis the run used.</param>
/// <param name="OtherBasisName">The one it did not.</param>
/// <param name="Lines">Every member, biggest difference first.</param>
public sealed record DividendBasisComparison(
    int Year,
    string RunsBasisName,
    string OtherBasisName,
    IReadOnlyList<DividendBasisComparisonLine> Lines)
{
    /// <summary>The largest single member's difference, which is the figure worth quoting.</summary>
    public Money LargestDifference => Lines.Count == 0
        ? Money.ZeroKes
        : Lines.Max(line => line.Difference.Abs());
}

internal sealed class CompareDividendBasesHandler
    : IRequestHandler<CompareDividendBasesQuery, DividendBasisComparison>
{
    private readonly IDividendRunRepository _runs;
    private readonly Abstractions.IBorrowerRepository _borrowers;
    private readonly Abstractions.IBalanceQueries _balances;

    public CompareDividendBasesHandler(
        IDividendRunRepository runs,
        Abstractions.IBorrowerRepository borrowers,
        Abstractions.IBalanceQueries balances)
    {
        _runs = runs;
        _borrowers = borrowers;
        _balances = balances;
    }

    public async Task<DividendBasisComparison> Handle(
        CompareDividendBasesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var run = await _runs.FindByIdAsync(query.RunId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No dividend run with id {query.RunId}.");

        var other = run.BasisName == new ClosingShareholdingBasis().Name
            ? (IDividendBasis)new TimeWeightedShareholdingBasis()
            : new ClosingShareholdingBasis();

        var shareholdings = await DividendLedgerReader
            .ShareholdingsOverYearAsync(_borrowers, _balances, run.Year, cancellationToken)
            .ConfigureAwait(false);

        var weights = other.Weigh(shareholdings);

        // The same amount, divided the other way. Allocate again rather than scaling the run's
        // figures: the leftover cents land differently under a different set of weights, and
        // a comparison that ignored that would be out by the cents it exists to explain.
        var amounts = weights.Sum() <= 0m
            ? [.. shareholdings.Select(_ => Money.Zero(run.Distributable.Currency))]
            : run.Distributable.Allocate(weights);

        var onTheOther = shareholdings
            .Select((member, index) => (member.MemberId, Amount: amounts[index]))
            .ToDictionary(entry => entry.MemberId, entry => entry.Amount);

        var lines = run.Lines
            .Select(line => new DividendBasisComparisonLine(
                line.MembershipNumber,
                line.FullName,
                line.Amount,
                onTheOther.TryGetValue(line.MemberId, out var amount)
                    ? amount
                    : Money.Zero(run.Distributable.Currency)))
            .OrderByDescending(line => line.Difference.Abs())
            .ThenBy(line => line.MembershipNumber, StringComparer.Ordinal)
            .ToList();

        return new DividendBasisComparison(run.Year, run.BasisName, other.Name, lines);
    }
}
