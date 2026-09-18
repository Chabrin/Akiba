using Akiba.Application.Abstractions;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Domain.Reconciliation;
using FluentValidation;
using MediatR;

namespace Akiba.Application.Reconciliation;

/// <summary>
/// Imports a bank statement so it can be matched against the ledger.
/// </summary>
/// <param name="FileName">The uploaded file's name. Decides whether it is read as CSV or Excel.</param>
/// <param name="Content">The file's bytes.</param>
/// <param name="From">The statement's opening date.</param>
/// <param name="To">The statement's closing date.</param>
/// <param name="OpeningBalance">What the bank says the account held at the start.</param>
/// <param name="ClosingBalance">What the bank says it holds at the end.</param>
/// <param name="BankAccountId">
/// Which of Akiba's bank accounts. Left unset it means the main Bank account.
/// </param>
/// <remarks>
/// Importing posts nothing. It reads a file and stores what it read, so that a clerk can work
/// through the lines. Anything the statement shows that Akiba never recorded is posted later,
/// by hand, as an ordinary journal entry.
/// </remarks>
public sealed record ImportBankStatementCommand(
    string FileName,
    byte[] Content,
    DateOnly From,
    DateOnly To,
    decimal OpeningBalance,
    decimal ClosingBalance,
    AccountId? BankAccountId = null) : IRequest<BankStatementImported>;

/// <summary>What the import found.</summary>
/// <param name="ReconciliationId">The stored reconciliation.</param>
/// <param name="LinesImported">How many lines were read.</param>
/// <param name="Problems">Rows that could not be read. None, ideally.</param>
/// <param name="IsSelfConsistent">
/// Whether opening plus the lines comes to closing. When it does not, the file is wrong or
/// incomplete and there is no point matching anything yet.
/// </param>
/// <param name="SuggestedMatches">How many lines Akiba could match unambiguously.</param>
/// <param name="ContinuityWarning">
/// Set when this statement does not start where the previous one ended.
/// </param>
public sealed record BankStatementImported(
    BankStatementId ReconciliationId,
    int LinesImported,
    IReadOnlyList<string> Problems,
    bool IsSelfConsistent,
    int SuggestedMatches,
    string? ContinuityWarning);

public sealed class ImportBankStatementValidator : AbstractValidator<ImportBankStatementCommand>
{
    public ImportBankStatementValidator()
    {
        RuleFor(command => command.FileName).NotEmpty();

        RuleFor(command => command.Content)
            .Must(content => content is { Length: > 0 })
            .WithMessage("The statement file is empty.");

        RuleFor(command => command.To)
            .GreaterThanOrEqualTo(command => command.From)
            .WithMessage("A statement's closing date cannot precede its opening date.");
    }
}

