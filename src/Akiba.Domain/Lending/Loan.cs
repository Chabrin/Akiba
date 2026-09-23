using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Guaranteeing;
using Akiba.Domain.Ledger;
using Akiba.Domain.Membership;

namespace Akiba.Domain.Lending;

/// <summary>Identifies a loan.</summary>
public readonly record struct LoanId(Guid Value)
{
    public static LoanId New() => new(Guid.NewGuid());

    public bool IsSpecified => Value != Guid.Empty;

    public override string ToString() => Value.ToString();
}

public enum LoanStatus
{
    /// <summary>Disbursed and being repaid.</summary>
    Running = 1,

    /// <summary>Repaid in full.</summary>
    Settled = 2,

    /// <summary>Closed into a restructured loan. Its balance lives on in the new one.</summary>
    Restructured = 3,

    /// <summary>Written off by the chairman. Rare - there are none today.</summary>
    WrittenOff = 4,
}

/// <summary>
/// A disbursed loan.
/// </summary>
/// <remarks>
/// <para>
/// A loan exists from the moment the cheque is drawn, not from approval. Everything before
/// that is a <see cref="LoanApplication"/>.
/// </para>
/// <para>
/// <b>There is no outstanding balance on this class.</b> What a member owes is the balance of
/// the loan's receivable account, derived from the ledger as at a date - which is what makes
/// it answerable for any past date, and what makes it impossible for the figure to drift away
/// from the entries that produced it.
/// </para>
/// </remarks>
public sealed class Loan : AggregateRoot<LoanId>
{
    private readonly List<Guarantee> _guarantees = [];

