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
/// <param name="OriginalPrincipal">The amount that was originally disbursed.</param>
/// <param name="DisbursedOn">When the loan was originally paid out.</param>
/// <param name="TotalTermMonths">The full repayment period agreed at disbursement.</param>
/// <param name="RemainingTermMonths">How many monthly instalments are left to run.</param>
/// <param name="MonthlyDeduction">The monthly payroll deduction (principal + interest component).</param>
/// <param name="Product">Which loan product this is (Normal, Emergency, Client, or RentalIncome).</param>
public sealed record LoanBalanceRow(
    int RowNumber,
    string LoanNumber,
    string? PayrollNumber,
    Money OriginalPrincipal,
    DateOnly DisbursedOn,
    int TotalTermMonths,
    int RemainingTermMonths,
    Money MonthlyDeduction,
    LoanProduct Product = LoanProduct.Normal)
{
    /// <summary>
    /// Total amount still to collect: monthly deduction times months remaining.
    /// This is what goes into the loan receivable account (and the journal debit at go-live).
    /// </summary>
    public Money TotalReceivable =>
        Money.Kes(MonthlyDeduction.Amount * RemainingTermMonths);

    /// <summary>
    /// Full interest over the original term: (monthly × total months) − principal.
    /// This is stored on the loan entity so the progress bar can use TotalRepayable correctly.
    /// Returns zero if the inputs are not yet valid (guard for dry-run rows with errors).
    /// </summary>
    public Money TotalInterest
    {
        get
        {
            if (TotalTermMonths <= 0 || !OriginalPrincipal.IsPositive)
            {
                return Money.ZeroKes;
            }

            var totalInterest = MonthlyDeduction.Amount * TotalTermMonths - OriginalPrincipal.Amount;
            return totalInterest > 0m ? Money.Kes(totalInterest) : Money.ZeroKes;
        }
    }

    /// <summary>
    /// Remaining interest — the share of future deductions that is interest income.
    /// Used for the go-live journal entry (credit to Interest Income).
    /// </summary>
    public Money RemainingInterest
    {
        get
        {
            if (TotalTermMonths <= 0 || !OriginalPrincipal.IsPositive)
            {
                return Money.ZeroKes;
            }

            var totalInterest = MonthlyDeduction.Amount * TotalTermMonths - OriginalPrincipal.Amount;
            if (totalInterest <= 0m)
            {
                return Money.ZeroKes;
            }
            return Money.Kes(totalInterest * RemainingTermMonths / TotalTermMonths).Round();
        }
    }

    /// <summary>Outstanding principal at go-live: total receivable minus remaining interest.</summary>
    public Money OutstandingPrincipal => TotalReceivable - RemainingInterest;
}

/// <summary>
/// What a dry run found: what would be created, and everything wrong with it.
/// </summary>
/// <param name="GoLiveDate">The date each opening balance would be dated.</param>
/// <param name="Rows">Rows that parsed without a fatal problem.</param>
/// <param name="Problems">Everything wrong, fatal or not.</param>
/// <param name="TotalReceivable">Sum of every loan's total receivable (principal + remaining interest).</param>
/// <param name="TotalInterestIncome">Sum of every loan's remaining interest to be recognised at go-live.</param>
public sealed record LoanBalanceDryRun(
    DateOnly GoLiveDate,
    IReadOnlyList<LoanBalanceRow> Rows,
    IReadOnlyList<ImportProblem> Problems,
    Money TotalReceivable,
    Money TotalInterestIncome)
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
/// Validates loan numbers, payroll numbers, original principal, term, and monthly deduction.
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

            if (!row.OriginalPrincipal.IsPositive)
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.LoanNumber,
                    $"Original principal is {row.OriginalPrincipal}. Enter the full amount that was disbursed.",
                    IsFatal: true));
            }

            if (row.TotalTermMonths <= 0)
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.LoanNumber,
                    $"Total term is {row.TotalTermMonths} months. Enter the full repayment period agreed at disbursement.",
                    IsFatal: true));
            }

            if (row.RemainingTermMonths <= 0)
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.LoanNumber,
                    $"Remaining term is {row.RemainingTermMonths} months. Enter how many deductions are still outstanding.",
                    IsFatal: true));
            }

            if (row.TotalTermMonths > 0 && row.RemainingTermMonths > row.TotalTermMonths)
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.LoanNumber,
                    $"Remaining months ({row.RemainingTermMonths}) is more than the total term ({row.TotalTermMonths}). " +
                    "A loan cannot have more months left than its full term.",
                    IsFatal: true));
            }

            if (!row.MonthlyDeduction.IsPositive)
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.LoanNumber,
                    $"Monthly deduction is {row.MonthlyDeduction}. Enter the amount deducted from payroll each month.",
                    IsFatal: true));
            }

            // The deductions must exceed the principal — otherwise there is zero or negative interest,
            // which means the figures have been entered in the wrong columns.
            if (row.OriginalPrincipal.IsPositive && row.TotalTermMonths > 0 && row.MonthlyDeduction.IsPositive)
            {
                var totalRepayable = row.MonthlyDeduction.Amount * row.TotalTermMonths;
                if (totalRepayable <= row.OriginalPrincipal.Amount)
                {
                    problems.Add(new ImportProblem(
                        row.RowNumber, row.LoanNumber,
                        $"Monthly deduction {row.MonthlyDeduction} × {row.TotalTermMonths} months = " +
                        $"{Money.Kes(totalRepayable)} does not exceed the principal {row.OriginalPrincipal}. " +
                        "The deduction must cover principal plus interest — check the figures.",
                        IsFatal: true));
                }
                else
                {
                    // Warn if the implied flat rate looks unusual: outside 5%–25% of principal.
                    var impliedRate = (totalRepayable - row.OriginalPrincipal.Amount) / row.OriginalPrincipal.Amount;
                    if (impliedRate < 0.05m || impliedRate > 0.25m)
                    {
                        problems.Add(new ImportProblem(
                            row.RowNumber, row.LoanNumber,
                            $"Implied total interest is {impliedRate:P0} of the principal " +
                            $"({Money.Kes(totalRepayable - row.OriginalPrincipal.Amount)} on {row.OriginalPrincipal}). " +
                            "Double-check the original principal and monthly deduction.",
                            IsFatal: false));
                    }
                }
            }

            if (row.DisbursedOn > command.GoLiveDate)
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.LoanNumber,
                    $"Disbursement date {row.DisbursedOn:d MMM yyyy} is after the go-live date {command.GoLiveDate:d MMM yyyy}. " +
                    "A loan disbursed in the future cannot have an opening balance.",
                    IsFatal: true));
            }

            if (row.OriginalPrincipal.Amount != decimal.Truncate(row.OriginalPrincipal.Amount))
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.LoanNumber,
                    $"Principal {row.OriginalPrincipal} has a fractional shilling — check it against the register.",
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

        var validRows = problems.Any(p => p.IsFatal)
            ? command.Rows
                .Where(r => !problems.Any(p => p.IsFatal && p.RowNumber == r.RowNumber))
                .ToList()
            : (IReadOnlyList<LoanBalanceRow>)command.Rows;

        return new LoanBalanceDryRun(
            command.GoLiveDate,
            command.Rows,
            problems,
            command.Rows.Select(r => r.TotalReceivable).Sum(Currency.Kes),
            command.Rows.Select(r => r.RemainingInterest).Sum(Currency.Kes));
    }
}

