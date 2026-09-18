using Akiba.Application.Abstractions;
using Akiba.Application.Reporting;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Domain.Reconciliation;
using MediatR;

namespace Akiba.Application.Reconciliation;

/// <summary>A reconciliation with everything the screen needs to show it.</summary>
/// <param name="Reconciliation">The statement and its lines.</param>
/// <param name="Result">Where the statement and the ledger stand.</param>
/// <param name="Candidates">
/// Akiba movements with no statement line against them, offered as the things an unmatched
/// line might be.
/// </param>
public sealed record BankReconciliationView(
    BankReconciliation Reconciliation,
    ReconciliationResult Result,
    IReadOnlyList<LedgerMovement> Candidates);

/// <summary>Reads one reconciliation and works out where it stands.</summary>
public sealed record GetBankReconciliationQuery(BankStatementId ReconciliationId)
    : IRequest<BankReconciliationView>;

internal sealed class GetBankReconciliationHandler
    : IRequestHandler<GetBankReconciliationQuery, BankReconciliationView>
{
    private readonly IBankReconciliationRepository _reconciliations;
    private readonly IJournalRepository _journal;
    private readonly IBalanceQueries _balances;

    public GetBankReconciliationHandler(
        IBankReconciliationRepository reconciliations,
        IJournalRepository journal,
        IBalanceQueries balances)
    {
        _reconciliations = reconciliations;
        _journal = journal;
        _balances = balances;
    }

    public async Task<BankReconciliationView> Handle(
        GetBankReconciliationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var reconciliation = await Reconciliations
            .LoadAsync(_reconciliations, query.ReconciliationId, cancellationToken)
            .ConfigureAwait(false);

        var result = await Reconciliations
            .ResultAsync(reconciliation, _journal, _balances, cancellationToken)
            .ConfigureAwait(false);

        return new BankReconciliationView(
            reconciliation, result, result.UnmatchedMovements);
    }
}

/// <summary>One reconciliation on the work list.</summary>
/// <param name="ReconciliationId">Which one.</param>
/// <param name="AccountLabel">Which bank account.</param>
/// <param name="From">Statement period.</param>
/// <param name="To">Statement period.</param>
/// <param name="LineCount">How many lines it has.</param>
/// <param name="UnresolvedLineCount">How many still need a decision.</param>
/// <param name="Difference">What is unexplained.</param>
/// <param name="IsSignedOff">Whether the treasurer has signed it.</param>
public sealed record ReconciliationSummary(
    BankStatementId ReconciliationId,
    string AccountLabel,
    DateOnly From,
    DateOnly To,
    int LineCount,
    int UnresolvedLineCount,
    Money Difference,
    bool IsSignedOff)
{
    public bool NeedsWork => !IsSignedOff;
}

/// <summary>The reconciliation work list, newest statement first.</summary>
public sealed record ListBankReconciliationsQuery : IRequest<IReadOnlyList<ReconciliationSummary>>;

internal sealed class ListBankReconciliationsHandler
    : IRequestHandler<ListBankReconciliationsQuery, IReadOnlyList<ReconciliationSummary>>
{
    private readonly IBankReconciliationRepository _reconciliations;
    private readonly IJournalRepository _journal;
    private readonly IBalanceQueries _balances;

    public ListBankReconciliationsHandler(
        IBankReconciliationRepository reconciliations,
        IJournalRepository journal,
        IBalanceQueries balances)
    {
        _reconciliations = reconciliations;
        _journal = journal;
        _balances = balances;
    }

    public async Task<IReadOnlyList<ReconciliationSummary>> Handle(
        ListBankReconciliationsQuery query, CancellationToken cancellationToken)
    {
        var all = await _reconciliations.AllAsync(cancellationToken).ConfigureAwait(false);

        var summaries = new List<ReconciliationSummary>(all.Count);

        foreach (var reconciliation in all)
        {
            var result = await Reconciliations
                .ResultAsync(reconciliation, _journal, _balances, cancellationToken)
                .ConfigureAwait(false);

            summaries.Add(new ReconciliationSummary(
                reconciliation.Id,
                reconciliation.AccountLabel,
                reconciliation.From,
                reconciliation.To,
                reconciliation.Lines.Count,
                result.UnresolvedLines.Count,
                result.Difference,
                reconciliation.IsSignedOff));
        }

        return summaries;
    }
}

/// <summary>What HR says came off one payslip, as keyed or uploaded.</summary>
/// <param name="PayrollNumber">HR's staff number.</param>
/// <param name="NameAsHrWroteIt">The name as it appears on HR's return.</param>
/// <param name="Deducted">The figure.</param>
public sealed record PayrollReturnLine(
    string PayrollNumber,
    string NameAsHrWroteIt,
    decimal Deducted);

/// <summary>
/// Sets a month's deduction schedule against what HR actually deducted.
/// </summary>
/// <param name="Kind">Employees or landlords. They go to different places and come back separately.</param>
/// <param name="Year">The payroll month.</param>
/// <param name="Month">The payroll month.</param>
/// <param name="Returned">HR's return.</param>
/// <param name="ChequeReceived">The cheque that came back with it.</param>
/// <remarks>
/// The schedule side is rebuilt from the ledger rather than stored, so this compares HR's
/// return against what Akiba would ask for today. That is the right comparison for the
/// current month and the wrong one for a month whose loans have since been restructured -
/// so run it when the cheque arrives, not a year later.
/// </remarks>
public sealed record GetPayrollReconciliationQuery(
    DeductionScheduleKind Kind,
    int Year,
    int Month,
    IReadOnlyList<PayrollReturnLine> Returned,
    decimal ChequeReceived) : IRequest<PayrollReconciliation>;

internal sealed class GetPayrollReconciliationHandler
    : IRequestHandler<GetPayrollReconciliationQuery, PayrollReconciliation>
{
    private readonly ISender _sender;

    public GetPayrollReconciliationHandler(ISender sender) => _sender = sender;

    public async Task<PayrollReconciliation> Handle(
        GetPayrollReconciliationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var schedule = await _sender
            .Send(new GetDeductionScheduleQuery(query.Kind, query.Year, query.Month), cancellationToken)
            .ConfigureAwait(false);

        var expectations = schedule.Lines.Select(line => new PayrollExpectation(
            line.PayrollNumber,
            line.FullName,
            line.ShareContribution,
            line.TotalLoanInstalments));

        var actuals = query.Returned.Select(line => new PayrollActual(
            line.PayrollNumber, line.NameAsHrWroteIt, Money.Kes(line.Deducted)));

        return PayrollReconciliation.Compare(
            query.Year, query.Month, expectations, actuals, Money.Kes(query.ChequeReceived));
    }
}