internal sealed class ImportBankStatementHandler
    : IRequestHandler<ImportBankStatementCommand, BankStatementImported>
{
    private readonly IBankStatementReader _reader;
    private readonly IBankReconciliationRepository _reconciliations;
    private readonly IAccountRepository _accounts;
    private readonly IAkibaAccounts _akibaAccounts;
    private readonly IJournalRepository _journal;
    private readonly IUnitOfWork _unitOfWork;

    public ImportBankStatementHandler(
        IBankStatementReader reader,
        IBankReconciliationRepository reconciliations,
        IAccountRepository accounts,
        IAkibaAccounts akibaAccounts,
        IJournalRepository journal,
        IUnitOfWork unitOfWork)
    {
        _reader = reader;
        _reconciliations = reconciliations;
        _accounts = accounts;
        _akibaAccounts = akibaAccounts;
        _journal = journal;
        _unitOfWork = unitOfWork;
    }

    public async Task<BankStatementImported> Handle(
        ImportBankStatementCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var bankAccountId = command.BankAccountId
            ?? await _akibaAccounts.BankAsync(cancellationToken).ConfigureAwait(false);

        var account = await _accounts.FindByIdAsync(bankAccountId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No account with id {bankAccountId}.");

        var file = _reader.Read(command.Content, command.FileName);

        var reconciliation = BankReconciliation.Import(
            bankAccountId,
            account.Name,
            command.From,
            command.To,
            Money.Kes(command.OpeningBalance),
            Money.Kes(command.ClosingBalance),
            file.Rows.Select(row => BankStatementLine.Import(
                row.LineNumber,
                row.ValueDate,
                row.Description,
                Money.Kes(row.Amount),
                row.Direction,
                row.BankReference)));

        var movements = await LedgerMovements
            .ForAccountAsync(_journal, bankAccountId, command.From, command.To, cancellationToken)
            .ConfigureAwait(false);

        var suggested = reconciliation.SuggestMatches(movements);

        _reconciliations.Add(reconciliation);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new BankStatementImported(
            reconciliation.Id,
            reconciliation.Lines.Count,
            file.Problems,
            reconciliation.IsSelfConsistent,
            suggested,
            await ContinuityWarningAsync(reconciliation, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Checks that this statement starts where the last one ended.
    /// </summary>
    /// <remarks>
    /// A warning rather than a refusal. The first statement ever imported has nothing before
    /// it, and a society that has been running on paper will import its statements out of
    /// order at least once. What matters is that nobody reconciles a quarter without noticing
    /// that the previous one is missing.
    /// </remarks>
    private async Task<string?> ContinuityWarningAsync(
        BankReconciliation reconciliation, CancellationToken cancellationToken)
    {
        var preceding = await _reconciliations
            .PrecedingAsync(reconciliation.BankAccountId, reconciliation.From, cancellationToken)
            .ConfigureAwait(false);

        if (preceding is null)
        {
            return null;
        }

        if (preceding.ClosingBalance != reconciliation.OpeningBalance)
        {
            return $"The previous statement closed at {preceding.ClosingBalance} on " +
                   $"{preceding.To:d MMMM yyyy}, but this one opens at " +
                   $"{reconciliation.OpeningBalance}. A statement is missing, or a figure was " +
                   "keyed wrongly.";
        }

        var gap = reconciliation.From.DayNumber - preceding.To.DayNumber;

        return gap > 1
            ? $"There are {gap - 1} day(s) between the previous statement's close on " +
              $"{preceding.To:d MMMM yyyy} and this one's start. Anything that happened in " +
              "between is on no statement."
            : null;
    }
}

/// <summary>Asks Akiba to propose matches again, after entries have been posted.</summary>
/// <remarks>
/// Run after posting the bank charges the statement revealed. Nothing is confirmed by it -
/// each suggestion still waits for a clerk.
/// </remarks>
public sealed record SuggestReconciliationMatchesCommand(BankStatementId ReconciliationId)
    : IRequest<int>;

internal sealed class SuggestReconciliationMatchesHandler
    : IRequestHandler<SuggestReconciliationMatchesCommand, int>
{
    private readonly IBankReconciliationRepository _reconciliations;
    private readonly IJournalRepository _journal;
    private readonly IUnitOfWork _unitOfWork;

    public SuggestReconciliationMatchesHandler(
        IBankReconciliationRepository reconciliations,
        IJournalRepository journal,
        IUnitOfWork unitOfWork)
    {
        _reconciliations = reconciliations;
        _journal = journal;
        _unitOfWork = unitOfWork;
    }

    public async Task<int> Handle(
        SuggestReconciliationMatchesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reconciliation = await Reconciliations
            .LoadAsync(_reconciliations, command.ReconciliationId, cancellationToken)
            .ConfigureAwait(false);

        var movements = await LedgerMovements
            .ForAccountAsync(
                _journal, reconciliation.BankAccountId, reconciliation.From, reconciliation.To,
                cancellationToken)
            .ConfigureAwait(false);

        var suggested = reconciliation.SuggestMatches(movements);

        _reconciliations.Update(reconciliation);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return suggested;
    }
}

/// <summary>
/// A clerk confirms that a statement line is a particular journal entry.
/// </summary>
/// <param name="ReconciliationId">Which statement.</param>
/// <param name="LineNumber">Which line, as printed on the paper statement.</param>
/// <param name="JournalEntryId">What it is.</param>
public sealed record ConfirmStatementMatchCommand(
    BankStatementId ReconciliationId,
    int LineNumber,
    JournalEntryId JournalEntryId) : IRequest;

internal sealed class ConfirmStatementMatchHandler
    : IRequestHandler<ConfirmStatementMatchCommand>
{
    private readonly IBankReconciliationRepository _reconciliations;
    private readonly IJournalRepository _journal;
    private readonly ICurrentUser _currentUser;
    private readonly IUnitOfWork _unitOfWork;

    public ConfirmStatementMatchHandler(
        IBankReconciliationRepository reconciliations,
        IJournalRepository journal,
        ICurrentUser currentUser,
        IUnitOfWork unitOfWork)
    {
        _reconciliations = reconciliations;
        _journal = journal;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(
        ConfirmStatementMatchCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reconciliation = await Reconciliations
            .LoadAsync(_reconciliations, command.ReconciliationId, cancellationToken)
            .ConfigureAwait(false);

        var entry = await _journal.FindByIdAsync(command.JournalEntryId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No journal entry with id {command.JournalEntryId}.");

        if (entry.Lines.All(line => line.AccountId != reconciliation.BankAccountId))
        {
            throw new InvalidOperationException(
                $"Entry \"{entry.Narration}\" does not touch {reconciliation.AccountLabel}, so it " +
                "cannot be what a line on its statement is.");
        }

        reconciliation.ConfirmMatch(
            command.LineNumber, entry.Id.Value, entry.Narration, _currentUser.Actor);

        _reconciliations.Update(reconciliation);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A clerk decides a statement line is not Akiba's money.</summary>
/// <param name="ReconciliationId">Which statement.</param>
/// <param name="LineNumber">Which line.</param>
/// <param name="Reason">Why. Recorded permanently.</param>
public sealed record MarkStatementLineNotOursCommand(
    BankStatementId ReconciliationId,
    int LineNumber,
    string Reason) : IRequest;

public sealed class MarkStatementLineNotOursValidator
    : AbstractValidator<MarkStatementLineNotOursCommand>
{
    public MarkStatementLineNotOursValidator() =>
        RuleFor(command => command.Reason)
            .NotEmpty()
            .WithMessage(
                "Say why the line is not Akiba's. It is the only record of a decision that " +
                "leaves money on the bank's side of the reconciliation for good.");
}

internal sealed class MarkStatementLineNotOursHandler
    : IRequestHandler<MarkStatementLineNotOursCommand>
{
    private readonly IBankReconciliationRepository _reconciliations;
    private readonly ICurrentUser _currentUser;
    private readonly IUnitOfWork _unitOfWork;

    public MarkStatementLineNotOursHandler(
        IBankReconciliationRepository reconciliations,
        ICurrentUser currentUser,
        IUnitOfWork unitOfWork)
    {
        _reconciliations = reconciliations;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(
        MarkStatementLineNotOursCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reconciliation = await Reconciliations
            .LoadAsync(_reconciliations, command.ReconciliationId, cancellationToken)
            .ConfigureAwait(false);

        reconciliation.MarkLineNotOurs(command.LineNumber, command.Reason, _currentUser.Actor);

        _reconciliations.Update(reconciliation);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Undoes a decision about a statement line.</summary>
public sealed record ClearStatementLineCommand(BankStatementId ReconciliationId, int LineNumber)
    : IRequest;

internal sealed class ClearStatementLineHandler : IRequestHandler<ClearStatementLineCommand>
{
    private readonly IBankReconciliationRepository _reconciliations;
    private readonly IUnitOfWork _unitOfWork;

    public ClearStatementLineHandler(
        IBankReconciliationRepository reconciliations, IUnitOfWork unitOfWork)
    {
        _reconciliations = reconciliations;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(ClearStatementLineCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reconciliation = await Reconciliations
            .LoadAsync(_reconciliations, command.ReconciliationId, cancellationToken)
            .ConfigureAwait(false);

        reconciliation.ClearLine(command.LineNumber);

        _reconciliations.Update(reconciliation);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The treasurer signs a reconciliation off.
/// </summary>
/// <remarks>
/// Refused unless it balances and every line has been accounted for. This is the gate the
/// period close depends on, so it is the one place where "close enough" is not a thing.
/// </remarks>
public sealed record SignOffReconciliationCommand(BankStatementId ReconciliationId)
    : IRequest<ReconciliationResult>;

internal sealed class SignOffReconciliationHandler
    : IRequestHandler<SignOffReconciliationCommand, ReconciliationResult>
{
    private readonly IBankReconciliationRepository _reconciliations;
    private readonly IJournalRepository _journal;
    private readonly IBalanceQueries _balances;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public SignOffReconciliationHandler(
        IBankReconciliationRepository reconciliations,
        IJournalRepository journal,
        IBalanceQueries balances,
        ICurrentUser currentUser,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _reconciliations = reconciliations;
        _journal = journal;
        _balances = balances;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task<ReconciliationResult> Handle(
        SignOffReconciliationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reconciliation = await Reconciliations
            .LoadAsync(_reconciliations, command.ReconciliationId, cancellationToken)
            .ConfigureAwait(false);

        var result = await Reconciliations
            .ResultAsync(reconciliation, _journal, _balances, cancellationToken)
            .ConfigureAwait(false);

        reconciliation.SignOff(result, _currentUser.Actor, _clock.UtcNow);

        _reconciliations.Update(reconciliation);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }
}

/// <summary>
/// Posts a bank charge the statement revealed.
/// </summary>
/// <param name="Amount">What the bank took.</param>
/// <param name="ChargedOn">The date the bank gave it, so it lands in the month it happened.</param>
/// <param name="Narration">What it was for, as the statement described it.</param>
/// <param name="StatementReference">The statement it came off, for the source document.</param>
/// <remarks>
/// <para>
/// Reconciling reveals these; it does not post them. A charge posted as a side effect of
/// opening a file would have no author and no date anybody chose, which is exactly what a
/// journal entry is supposed to have.
/// </para>
/// <para>
/// The charges matter beyond bookkeeping: the account earns no interest but incurs them, and
/// they are deducted when working out the interest the dividend is calculated from.
/// </para>
/// </remarks>
public sealed record RecordBankChargeCommand(
    decimal Amount,
    DateOnly ChargedOn,
    string Narration,
    string StatementReference) : IRequest<JournalEntryId>;

public sealed class RecordBankChargeValidator : AbstractValidator<RecordBankChargeCommand>
{
    public RecordBankChargeValidator()
    {
        RuleFor(command => command.Amount)
            .GreaterThan(0m)
            .WithMessage("A bank charge is a positive amount taken out of the account.");

        RuleFor(command => command.Narration).NotEmpty();
        RuleFor(command => command.StatementReference).NotEmpty();
    }
}

internal sealed class RecordBankChargeHandler
    : IRequestHandler<RecordBankChargeCommand, JournalEntryId>
{
    private readonly IJournalRepository _journal;
    private readonly IAkibaAccounts _accounts;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public RecordBankChargeHandler(
        IJournalRepository journal,
        IAkibaAccounts accounts,
        ICurrentUser currentUser,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _journal = journal;
        _accounts = accounts;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task<JournalEntryId> Handle(
        RecordBankChargeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var entry = Posting.AkibaPostings.BankCharge(
            await _accounts.BankChargesAsync(cancellationToken).ConfigureAwait(false),
            await _accounts.BankAsync(cancellationToken).ConfigureAwait(false),
            Money.Kes(command.Amount),
            command.ChargedOn,
            command.Narration,
            SourceDocument.Of(SourceDocumentKind.BankStatement, command.StatementReference),
            _currentUser.Actor,
            _clock.UtcNow);

        await _journal.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return entry.Id;
    }
}

/// <summary>Turns journal entries into the movements a reconciliation works with.</summary>
internal static class LedgerMovements
{
    public static async Task<IReadOnlyList<LedgerMovement>> ForAccountAsync(
        IJournalRepository journal,
        AccountId bankAccountId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var entries = await journal
            .ForAccountBetweenAsync(bankAccountId, from, to, cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. entries.Select(entry => new LedgerMovement(
                entry.Id,
                entry.EntryDate,
                entry.Narration,
                // An entry may touch the bank account more than once - a cheque banked net of
                // a charge, say. What reached the bank is the net of those lines, which is what
                // the statement will show.
                entry.Lines
                    .Where(line => line.AccountId == bankAccountId)
                    .Sum(line => line.SignedAmount, Currency.Kes))),
        ];
    }
}

/// <summary>Loading and reconciling, shared by the handlers.</summary>
internal static class Reconciliations
{
    public static async Task<BankReconciliation> LoadAsync(
        IBankReconciliationRepository reconciliations,
        BankStatementId id,
        CancellationToken cancellationToken) =>
        await reconciliations.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"No bank reconciliation with id {id}.");

    public static async Task<ReconciliationResult> ResultAsync(
        BankReconciliation reconciliation,
        IJournalRepository journal,
        IBalanceQueries balances,
        CancellationToken cancellationToken)
    {
        var ledgerBalance = await balances
            .NaturalBalanceAsAtAsync(reconciliation.BankAccountId, reconciliation.To, cancellationToken)
            .ConfigureAwait(false);

        var movements = await LedgerMovements
            .ForAccountAsync(
                journal, reconciliation.BankAccountId, reconciliation.From, reconciliation.To,
                cancellationToken)
            .ConfigureAwait(false);

        return reconciliation.Reconcile(ledgerBalance, movements);
    }
}
