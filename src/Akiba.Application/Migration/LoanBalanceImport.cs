using Akiba.Application.Abstractions;
using Akiba.Application.Posting;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Domain.Lending;
using Akiba.Domain.Membership;
using MediatR;

namespace Akiba.Application.Migration;

/// <summary>One loan as the go-live register lists it.</summary>
/// <param name="RowNumber">Which row of the file this came from, so a problem can be pointed at.</param>
/// <param name="LoanNumber">The society's reference for this loan (e.g. "L-0042").</param>
/// <param name="PayrollNumber">The borrower's staff number, used to look them up.</param>
/// <param name="OutstandingBalance">What the member still owes as at the go-live date.</param>
/// <param name="DisbursedOn">When the loan was originally paid out.</param>
/// <param name="RemainingTermMonths">How many monthly instalments are left to run.</param>
public sealed record LoanBalanceRow(
    int RowNumber,
    string LoanNumber,
    string? PayrollNumber,
    Money OutstandingBalance,
    DateOnly DisbursedOn,
    int RemainingTermMonths);

/// <summary>
/// What a dry run found: what would be created, and everything wrong with it.
/// </summary>
/// <param name="GoLiveDate">The date each opening balance would be dated.</param>
/// <param name="Rows">Rows that parsed without a fatal problem.</param>
/// <param name="Problems">Everything wrong, fatal or not.</param>
/// <param name="TotalOutstanding">Sum of every outstanding balance.</param>
public sealed record LoanBalanceDryRun(
    DateOnly GoLiveDate,
    IReadOnlyList<LoanBalanceRow> Rows,
    IReadOnlyList<ImportProblem> Problems,
    Money TotalOutstanding)
{
    public IReadOnlyList<ImportProblem> FatalProblems =>
        [.. Problems.Where(p => p.IsFatal)];

    public IReadOnlyList<ImportProblem> Warnings =>
        [.. Problems.Where(p => !p.IsFatal)];

    public bool CanCommit => FatalProblems.Count == 0 && Rows.Count > 0;
}

/// <summary>
/// Checks a list of opening loan balances without writing anything.
/// </summary>
/// <remarks>
/// Validates loan numbers, payroll numbers, amounts, and term months.
/// Does not touch the database. The dry run is shown to the clerk before the commit.
/// </remarks>
public sealed record DryRunLoanBalancesCommand(
    IReadOnlyList<LoanBalanceRow> Rows,
    DateOnly GoLiveDate) : IRequest<LoanBalanceDryRun>;

