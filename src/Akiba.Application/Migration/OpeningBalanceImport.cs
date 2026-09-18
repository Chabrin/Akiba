using Akiba.Application.Abstractions;
using Akiba.Application.Posting;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Domain.Membership;
using MediatR;

namespace Akiba.Application.Migration;

/// <summary>One shareholder as the register lists them.</summary>
/// <param name="RowNumber">Which row of the file this came from, so a problem can be pointed at.</param>
/// <param name="StaffNumber">The payroll number, or the office's stand-in for "not on payroll".</param>
/// <param name="Name">The name exactly as the register writes it.</param>
/// <param name="OpeningShareholding">What they hold as at the go-live date.</param>
public sealed record RegisterRow(
    int RowNumber,
    string? StaffNumber,
    string Name,
    Money OpeningShareholding);

/// <summary>What is wrong with a row, or with the file as a whole.</summary>
/// <param name="RowNumber">Null where it concerns the whole file.</param>
/// <param name="Name">The shareholder, where the problem is about one.</param>
/// <param name="Problem">What is wrong, in the words an accounts clerk would use.</param>
/// <param name="IsFatal">
/// True where it stops the import. False where the clerk should look but the row can load.
/// </param>
public sealed record ImportProblem(int? RowNumber, string? Name, string Problem, bool IsFatal);

/// <summary>
/// What a dry run found: what would be created, and everything wrong with it.
/// </summary>
/// <param name="GoLiveDate">The date opening balances would be dated.</param>
/// <param name="Rows">Rows that parsed.</param>
/// <param name="Problems">Everything wrong, fatal or not.</param>
/// <param name="TotalShareholding">The sum of every opening shareholding.</param>
/// <param name="OpeningBankBalance">What the society says is in the bank at go-live.</param>
public sealed record ImportDryRun(
    DateOnly GoLiveDate,
    IReadOnlyList<RegisterRow> Rows,
    IReadOnlyList<ImportProblem> Problems,
    Money TotalShareholding,
    Money OpeningBankBalance)
{
    public IReadOnlyList<ImportProblem> FatalProblems =>
        [.. Problems.Where(problem => problem.IsFatal)];

    public IReadOnlyList<ImportProblem> Warnings =>
        [.. Problems.Where(problem => !problem.IsFatal)];

    /// <summary>
    /// What the opening entry would be out by.
    /// </summary>
    /// <remarks>
    /// Every opening balance posts against Opening Balance Equity, so the set always balances
    /// by construction. This is the different and more useful question: whether what the
    /// society says it <b>has</b> - the bank - matches what it says it <b>owes</b> - the
    /// members' shares. A gap is not an error in the file; it is loans outstanding, or a figure
    /// nobody has reconciled. Either way the treasurer should see it before signing.
    /// </remarks>
    public Money BankLessShareholding => OpeningBankBalance - TotalShareholding;

    public bool CanCommit => FatalProblems.Count == 0 && Rows.Count > 0;
}

/// <summary>
/// Checks a register without writing anything.
/// </summary>
/// <remarks>
/// The first half of a deliberately staged process: <b>load, validate, dry run, clerk reviews,
/// treasurer signs off, commit</b>. Nothing reaches the ledger until somebody has read this
/// and said yes.
/// </remarks>
public sealed record DryRunOpeningBalancesCommand(
    IReadOnlyList<RegisterRow> Rows,
    DateOnly GoLiveDate,
    Money OpeningBankBalance) : IRequest<ImportDryRun>;