/// <summary>What a committed loan balance import created.</summary>
/// <param name="LoansCreated">How many loans were imported.</param>
/// <param name="TotalReceivable">Their combined outstanding receivable (principal + remaining interest).</param>
/// <param name="TotalInterestIncome">Interest income recognised at go-live.</param>
/// <param name="OpeningEntryId">The journal entry that recorded them.</param>
public sealed record LoanImportResult(
    int LoansCreated,
    Money TotalReceivable,
    Money TotalInterestIncome,
    JournalEntryId OpeningEntryId);

/// <summary>
/// Commits the opening loan balances.
/// </summary>
/// <remarks>
/// <para>
/// The second half of the staged import. Each loan is created as a domain entity, its
/// receivable account is opened in the ledger, and one journal entry:
/// <list type="bullet">
///   <item>debits each loan receivable account for principal + remaining interest;</item>
///   <item>credits Loan Interest Income for the total remaining interest across all loans;</item>
///   <item>credits Opening Balance Equity for the total outstanding principal.</item>
/// </list>
/// This mirrors how a new loan is posted at disbursement (DR Receivable, CR Bank, CR Interest
/// Income) — the only difference is that Opening Balance Equity stands in for Bank, because
/// the cash was paid out before go-live.
/// </para>
/// <para>One entry for all loans, deliberately — the whole import either happened or it did not.</para>
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
        var interestIncome = await _accounts.LoanInterestIncomeAsync(cancellationToken).ConfigureAwait(false);

        var lines = new List<JournalLine>();

        foreach (var row in command.Rows)
        {
            var borrower = membersByPayroll[row.PayrollNumber!];
            var loanId = LoanId.New();

            var receivableAccount = await _accounts
                .OpenLoanReceivableAccountAsync(row.LoanNumber.Trim(), loanId.Value, cancellationToken)
                .ConfigureAwait(false);

            // Store the original principal and full-term interest on the loan entity.
            // The ledger is debited only for TotalReceivable (Monthly × Remaining), so
            // ledgerProportion = (TotalRepayable − outstanding) / TotalRepayable correctly
            // reflects how many months have already been paid before go-live.
            var remainingInterest = row.RemainingInterest;
            var totalReceivable = row.TotalReceivable;

            var terms = new LoanTerms(
                row.Product,
                row.OriginalPrincipal,
                row.TotalInterest,
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
                guarantees: [],
                originalTermMonths: row.TotalTermMonths);

            _loans.Add(loan);

            // Debit the full receivable (principal + remaining interest) per loan, matching
            // how a new disbursement posts: DR Receivable for TotalRepayable.
            lines.Add(JournalLine.Debit(
                receivableAccount.Id,
                totalReceivable,
                $"Opening loan balance — {row.LoanNumber.Trim()}"));
        }

        // One credit to Interest Income for all remaining interest combined.
        var totalInterestIncome = dryRun.TotalInterestIncome;
        if (totalInterestIncome.IsPositive)
        {
            lines.Add(JournalLine.Credit(
                interestIncome,
                totalInterestIncome,
                "Flat interest on opening loan balances at go-live"));
        }

        // Balance the rest against Opening Balance Equity (the "cash out" before go-live).
        var totalPrincipal = dryRun.TotalReceivable - totalInterestIncome;
        lines.Add(JournalLine.Credit(equity, totalPrincipal, "Opening loan balances at go-live"));

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

        return new LoanImportResult(
            command.Rows.Count,
            dryRun.TotalReceivable,
            totalInterestIncome,
            entry.Id);
    }
}