internal sealed class DryRunLoanBalancesHandler
    : IRequestHandler<DryRunLoanBalancesCommand, LoanBalanceDryRun>
{
    private readonly IBorrowerRepository _borrowers;
    private readonly ILoanRepository _loans;

    public DryRunLoanBalancesHandler(IBorrowerRepository borrowers, ILoanRepository loans)
    {
        _borrowers = borrowers;
        _loans = loans;
    }

    public async Task<LoanBalanceDryRun> Handle(
        DryRunLoanBalancesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var problems = new List<ImportProblem>();

        if (command.Rows.Count == 0)
        {
            problems.Add(new ImportProblem(null, null, "The loan list is empty.", true));
        }

        // Loan numbers must be unique in the file.
        var duplicateLoanNumbers = command.Rows
            .GroupBy(row => row.LoanNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in duplicateLoanNumbers)
        {
            foreach (var row in group)
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.LoanNumber,
                    $"Loan number \"{group.Key}\" appears {group.Count()} times. Each loan must be unique.",
                    IsFatal: true));
            }
        }

        foreach (var row in command.Rows)
        {
            if (string.IsNullOrWhiteSpace(row.LoanNumber))
            {
                problems.Add(new ImportProblem(row.RowNumber, null, "No loan number.", true));
            }

            if (!PayrollNumber.FromRegister(row.PayrollNumber).IsSpecified)
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.LoanNumber,
                    "Payroll number is blank or invalid. The borrower cannot be found without one.",
                    IsFatal: true));
            }

            if (!row.OutstandingBalance.IsPositive)
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.LoanNumber,
                    $"Outstanding balance is {row.OutstandingBalance}. A running loan must have a positive balance.",
                    IsFatal: true));
            }

            if (row.RemainingTermMonths <= 0)
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.LoanNumber,
                    $"Remaining term is {row.RemainingTermMonths} months. Enter how many instalments are left to run.",
                    IsFatal: true));
            }

            if (row.DisbursedOn > command.GoLiveDate)
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.LoanNumber,
                    $"Disbursement date {row.DisbursedOn:d MMM yyyy} is after the go-live date {command.GoLiveDate:d MMM yyyy}. " +
                    "A loan disbursed in the future cannot have an opening balance.",
                    IsFatal: true));
            }

            if (row.OutstandingBalance.Amount != decimal.Truncate(row.OutstandingBalance.Amount))
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.LoanNumber,
                    $"Balance {row.OutstandingBalance} has a fractional shilling — check it against the register.",
                    IsFatal: false));
            }
        }

        // Borrower look-up: each payroll number must match exactly one active member.
        var allMembers = await _borrowers.AllMembersAsync(false, cancellationToken).ConfigureAwait(false);
        var membersByPayroll = allMembers
            .Where(m => m.PayrollNumber.IsSpecified)
            .ToDictionary(m => m.PayrollNumber.Value!, StringComparer.OrdinalIgnoreCase);

        foreach (var row in command.Rows.Where(r => PayrollNumber.FromRegister(r.PayrollNumber).IsSpecified))
        {
            if (!membersByPayroll.ContainsKey(row.PayrollNumber!))
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.LoanNumber,
                    $"No active member found with payroll number \"{row.PayrollNumber}\". " +
                    "Import the member register first, or check the number.",
                    IsFatal: true));
            }
        }

        // Loan numbers must not already exist in Akiba.
        var existingLoans = await _loans.AllRunningAsync(cancellationToken).ConfigureAwait(false);
        var existingNumbers = existingLoans
            .Select(l => l.LoanNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var row in command.Rows)
        {
            if (!string.IsNullOrWhiteSpace(row.LoanNumber) && existingNumbers.Contains(row.LoanNumber.Trim()))
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.LoanNumber,
                    $"Loan number \"{row.LoanNumber}\" is already in Akiba. Importing it again would create a duplicate.",
                    IsFatal: true));
            }
        }

        return new LoanBalanceDryRun(
            command.GoLiveDate,
            command.Rows,
            problems,
            command.Rows.Select(r => r.OutstandingBalance).Sum(Currency.Kes));
    }
}

/// <summary>What a committed loan balance import created.</summary>
/// <param name="LoansCreated">How many loans were imported.</param>
/// <param name="TotalOutstanding">Their combined outstanding balance.</param>
/// <param name="OpeningEntryId">The journal entry that recorded them.</param>
public sealed record LoanImportResult(
    int LoansCreated,
    Money TotalOutstanding,
    JournalEntryId OpeningEntryId);

/// <summary>
/// Commits the opening loan balances.
/// </summary>
/// <remarks>
/// <para>
/// The second half of the staged import. Each loan is created as a domain entity, its
/// receivable account is opened in the ledger, and one journal entry debits each receivable
/// account against Opening Balance Equity. One entry for all loans, deliberately — the whole
/// import either happened or it did not.
/// </para>
/// <para>
/// Interest is set to zero because the outstanding balance already includes whatever interest
/// was charged. We are recording where things stand today, not reconstructing the original
/// pricing. Repayment tracking works correctly with a zero-interest loan — the schedule
/// shows the remaining balance split equally across the remaining term.
/// </para>
/// </remarks>
public sealed record CommitLoanBalancesCommand(
    IReadOnlyList<LoanBalanceRow> Rows,
    DateOnly GoLiveDate,
    string TreasurerSignOff) : IRequest<LoanImportResult>;

