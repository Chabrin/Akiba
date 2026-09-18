using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;

namespace Akiba.Domain.Reconciliation;

/// <summary>How far along a reconciliation is.</summary>
public enum ReconciliationStatus
{
    /// <summary>Imported. Lines are still being matched.</summary>
    Draft = 1,

    /// <summary>
    /// The treasurer has signed it off. It balanced, and every line had been accounted for.
    /// </summary>
    SignedOff = 2,
}

/// <summary>
/// A movement on Akiba's bank account, as Akiba recorded it.
/// </summary>
/// <param name="JournalEntryId">The entry it came from.</param>
/// <param name="EntryDate">The date Akiba gave it.</param>
/// <param name="Narration">What Akiba said it was.</param>
/// <param name="SignedAmount">
/// Signed the way the ledger signs it: money in is a debit and therefore positive.
/// </param>
/// <remarks>
/// Never stored as part of a reconciliation. It is read from the journal every time the
/// reconciliation is worked out, because the journal is where the figure lives - a copy kept
/// alongside would be a second version of the truth, and the two would eventually disagree.
/// </remarks>
public sealed record LedgerMovement(
    JournalEntryId JournalEntryId,
    DateOnly EntryDate,
    string Narration,
    Money SignedAmount);

/// <summary>
/// What a reconciliation came to.
/// </summary>
/// <param name="LedgerBalance">Akiba's bank account, as at the statement's closing date.</param>
/// <param name="StatementClosingBalance">What the bank says.</param>
/// <param name="UnreconciledStatementMovement">
/// The signed sum of statement lines Akiba has not matched - bank charges, interest, a direct
/// credit nobody told the office about, and anything the clerk has decided is not Akiba's.
/// </param>
/// <param name="UnpresentedLedgerMovement">
/// The signed sum of Akiba movements the bank has not shown - a cheque that has not been
/// presented, a deposit still in transit.
/// </param>
/// <param name="Difference">
/// What is left unexplained. It should be zero. Anything else means a figure on one side has
/// no counterpart on the other and nobody has said why.
/// </param>
/// <param name="UnresolvedLines">Statement lines still waiting for a clerk's decision.</param>
/// <param name="UnmatchedMovements">Akiba movements with no statement line against them.</param>
public sealed record ReconciliationResult(
    Money LedgerBalance,
    Money StatementClosingBalance,
    Money UnreconciledStatementMovement,
    Money UnpresentedLedgerMovement,
    Money Difference,
    IReadOnlyList<BankStatementLine> UnresolvedLines,
    IReadOnlyList<LedgerMovement> UnmatchedMovements)
{
    public bool Balances => Difference.IsZero;

    /// <summary>Whether every statement line has had a decision made about it.</summary>
    public bool EveryLineResolved => UnresolvedLines.Count == 0;

    /// <summary>
    /// Whether the month this statement covers may be closed.
    /// </summary>
    /// <remarks>
    /// Both conditions, not either. A reconciliation that balances while three lines sit
    /// unexamined balances by accident.
    /// </remarks>
    public bool MayClosePeriod => Balances && EveryLineResolved;

    /// <summary>What is outstanding, in words an official can act on.</summary>
    public string Verdict
    {
        get
        {
            if (MayClosePeriod)
            {
                return "Reconciled. The bank and the ledger agree and every line has been " +
                       "accounted for.";
            }

            var problems = new List<string>(2);

            if (!Balances)
            {
                problems.Add(
                    $"{Difference.Abs()} is unexplained - the ledger and the statement do not " +
                    "agree even after allowing for items in transit");
            }

            if (!EveryLineResolved)
            {
                problems.Add(
                    $"{UnresolvedLines.Count} statement line(s) have not been matched or " +
                    "written off");
            }

            return string.Join("; ", problems) + ". The month cannot be closed until this is settled.";
        }
    }
}