    private Loan(
        LoanId id,
        string loanNumber,
        LoanApplicationId applicationId,
        BorrowerId borrowerId,
        AccountId receivableAccountId,
        LoanTerms terms,
        DateOnly disbursedOn,
        ChequeDetails? cheque)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loanNumber);
        ArgumentNullException.ThrowIfNull(terms);

        LoanNumber = loanNumber.Trim().ToUpperInvariant();
        ApplicationId = applicationId;
        BorrowerId = borrowerId;
        ReceivableAccountId = receivableAccountId;
        Terms = terms;
        DisbursedOn = disbursedOn;
        Cheque = cheque;
        Status = LoanStatus.Running;
    }

    /// <summary>
    /// The loan's human-readable number, as written in the LOAN NO. box on the form.
    /// </summary>
    public string LoanNumber { get; }

    public LoanApplicationId ApplicationId { get; }

    public BorrowerId BorrowerId { get; }

    /// <summary>
    /// This loan's receivable account. Its balance as at a date is what the borrower owes.
    /// </summary>
    public AccountId ReceivableAccountId { get; }

    public LoanTerms Terms { get; }

    public DateOnly DisbursedOn { get; }

    /// <summary>
    /// The cheque the money went out on.
    /// </summary>
    /// <remarks>
    /// Null for a loan that came out of a restructure. No money moves in a restructure - the
    /// old loan's balance is carried into the new one by a journal entry - so there is no
    /// cheque, no voucher and no signatories. A zero-amount cheque with an invented number
    /// would have kept this property non-null at the cost of recording something that did not
    /// happen, in the one system whose whole purpose is not doing that.
    /// </remarks>
    public ChequeDetails? Cheque { get; }

    /// <summary>Whether this loan replaced a restructured one.</summary>
    public bool CameFromRestructure => Restructures is not null;

    public LoanStatus Status { get; private set; }

    public IReadOnlyList<Guarantee> Guarantees => _guarantees;

    /// <summary>
    /// The loan this one replaced, where it came out of a restructure.
    /// </summary>
    public LoanId? Restructures { get; private set; }

    /// <summary>
    /// Whether this loan has already been restructured. A loan may be restructured once only.
    /// </summary>
    public bool HasBeenRestructured { get; private set; }

    public DateOnly? SettledOn { get; private set; }

    public bool IsRunning => Status == LoanStatus.Running;

    /// <summary>The schedule of instalments, with its one month of grace.</summary>
    public RepaymentSchedule Schedule => RepaymentSchedule.Generate(Terms, DisbursedOn);

    /// <summary>
    /// Disburses an approved application: draws the cheque and brings the loan into being.
    /// </summary>
    public static Loan Disburse(
        LoanApplication application,
        string loanNumber,
        AccountId receivableAccountId,
        DateOnly disbursedOn,
        ChequeDetails cheque,
        DateTimeOffset disbursedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(cheque);

        if (application.ApprovedTerms is not { } terms)
        {
            throw new InvalidOperationException(
                "An application must be approved on terms before it can be disbursed.");
        }

        if (!receivableAccountId.IsSpecified)
        {
            throw new ArgumentException(
                "A loan needs a receivable account; its outstanding balance is that account's " +
                "balance.",
                nameof(receivableAccountId));
        }

        if (cheque.Amount > terms.Principal)
        {
            throw new InvalidOperationException(
                $"The cheque for {cheque.Amount} exceeds the approved principal of " +
                $"{terms.Principal}.");
        }

        application.MarkDisbursed();

        var loan = new Loan(
            LoanId.New(),
            loanNumber,
            application.Id,
            application.BorrowerId,
            receivableAccountId,
            terms,
            disbursedOn,
            cheque);

        loan._guarantees.AddRange(application.Guarantees);

        loan.Raise(new LoanDisbursed(
            loan.Id, loan.LoanNumber, loan.BorrowerId, terms, cheque, disbursedOn, disbursedAtUtc));

        return loan;
    }

    /// <summary>
    /// Brings into being the loan that replaces a restructured one.
    /// </summary>
    /// <param name="original">The loan being restructured. It is closed by this.</param>
    /// <param name="loanNumber">The replacement's own number, which officials quote.</param>
    /// <param name="receivableAccountId">Its own receivable account.</param>
    /// <param name="terms">From <see cref="Restructuring.TermsFor"/>. No fresh interest.</param>
    /// <param name="restructuredOn">The date the committee agreed it.</param>
    /// <param name="restructuredAtUtc">When it was recorded.</param>
    /// <remarks>
    /// <para>
    /// No cheque, because no money moves: the balance is carried across by a journal entry.
    /// The replacement keeps the original's application, because there was no second
    /// application - the committee agreed to restructure the loan that application produced.
    /// </para>
    /// <para>
    /// <b>The guarantees carry over rather than being re-signed.</b> Asked whether guarantors
    /// must sign again, the questionnaire answered "the terms of the loan still remain", which
    /// is read here as the guarantees continuing unchanged. It is the reading the society gave;
    /// if the committee meant something else, this is the line to change.
    /// </para>
    /// </remarks>
    public static Loan FromRestructure(
        Loan original,
        string loanNumber,
        AccountId receivableAccountId,
        LoanTerms terms,
        DateOnly restructuredOn,
        DateTimeOffset restructuredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(terms);

        if (!receivableAccountId.IsSpecified)
        {
            throw new ArgumentException(
                "A loan needs a receivable account; its outstanding balance is that account's " +
                "balance.",
                nameof(receivableAccountId));
        }

        var replacement = new Loan(
            LoanId.New(),
            loanNumber,
            original.ApplicationId,
            original.BorrowerId,
            receivableAccountId,
            terms,
            restructuredOn,
            cheque: null);

        // Closes the original and points the replacement back at it, in one step, so the two
        // cannot end up half-linked.
        Restructuring.CarryOver(original, replacement, restructuredOn);

        replacement._guarantees.AddRange(original.Guarantees.Where(guarantee => !guarantee.IsReleased));

        replacement.Raise(new LoanRestructured(
            original.Id,
            original.LoanNumber,
            replacement.Id,
            replacement.LoanNumber,
            original.BorrowerId,
            terms,
            restructuredOn,
            restructuredAtUtc));

        return replacement;
    }

    /// <summary>Rebuilds a loan from storage. For the persistence layer only.</summary>
    public static Loan Rehydrate(
        LoanId id,
        string loanNumber,
        LoanApplicationId applicationId,
        BorrowerId borrowerId,
        AccountId receivableAccountId,
        LoanTerms terms,
        DateOnly disbursedOn,
        ChequeDetails? cheque,
        LoanStatus status,
        LoanId? restructures,
        bool hasBeenRestructured,
        DateOnly? settledOn,
        IEnumerable<Guarantee> guarantees)
    {
        var loan = new Loan(
            id, loanNumber, applicationId, borrowerId, receivableAccountId, terms, disbursedOn, cheque)
        {
            Status = status,
            Restructures = restructures,
            HasBeenRestructured = hasBeenRestructured,
            SettledOn = settledOn,
        };

        loan._guarantees.AddRange(guarantees);

        return loan;
    }

    /// <summary>
    /// Marks the loan settled, once the ledger shows its receivable account at zero.
    /// </summary>
    /// <remarks>
    /// The caller establishes that the balance is zero by deriving it. This aggregate does not
    /// hold a balance and so cannot check it itself - which is deliberate.
    /// </remarks>
    public void Settle(DateOnly settledOn)
    {
        if (Status != LoanStatus.Running)
        {
            throw new InvalidOperationException($"A loan that is {Status} cannot be settled.");
        }

        Status = LoanStatus.Settled;
        SettledOn = settledOn;

        Raise(new LoanSettled(Id, LoanNumber, BorrowerId, settledOn));
    }

    /// <summary>Closes this loan into a restructured one.</summary>
    internal void CloseIntoRestructure(DateOnly restructuredOn)
    {
        if (Status != LoanStatus.Running)
        {
            throw new InvalidOperationException($"A loan that is {Status} cannot be restructured.");
        }

        if (HasBeenRestructured)
        {
            throw new InvalidOperationException(
                $"Loan {LoanNumber} has already been restructured. A loan may be restructured " +
                "once only.");
        }

        Status = LoanStatus.Restructured;
        HasBeenRestructured = true;
        SettledOn = restructuredOn;
    }

    /// <summary>Records that this loan came out of restructuring another.</summary>
    internal void RecordAsRestructureOf(LoanId original) => Restructures = original;

    public void WriteOff(Actor chairman, string reason, DateOnly writtenOffOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!chairman.IsSpecified)
        {
            throw new ArgumentException("A write-off records who authorised it.", nameof(chairman));
        }

        if (Status != LoanStatus.Running)
        {
            throw new InvalidOperationException($"A loan that is {Status} cannot be written off.");
        }

        Status = LoanStatus.WrittenOff;
        SettledOn = writtenOffOn;

        Raise(new LoanWrittenOff(Id, LoanNumber, chairman, reason.Trim(), writtenOffOn));
    }

    public override string ToString() => $"{LoanNumber} ({Terms.Product.DisplayName()})";
}