internal sealed class DryRunOpeningBalancesHandler
    : IRequestHandler<DryRunOpeningBalancesCommand, ImportDryRun>
{
    private readonly IBorrowerRepository _borrowers;

    public DryRunOpeningBalancesHandler(IBorrowerRepository borrowers) => _borrowers = borrowers;

    public async Task<ImportDryRun> Handle(
        DryRunOpeningBalancesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var problems = new List<ImportProblem>();

        if (command.Rows.Count == 0)
        {
            problems.Add(new ImportProblem(null, null, "The register has no shareholders in it.", true));
        }

        // A payroll number that is genuinely present must be unique - HR matches the deduction
        // schedule on it. One that is absent may repeat as often as it likes.
        var duplicatePayroll = command.Rows
            .Select(row => new { row, payroll = PayrollNumber.FromRegister(row.StaffNumber) })
            .Where(entry => entry.payroll.IsSpecified)
            .GroupBy(entry => entry.payroll.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);

        foreach (var group in duplicatePayroll)
        {
            foreach (var entry in group)
            {
                problems.Add(new ImportProblem(
                    entry.row.RowNumber,
                    entry.row.Name,
                    $"Payroll number {group.Key} is used by {group.Count()} shareholders. HR " +
                    "matches the deduction schedule on it, so it cannot be shared.",
                    IsFatal: true));
            }
        }

        foreach (var row in command.Rows)
        {
            if (string.IsNullOrWhiteSpace(row.Name))
            {
                problems.Add(new ImportProblem(row.RowNumber, null, "No name.", true));
            }

            if (row.OpeningShareholding.IsNegative)
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.Name,
                    $"Opening shareholding is {row.OpeningShareholding}. A member cannot hold " +
                    "negative shares.",
                    IsFatal: true));
            }

            if (row.OpeningShareholding.IsZero)
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.Name,
                    "Opening shareholding is zero. They will load, but membership begins at the " +
                    "first contribution, so they have no membership start date until one arrives.",
                    IsFatal: false));
            }

            if (!PayrollNumber.FromRegister(row.StaffNumber).IsSpecified)
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.Name,
                    "Not on the CAL payroll. They will load, but they cannot be deducted at " +
                    "source, so their contributions must arrive another way.",
                    IsFatal: false));
            }

            // Money is decimal end to end, so a fractional shilling here came from the file.
            if (row.OpeningShareholding.Amount != decimal.Truncate(row.OpeningShareholding.Amount))
            {
                problems.Add(new ImportProblem(
                    row.RowNumber, row.Name,
                    $"Opening shareholding {row.OpeningShareholding} has a fractional part. " +
                    "Shareholdings in the register are whole shillings, so this is probably " +
                    "spreadsheet rounding rather than a real figure - check it before signing.",
                    IsFatal: false));
            }
        }

        // Somebody already in Akiba would be imported twice.
        var existing = await _borrowers.AllMembersAsync(true, cancellationToken).ConfigureAwait(false);

        foreach (var member in existing)
        {
            var clash = command.Rows.FirstOrDefault(row =>
                string.Equals(row.Name.Trim(), member.Name.Full, StringComparison.OrdinalIgnoreCase));

            if (clash is not null)
            {
                problems.Add(new ImportProblem(
                    clash.RowNumber, clash.Name,
                    $"{member.Name.Full} is already in Akiba. Importing would give them a second " +
                    "member record and a second share account.",
                    IsFatal: true));
            }
        }

        if (!command.OpeningBankBalance.IsPositive)
        {
            problems.Add(new ImportProblem(
                null, null,
                "No opening bank balance. The register gives what members hold but says nothing " +
                "about what is in the account, and the opening entry needs both.",
                IsFatal: true));
        }

        return new ImportDryRun(
            command.GoLiveDate,
            command.Rows,
            problems,
            command.Rows.Select(row => row.OpeningShareholding).Sum(Currency.Kes),
            command.OpeningBankBalance);
    }
}

/// <summary>What a committed import created.</summary>
public sealed record ImportResult(
    int MembersCreated,
    Money TotalShareholding,
    Money OpeningBankBalance,
    JournalEntryId OpeningEntryId);

/// <summary>
/// Commits the opening balances.
/// </summary>
/// <remarks>
/// <para>
/// The second half of the staged process, and it refuses to run unless the dry run was clean.
/// It creates a member and a share account for every row, then posts <b>one</b> journal entry
/// carrying every opening balance against Opening Balance Equity.
/// </para>
/// <para>
/// One entry rather than one per member, deliberately. The whole migration either happened or
/// it did not, and a single entry that must sum to zero is the thing that proves it was
/// complete rather than partial - <see cref="Domain.Ledger.JournalEntry"/>'s constructor will
/// not build it otherwise.
/// </para>
/// <para>
/// Only active balances are migrated, never ledger history. Akiba's history starts at go-live;
/// what came before it lives in the books it replaces.
/// </para>
/// </remarks>
public sealed record CommitOpeningBalancesCommand(
    IReadOnlyList<RegisterRow> Rows,
    DateOnly GoLiveDate,
    Money OpeningBankBalance,
    ZoneId DefaultZoneId,
    string TreasurerSignOff) : IRequest<ImportResult>;