/// <summary>
/// A bank statement imported for matching against the ledger.
/// </summary>
/// <remarks>
/// <para>
/// Statements arrive quarterly, so this is the control that catches everything the office
/// never saw: bank charges, a member's deposit paid straight in without telling anybody, a
/// cheque that was never presented. Until a month reconciles it cannot be closed.
/// </para>
/// <para>
/// <b>Matching suggests; it never decides.</b> Akiba proposes a match where an amount and a
/// date line up, and a clerk confirms it. Two members paying 5,000 on the same day is not
/// unusual, and a system that silently picked one of them would produce a statement a member
/// cannot query.
/// </para>
/// <para>
/// Reconciling posts nothing by itself. Where the statement shows something Akiba has not
/// recorded - bank charges, most often - the clerk posts it as an ordinary journal entry and
/// then matches it. That way the entry has a date, a narration and an author like every other
/// entry, instead of appearing as a side effect of opening a file.
/// </para>
/// </remarks>
public sealed class BankReconciliation : AggregateRoot<BankStatementId>
{
    private readonly List<BankStatementLine> _lines;

    private BankReconciliation(
        BankStatementId id,
        AccountId bankAccountId,
        string accountLabel,
        DateOnly from,
        DateOnly to,
        Money openingBalance,
        Money closingBalance,
        IEnumerable<BankStatementLine> lines)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountLabel);

        if (to < from)
        {
            throw new ArgumentException(
                $"A statement's closing date {to:yyyy-MM-dd} cannot precede its opening date " +
                $"{from:yyyy-MM-dd}.",
                nameof(to));
        }

        BankAccountId = bankAccountId;
        AccountLabel = accountLabel.Trim();
        From = from;
        To = to;
        OpeningBalance = openingBalance.Round();
        ClosingBalance = closingBalance.Round();
        Status = ReconciliationStatus.Draft;
        _lines = [.. lines];
    }

    /// <summary>Which of Akiba's bank accounts this statement is for.</summary>
    /// <remarks>
    /// The main account and the business account reconcile separately. Employee deductions
    /// arrive on one and landlord rent offsets on the other, so a single combined
    /// reconciliation would hide a shortfall on either.
    /// </remarks>
    public AccountId BankAccountId { get; }

    /// <summary>The account's name as an official would say it, for the statement's heading.</summary>
    public string AccountLabel { get; }

    public DateOnly From { get; }

    public DateOnly To { get; }

    /// <summary>What the bank said the account held at the start.</summary>
    public Money OpeningBalance { get; }

    /// <summary>What the bank says the account holds at the end. The figure being proved.</summary>
    public Money ClosingBalance { get; }

    public ReconciliationStatus Status { get; private set; }

    public Actor? SignedOffBy { get; private set; }

    public DateTimeOffset? SignedOffAtUtc { get; private set; }

    public IReadOnlyList<BankStatementLine> Lines => _lines;

    public bool IsSignedOff => Status == ReconciliationStatus.SignedOff;

    /// <summary>
    /// Whether the statement's own figures are internally consistent.
    /// </summary>
    /// <remarks>
    /// Opening plus every line should come to closing. When it does not, the import lost a
    /// line or read a column wrongly, and there is no point matching anything until that is
    /// fixed.
    /// </remarks>
    public bool IsSelfConsistent =>
        (OpeningBalance + _lines.Sum(line => line.SignedAmount, ClosingBalance.Currency))
            .Round() == ClosingBalance;

    /// <summary>
    /// Imports a statement.
    /// </summary>
    /// <exception cref="ArgumentException">Two lines share a line number.</exception>
    public static BankReconciliation Import(
        AccountId bankAccountId,
        string accountLabel,
        DateOnly from,
        DateOnly to,
        Money openingBalance,
        Money closingBalance,
        IEnumerable<BankStatementLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var imported = lines.ToList();

        var duplicate = imported
            .GroupBy(line => line.LineNumber)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Two lines were imported as line {duplicate.Key}. Line numbers identify a line " +
                "to whoever has the paper statement in front of them, so they cannot repeat.",
                nameof(lines));
        }

        return new BankReconciliation(
            BankStatementId.New(), bankAccountId, accountLabel, from, to,
            openingBalance, closingBalance, imported);
    }

    /// <summary>Rebuilds a reconciliation from storage. For the persistence layer only.</summary>
    public static BankReconciliation Rehydrate(
        BankStatementId id,
        AccountId bankAccountId,
        string accountLabel,
        DateOnly from,
        DateOnly to,
        Money openingBalance,
        Money closingBalance,
        ReconciliationStatus status,
        Actor? signedOffBy,
        DateTimeOffset? signedOffAtUtc,
        IEnumerable<BankStatementLine> lines) =>
        new(id, bankAccountId, accountLabel, from, to, openingBalance, closingBalance, lines)
        {
            Status = status,
            SignedOffBy = signedOffBy,
            SignedOffAtUtc = signedOffAtUtc,
        };

    public BankStatementLine Line(int lineNumber) =>
        _lines.FirstOrDefault(line => line.LineNumber == lineNumber)
        ?? throw new InvalidOperationException(
            $"This statement has no line {lineNumber}.");

    /// <summary>
    /// Proposes matches between statement lines and Akiba's own movements.
    /// </summary>
    /// <param name="movements">Akiba's movements on this account over the statement period.</param>
    /// <param name="dateTolerance">
    /// How many days apart the two dates may be. Money paid in on a Friday can reach the
    /// statement on the Monday, so a few days of slack is normal; the default is three.
    /// </param>
    /// <returns>How many lines got a suggestion.</returns>
    /// <remarks>
    /// <para>
    /// A suggestion is offered only where it is unambiguous: exactly one unmatched movement of
    /// the same signed amount within the tolerance. Two candidates produce none, because
    /// picking one would be a guess wearing a decision's clothes.
    /// </para>
    /// <para>
    /// Nothing here changes a balance. Every suggestion still has to be confirmed by somebody.
    /// </para>
    /// </remarks>
    public int SuggestMatches(IEnumerable<LedgerMovement> movements, int dateTolerance = 3)
    {
        ArgumentNullException.ThrowIfNull(movements);
        ArgumentOutOfRangeException.ThrowIfNegative(dateTolerance);

        ThrowIfSignedOff();

        var taken = _lines
            .Where(line => line.MatchedToId is not null)
            .Select(line => line.MatchedToId!.Value)
            .ToHashSet();

        var available = movements
            .Where(movement => !taken.Contains(movement.JournalEntryId.Value))
            .ToList();

        var suggested = 0;

        foreach (var line in _lines.Where(line => line.State == MatchState.Unmatched))
        {
            var candidates = available
                .Where(movement =>
                    movement.SignedAmount.Round() == line.SignedAmount
                    && Math.Abs(line.ValueDate.DayNumber - movement.EntryDate.DayNumber) <= dateTolerance)
                .ToList();

            if (candidates.Count != 1)
            {
                // None, or more than one. Both are cases for a person to look at.
                continue;
            }

            var match = candidates[0];
            line.Suggest(match.JournalEntryId.Value, match.Narration);
            available.Remove(match);
            suggested++;
        }

        return suggested;
    }

    /// <summary>A clerk confirms what a line is.</summary>
    public void ConfirmMatch(int lineNumber, Guid journalEntryId, string narration, Actor clerk)
    {
        ThrowIfSignedOff();

        if (_lines.Any(other =>
                other.LineNumber != lineNumber
                && other.State == MatchState.Matched
                && other.MatchedToId == journalEntryId))
        {
            throw new InvalidOperationException(
                "That entry is already matched to another line on this statement. One movement " +
                "cannot have reached the bank twice.");
        }

        Line(lineNumber).Match(journalEntryId, narration, clerk);
    }

    /// <summary>A clerk decides a line is not Akiba's money.</summary>
    public void MarkLineNotOurs(int lineNumber, string reason, Actor clerk)
    {
        ThrowIfSignedOff();
        Line(lineNumber).MarkNotOurs(reason, clerk);
    }

    /// <summary>Undoes a decision about a line.</summary>
    public void ClearLine(int lineNumber)
    {
        ThrowIfSignedOff();
        Line(lineNumber).Clear();
    }

    /// <summary>
    /// Works out where the statement and the ledger stand.
    /// </summary>
    /// <param name="ledgerBalance">
    /// The bank account's natural balance as at <see cref="To"/>, summed from the journal.
    /// </param>
    /// <param name="movements">Akiba's movements on this account over the statement period.</param>
    /// <remarks>
    /// <para>
    /// The identity being tested is
    /// <c>ledger + unreconciled statement movement = statement + unpresented ledger movement</c>.
    /// </para>
    /// <para>
    /// It is written that way so that posting a bank charge and matching it does not change the
    /// answer: the charge moves out of the unreconciled column and into the ledger balance by
    /// the same amount. A formula whose answer improves when you post something would reward
    /// posting things.
    /// </para>
    /// </remarks>
    public ReconciliationResult Reconcile(Money ledgerBalance, IEnumerable<LedgerMovement> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);

        var currency = ledgerBalance.Currency;

        var matched = _lines
            .Where(line => line.State == MatchState.Matched)
            .Select(line => line.MatchedToId!.Value)
            .ToHashSet();

        var unmatchedMovements = movements
            .Where(movement => !matched.Contains(movement.JournalEntryId.Value))
            .ToList();

        // Everything the bank shows that Akiba has not matched, including lines a clerk has
        // written off as not ours: the bank's balance carries them and Akiba's never will.
        var unreconciledStatement = _lines
            .Where(line => line.State != MatchState.Matched)
            .Sum(line => line.SignedAmount, currency);

        var unpresented = unmatchedMovements.Sum(movement => movement.SignedAmount, currency);

        var difference =
            ((ledgerBalance + unreconciledStatement) - (ClosingBalance + unpresented)).Round();

        return new ReconciliationResult(
            ledgerBalance.Round(),
            ClosingBalance,
            unreconciledStatement.Round(),
            unpresented.Round(),
            difference,
            [.. _lines.Where(line => !line.IsResolved).OrderBy(line => line.LineNumber)],
            unmatchedMovements);
    }

    /// <summary>
    /// The treasurer signs the reconciliation off.
    /// </summary>
    /// <remarks>
    /// Only once it balances and every line has been accounted for. After this the lines are
    /// fixed - a signed reconciliation is what the period close rests on, so it cannot quietly
    /// change afterwards.
    /// </remarks>
    /// <exception cref="ReconciliationNotBalancedException">
    /// It does not balance, or lines are still unresolved.
    /// </exception>
    public void SignOff(ReconciliationResult result, Actor treasurer, DateTimeOffset signedOffAtUtc)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!treasurer.IsSpecified)
        {
            throw new ArgumentException(
                "Signing off a reconciliation records who signed it.", nameof(treasurer));
        }

        ThrowIfSignedOff();

        if (!result.MayClosePeriod)
        {
            throw new ReconciliationNotBalancedException(this, result);
        }

        Status = ReconciliationStatus.SignedOff;
        SignedOffBy = treasurer;
        SignedOffAtUtc = signedOffAtUtc;

        Raise(new BankReconciliationSignedOff(Id, BankAccountId, From, To, treasurer, signedOffAtUtc));
    }

    private void ThrowIfSignedOff()
    {
        if (IsSignedOff)
        {
            throw new InvalidOperationException(
                $"The {AccountLabel} reconciliation to {To:d MMMM yyyy} was signed off by " +
                $"{SignedOffBy} and cannot be altered. Import the next statement instead.");
        }
    }

    public override string ToString() =>
        $"{AccountLabel} {From:yyyy-MM-dd} to {To:yyyy-MM-dd} ({_lines.Count} lines, {Status})";
}