/// <summary>
/// Raised when a cheque has been drawn and a loan exists. Handled by posting the
/// disbursement entry: the borrower owes principal plus interest, the bank pays out the
/// principal, and the difference is recognised as income at once.
/// </summary>
public sealed record LoanDisbursed(
    LoanId LoanId,
    string LoanNumber,
    BorrowerId BorrowerId,
    LoanTerms Terms,
    ChequeDetails Cheque,
    DateOnly DisbursedOn,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Raised when a loan is restructured: the original is closed and a replacement takes its
/// balance.
/// </summary>
/// <param name="OriginalLoanId">The loan that was restructured.</param>
/// <param name="OriginalLoanNumber">Its number, which officials will still quote.</param>
/// <param name="ReplacementLoanId">The loan that took its place.</param>
/// <param name="ReplacementLoanNumber">The number the member will be told.</param>
/// <param name="BorrowerId">Whose loan.</param>
/// <param name="Terms">The replacement's terms. No fresh interest.</param>
/// <param name="RestructuredOn">The date the committee agreed it.</param>
/// <param name="OccurredAtUtc">When it was recorded.</param>
public sealed record LoanRestructured(
    LoanId OriginalLoanId,
    string OriginalLoanNumber,
    LoanId ReplacementLoanId,
    string ReplacementLoanNumber,
    BorrowerId BorrowerId,
    LoanTerms Terms,
    DateOnly RestructuredOn,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>Raised when a loan's receivable account reaches zero.</summary>
public sealed record LoanSettled(
    LoanId LoanId,
    string LoanNumber,
    BorrowerId BorrowerId,
    DateOnly SettledOn) : IDomainEvent
{
    public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
}

/// <summary>Raised when the chairman writes a loan off.</summary>
public sealed record LoanWrittenOff(
    LoanId LoanId,
    string LoanNumber,
    Actor AuthorisedBy,
    string Reason,
    DateOnly WrittenOffOn) : IDomainEvent
{
    public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
}