internal sealed class CommitLoanBalancesHandler
    : IRequestHandler<CommitLoanBalancesCommand, LoanImportResult>
{
    private readonly IMediator _mediator;
    private readonly IBorrowerRepository _borrowers;
    private readonly ILoanRepository _loans;
    private readonly IJournalRepository _journal;
    private readonly IAkibaAccounts _accounts;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public CommitLoanBalancesHandler(
        IMediator mediator,
        IBorrowerRepository borrowers,
        ILoanRepository loans,
        IJournalRepository journal,
        IAkibaAccounts accounts,
        ICurrentUser currentUser,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _mediator = mediator;
        _borrowers = borrowers;
        _loans = loans;
        _journal = journal;
        _accounts = accounts;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task<LoanImportResult> Handle(
        CommitLoanBalancesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.TreasurerSignOff))
        {
            throw new InvalidOperationException(
                "Opening loan balances are committed only after the treasurer has signed them off. " +
                "Record who signed, and when.");
        }

        var dryRun = await _mediator.Send(
            new DryRunLoanBalancesCommand(command.Rows, command.GoLiveDate),
            cancellationToken).ConfigureAwait(false);

        if (!dryRun.CanCommit)
        {
            throw new InvalidOperationException(
                "The loan list cannot be committed as it stands:" + Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    dryRun.FatalProblems.Select(p =>
                        $"  - row {p.RowNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}: {p.Problem}")));
        }

        var allMembers = await _borrowers.AllMembersAsync(false, cancellationToken).ConfigureAwait(false);
        var membersByPayroll = allMembers
            .Where(m => m.PayrollNumber.IsSpecified)
            .ToDictionary(m => m.PayrollNumber.Value!, StringComparer.OrdinalIgnoreCase);

        var equity = await _accounts.OpeningBalanceEquityAsync(cancellationToken).ConfigureAwait(false);
        var lines = new List<JournalLine>();

        foreach (var row in command.Rows)
        {
            var borrower = membersByPayroll[row.PayrollNumber!];
            var loanId = LoanId.New();

            var receivableAccount = await _accounts
                .OpenLoanReceivableAccountAsync(row.LoanNumber.Trim(), loanId.Value, cancellationToken)
                .ConfigureAwait(false);

            // Outstanding balance = principal; no interest — we are recording a position,
            // not repricing the loan. The schedule distributes what is owed across the months left.
            var terms = new LoanTerms(
                LoanProduct.Normal,
                row.OutstandingBalance,
                new Money(0, Currency.Kes),
                row.RemainingTermMonths,
                TermScaleVersion: 1);

            var loan = Loan.Rehydrate(
                loanId,
                row.LoanNumber.Trim(),
                LoanApplicationId.New(),
                borrower.Id,
                receivableAccount.Id,
                terms,
                row.DisbursedOn,
                cheque: null,
                LoanStatus.Running,
                restructures: null,
                hasBeenRestructured: false,
                settledOn: null,
                guarantees: []);

            _loans.Add(loan);

            lines.Add(JournalLine.Debit(
                receivableAccount.Id,
                row.OutstandingBalance,
                $"Opening loan balance — {row.LoanNumber.Trim()}"));
        }

        // Balance the entry against Opening Balance Equity.
        var total = lines.Select(l => l.SignedAmount).Sum(Currency.Kes);
        lines.Add(JournalLine.Credit(equity, total, "Opening loan balances at go-live"));

        var entry = JournalEntry.Post(
            command.GoLiveDate,
            $"Opening loan balances at go-live — signed off by {command.TreasurerSignOff.Trim()}",
            SourceDocument.Of(
                SourceDocumentKind.OpeningBalance,
                command.GoLiveDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            _currentUser.Actor,
            _clock.UtcNow,
            lines);

        await _journal.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new LoanImportResult(command.Rows.Count, dryRun.TotalOutstanding, entry.Id);
    }
}