internal sealed class CommitOpeningBalancesHandler
    : IRequestHandler<CommitOpeningBalancesCommand, ImportResult>
{
    private readonly IMediator _mediator;
    private readonly IBorrowerRepository _borrowers;
    private readonly IJournalRepository _journal;
    private readonly IAkibaAccounts _accounts;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public CommitOpeningBalancesHandler(
        IMediator mediator,
        IBorrowerRepository borrowers,
        IJournalRepository journal,
        IAkibaAccounts accounts,
        ICurrentUser currentUser,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _mediator = mediator;
        _borrowers = borrowers;
        _journal = journal;
        _accounts = accounts;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task<ImportResult> Handle(
        CommitOpeningBalancesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.TreasurerSignOff))
        {
            throw new InvalidOperationException(
                "Opening balances are committed only after the treasurer has signed them off. " +
                "Record who signed, and when.");
        }

        var dryRun = await _mediator.Send(
            new DryRunOpeningBalancesCommand(command.Rows, command.GoLiveDate, command.OpeningBankBalance),
            cancellationToken).ConfigureAwait(false);

        if (!dryRun.CanCommit)
        {
            throw new InvalidOperationException(
                "The register cannot be committed as it stands:" + Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    dryRun.FatalProblems.Select(problem =>
                        $"  - row {problem.RowNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}: {problem.Problem}")));
        }

        var lines = new List<JournalLine>();
        var created = 0;

        foreach (var row in command.Rows)
        {
            var memberId = Guid.NewGuid();
            var membershipNumber = row.RowNumber.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

            var sharesAccount = await _accounts
                .OpenMemberSharesAccountAsync(membershipNumber, row.Name.Trim(), memberId, cancellationToken)
                .ConfigureAwait(false);

            _borrowers.Add(Member.Join(
                MembershipNumber.Of(membershipNumber),
                PayrollNumber.FromRegister(row.StaffNumber),
                PersonName.FromRegister(row.Name),
                NationalId.Unknown,
                PhoneNumber.Unknown,
                null,
                command.DefaultZoneId,
                sharesAccount.Id));

            created++;

            if (row.OpeningShareholding.IsPositive)
            {
                lines.Add(JournalLine.Credit(
                    sharesAccount.Id, row.OpeningShareholding, $"Opening shareholding - {row.Name.Trim()}"));
            }
        }

        var bank = await _accounts.BankAsync(cancellationToken).ConfigureAwait(false);
        var equity = await _accounts.OpeningBalanceEquityAsync(cancellationToken).ConfigureAwait(false);

        lines.Insert(0, JournalLine.Debit(bank, command.OpeningBankBalance, "Opening bank balance"));

        // Opening Balance Equity is the balancing figure. Where the bank does not match what
        // members hold, the difference sits here - visible, named, and waiting to be explained
        // rather than silently absorbed.
        var difference = lines.Select(line => line.SignedAmount).Sum(Currency.Kes);

        if (!difference.IsZero)
        {
            lines.Add(difference.IsNegative
                ? JournalLine.Debit(equity, -difference, "Opening balance equity")
                : JournalLine.Credit(equity, difference, "Opening balance equity"));
        }

        var entry = JournalEntry.Post(
            command.GoLiveDate,
            $"Opening balances at go-live - signed off by {command.TreasurerSignOff.Trim()}",
            SourceDocument.Of(
                SourceDocumentKind.OpeningBalance,
                command.GoLiveDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            _currentUser.Actor,
            _clock.UtcNow,
            lines);

        await _journal.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ImportResult(
            created, dryRun.TotalShareholding, command.OpeningBankBalance, entry.Id);
    }
}