/// <summary>Raised when a reconciliation is signed off.</summary>
public sealed record BankReconciliationSignedOff(
    BankStatementId ReconciliationId,
    AccountId BankAccountId,
    DateOnly From,
    DateOnly To,
    Actor SignedOffBy,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Thrown when a reconciliation is signed off, or a period closed, while it does not balance.
/// </summary>
public sealed class ReconciliationNotBalancedException : InvalidOperationException
{
    public ReconciliationNotBalancedException(
        BankReconciliation reconciliation, ReconciliationResult result)
        : base(Explain(reconciliation, result))
    {
        Difference = result?.Difference ?? Money.ZeroKes;
        UnresolvedLineCount = result?.UnresolvedLines.Count ?? 0;
    }

    public ReconciliationNotBalancedException()
        : base("The reconciliation does not balance.")
    {
    }

    public ReconciliationNotBalancedException(string message)
        : base(message)
    {
    }

    public ReconciliationNotBalancedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public Money Difference { get; }

    public int UnresolvedLineCount { get; }

    private static string Explain(BankReconciliation reconciliation, ReconciliationResult result)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        ArgumentNullException.ThrowIfNull(result);

        return $"The {reconciliation.AccountLabel} reconciliation to " +
               $"{reconciliation.To:d MMMM yyyy} is not finished. {result.Verdict}";
    }
}
