using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Membership;

namespace Akiba.Domain.Receipting;

/// <summary>Identifies a receipt.</summary>
public readonly record struct ReceiptId(Guid Value)
{
    public static ReceiptId New() => new(Guid.NewGuid());

    public bool IsSpecified => Value != Guid.Empty;

    public override string ToString() => Value.ToString();
}

/// <summary>Which of the three ways money reaches Akiba.</summary>
public enum ReceiptChannel
{
    /// <summary>
    /// Payroll deduction. The clerk's schedule reaches HR by the 25th, HR deducts, and the
    /// payments team draws a cheque on the <b>business</b> account.
    /// </summary>
    PayrollDeduction = 1,

    /// <summary>
    /// A landlord member's rent offset. A separate schedule from the employee one, and the
    /// cheque is drawn on the <b>main</b> account. The two must stay distinguishable in the
    /// ledger, because they reconcile against different statements.
    /// </summary>
    LandlordRentOffset = 2,

    /// <summary>
    /// Cash to an official, a bank deposit, M-Pesa, or a cheque. Matched by the member's name
    /// written on the deposit slip.
    /// </summary>
    DirectDeposit = 3,
}

/// <summary>How the money physically arrived.</summary>
public enum ReceiptMethod
{
    Cash = 1,
    BankDeposit = 2,
    Mpesa = 3,
    Cheque = 4,
}

/// <summary>How far along a receipt is towards being money Akiba can rely on.</summary>
public enum ReceiptStatus
{
    /// <summary>
    /// The office knows about it. Not yet confirmed by the bank, and for a cheque, not yet
    /// matured.
    /// </summary>
    Recorded = 1,

    /// <summary>The funds are available. A cheque has matured, or a deposit is confirmed.</summary>
    Cleared = 2,

    /// <summary>Matched against a line on a bank statement.</summary>
    Reconciled = 3,
}

/// <summary>What an allocation was applied to.</summary>
public enum AllocationTarget
{
    /// <summary>A member's regular monthly share contribution.</summary>
    Shares = 1,

    /// <summary>An instalment on a loan.</summary>
    LoanInstalment = 2,

    /// <summary>
    /// An overpayment converted into shareholding. One of the two sanctioned outcomes.
    /// </summary>
    OverpaymentToShares = 3,

    /// <summary>An overpayment refunded by cheque. The other sanctioned outcome.</summary>
    OverpaymentRefund = 4,

    /// <summary>
    /// A lump sum paid into shares, over and above the monthly contribution.
    /// </summary>
    /// <remarks>
    /// The deduction register carries these in their own column - 100,000 in one case, 862 in
    /// another. They increase shareholding exactly as a contribution does, and post to the same
    /// account, so nothing about a balance changes.
    ///
    /// They are a separate target because a member reading their statement should be able to
    /// tell a lump sum they chose to pay from the monthly amount they agreed to, and because
    /// the two arrive by different routes - the monthly figure through payroll, a top-up
    /// usually as a direct deposit.
    /// </remarks>
    ShareTopUp = 5,
}

/// <summary>
/// A decision to apply part of a receipt to something.
/// </summary>
/// <param name="Target">What it was applied to.</param>
/// <param name="TargetId">The member's share account or the loan, depending on the target.</param>
/// <param name="Amount">How much.</param>
/// <param name="AllocatedBy">The accounts clerk who decided.</param>
/// <param name="AllocatedAtUtc">When.</param>
/// <param name="ReversedReason">Why it was undone, where it has been.</param>
public sealed record ReceiptAllocation(
    AllocationTarget Target,
    Guid TargetId,
    Money Amount,
    Actor AllocatedBy,
    DateTimeOffset AllocatedAtUtc,
    string? ReversedReason = null)
{
    public bool IsReversed => ReversedReason is not null;
}

/// <summary>
/// Money that has arrived, and what it was decided to be for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every receipt lands in Unallocated Receipts first.</b> It is then applied to share
/// contributions and loan instalments by an explicit, audited, reversible action taken by the
/// accounts clerk.
/// </para>
/// <para>
/// Allocation is <b>never silently inferred</b>. It is tempting to guess - the amount matches
/// an instalment, so it must be that instalment - but a wrong guess in a system of record is
/// worse than no guess, because it looks like a decision somebody made. Direct deposits are
/// matched by a member's name written on a deposit slip, which is exactly as reliable as it
/// sounds. A human decides; the system records who, when, and what the money moved to.
/// </para>
/// <para>
/// A cheque must mature before it counts. Bank statements arrive quarterly, so a deposit can
/// sit unconfirmed for up to 90 days - which is why balances distinguish confirmed money from
/// unconfirmed.
/// </para>
/// </remarks>
public sealed class Receipt : AggregateRoot<ReceiptId>
{
    private readonly List<ReceiptAllocation> _allocations = [];

    private Receipt(
        ReceiptId id,
        ReceiptChannel channel,
        ReceiptMethod method,
        Money amount,
        DateOnly receivedOn,
        string reference,
        string? payerNameOnSlip,
        BorrowerId? identifiedBorrower,
        DateOnly? expectedClearanceOn)
        : base(id)
    {
        Channel = channel;
        Method = method;
        Amount = amount.Round();
        ReceivedOn = receivedOn;
        Reference = reference;
        PayerNameOnSlip = payerNameOnSlip;
        IdentifiedBorrower = identifiedBorrower;
        ExpectedClearanceOn = expectedClearanceOn;
        Status = ReceiptStatus.Recorded;
    }

    public ReceiptChannel Channel { get; }

    public ReceiptMethod Method { get; }

    public Money Amount { get; }

    public DateOnly ReceivedOn { get; }

    /// <summary>A cheque number, an M-Pesa code, a deposit slip number, or the payroll month.</summary>
    public string Reference { get; }

    /// <summary>
    /// The name written on the deposit slip, for a direct deposit.
    /// </summary>
    /// <remarks>
    /// This is how a direct deposit is matched to a member. It is kept as written, not
    /// normalised to a member's record, because when the two disagree the clerk needs to see
    /// what the slip actually said.
    /// </remarks>
    public string? PayerNameOnSlip { get; }

    /// <summary>Who the clerk decided the money came from. Null until they decide.</summary>
    public BorrowerId? IdentifiedBorrower { get; private set; }

    /// <summary>When a cheque is expected to mature.</summary>
    public DateOnly? ExpectedClearanceOn { get; }

    public ReceiptStatus Status { get; private set; }

    public DateOnly? ClearedOn { get; private set; }

    public DateOnly? ReconciledOn { get; private set; }

    /// <summary>The bank statement line this receipt was matched to.</summary>
    public string? BankStatementReference { get; private set; }

    public IReadOnlyList<ReceiptAllocation> Allocations => _allocations;

    /// <summary>The allocations still standing, ignoring any that were reversed.</summary>
    public IReadOnlyList<ReceiptAllocation> LiveAllocations =>
        [.. _allocations.Where(allocation => !allocation.IsReversed)];

    /// <summary>How much of this receipt has been applied to something.</summary>
    public Money AllocatedAmount =>
        LiveAllocations.Sum(allocation => allocation.Amount, Amount.Currency);

    /// <summary>How much is still sitting in Unallocated Receipts.</summary>
    public Money UnallocatedAmount => Amount - AllocatedAmount;

    public bool IsFullyAllocated => UnallocatedAmount.IsZero;

    /// <summary>
    /// Whether this money can be relied on. A recorded-but-uncleared cheque cannot.
    /// </summary>
    public bool IsConfirmed => Status is ReceiptStatus.Cleared or ReceiptStatus.Reconciled;

    /// <summary>Records a cheque from HR covering a month's payroll deductions.</summary>
    public static Receipt FromPayroll(Money amount, DateOnly receivedOn, string chequeNumber) =>
        new(
            ReceiptId.New(),
            ReceiptChannel.PayrollDeduction,
            ReceiptMethod.Cheque,
            amount,
            receivedOn,
            Required(chequeNumber, nameof(chequeNumber)),
            payerNameOnSlip: null,
            identifiedBorrower: null,
            expectedClearanceOn: null);

    /// <summary>Records a cheque from the payments team covering landlord rent offsets.</summary>
    public static Receipt FromLandlordSchedule(Money amount, DateOnly receivedOn, string chequeNumber) =>
        new(
            ReceiptId.New(),
            ReceiptChannel.LandlordRentOffset,
            ReceiptMethod.Cheque,
            amount,
            receivedOn,
            Required(chequeNumber, nameof(chequeNumber)),
            payerNameOnSlip: null,
            identifiedBorrower: null,
            expectedClearanceOn: null);

    /// <summary>
    /// Records money paid in directly by a member or a client.
    /// </summary>
    /// <param name="amount">How much arrived.</param>
    /// <param name="method">Cash, a bank deposit, M-Pesa or a cheque.</param>
    /// <param name="receivedOn">The date the office learned of it.</param>
    /// <param name="reference">The M-Pesa code, deposit slip number or cheque number.</param>
    /// <param name="payerNameOnSlip">The name written on the slip, kept as written.</param>
    /// <param name="expectedClearanceOn">
    /// When a cheque is expected to mature. Required for a cheque: it is recommended that a
    /// cheque matures before any action is taken on it.
    /// </param>
    public static Receipt FromDirectDeposit(
        Money amount,
        ReceiptMethod method,
        DateOnly receivedOn,
        string reference,
        string payerNameOnSlip,
        DateOnly? expectedClearanceOn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payerNameOnSlip);

        if (method == ReceiptMethod.Cheque && expectedClearanceOn is null)
        {
            throw new ArgumentException(
                "A cheque needs an expected clearance date. Akiba does not act on a cheque " +
                "until it has matured.",
                nameof(expectedClearanceOn));
        }

        return new Receipt(
            ReceiptId.New(),
            ReceiptChannel.DirectDeposit,
            method,
            amount,
            receivedOn,
            Required(reference, nameof(reference)),
            payerNameOnSlip.Trim(),
            identifiedBorrower: null,
            expectedClearanceOn);
    }

    /// <summary>Rebuilds a receipt from storage. For the persistence layer only.</summary>
    public static Receipt Rehydrate(
        ReceiptId id,
        ReceiptChannel channel,
        ReceiptMethod method,
        Money amount,
        DateOnly receivedOn,
        string reference,
        string? payerNameOnSlip,
        BorrowerId? identifiedBorrower,
        DateOnly? expectedClearanceOn,
        ReceiptStatus status,
        DateOnly? clearedOn,
        DateOnly? reconciledOn,
        string? bankStatementReference,
        IEnumerable<ReceiptAllocation> allocations)
    {
        var receipt = new Receipt(
            id, channel, method, amount, receivedOn, reference, payerNameOnSlip,
            identifiedBorrower, expectedClearanceOn)
        {
            Status = status,
            ClearedOn = clearedOn,
            ReconciledOn = reconciledOn,
            BankStatementReference = bankStatementReference,
        };

        receipt._allocations.AddRange(allocations);

        return receipt;
    }

    /// <summary>
    /// Records the clerk's decision about whose money this is.
    /// </summary>
    /// <remarks>
    /// Separate from allocation, and deliberately so. Deciding that a slip reading
    /// "G. NJERI" is Grace Njeri is one judgement; deciding what her money pays for is
    /// another, and each is recorded on its own.
    /// </remarks>
    public void IdentifyPayer(BorrowerId borrowerId)
    {
        if (!borrowerId.IsSpecified)
        {
            throw new ArgumentException("Identify the payer, or leave it unidentified.", nameof(borrowerId));
        }

        IdentifiedBorrower = borrowerId;
    }

    /// <summary>
    /// Marks a cheque as matured, or a deposit as confirmed on the bank statement.
    /// </summary>
    public void Clear(DateOnly clearedOn)
    {
        if (Status != ReceiptStatus.Recorded)
        {
            throw new InvalidOperationException($"A receipt that is {Status} cannot be cleared again.");
        }

        Status = ReceiptStatus.Cleared;
        ClearedOn = clearedOn;

        Raise(new ReceiptCleared(Id, Amount, clearedOn));
    }

    /// <summary>Matches the receipt to a line on a bank statement.</summary>
    public void Reconcile(DateOnly reconciledOn, string bankStatementReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankStatementReference);

        if (Status == ReceiptStatus.Recorded)
        {
            throw new InvalidOperationException(
                "A receipt clears before it is reconciled. Clear it first - a cheque that has " +
                "not matured has not brought any money in.");
        }

        Status = ReceiptStatus.Reconciled;
        ReconciledOn = reconciledOn;
        BankStatementReference = bankStatementReference.Trim();
    }

    /// <summary>
    /// Applies part of the receipt to something.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The allocation would exceed what is left unallocated.
    /// </exception>
    public void Allocate(
        AllocationTarget target,
        Guid targetId,
        Money amount,
        Actor clerk,
        DateTimeOffset allocatedAtUtc)
    {
        if (!clerk.IsSpecified)
        {
            throw new ArgumentException(
                "An allocation records who decided it. Allocation is never inferred.", nameof(clerk));
        }

        if (!amount.IsPositive)
        {
            throw new ArgumentException("An allocation must be for a positive amount.", nameof(amount));
        }

        if (targetId == Guid.Empty)
        {
            throw new ArgumentException("An allocation must name what it applies to.", nameof(targetId));
        }

        if (amount > UnallocatedAmount)
        {
            throw new InvalidOperationException(
                $"Cannot allocate {amount} from this receipt: only {UnallocatedAmount} of the " +
                $"{Amount} received is still unallocated.");
        }

        _allocations.Add(new ReceiptAllocation(target, targetId, amount, clerk, allocatedAtUtc));

        Raise(new ReceiptAllocated(Id, target, targetId, amount, clerk, allocatedAtUtc));
    }

    /// <summary>
    /// Undoes an allocation.
    /// </summary>
    /// <remarks>
    /// The original is kept and marked reversed rather than removed, so the record shows that
    /// a decision was made and then changed - which is what happened.
    /// </remarks>
    public void ReverseAllocation(int allocationIndex, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentOutOfRangeException.ThrowIfNegative(allocationIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(allocationIndex, _allocations.Count);

        var allocation = _allocations[allocationIndex];

        if (allocation.IsReversed)
        {
            throw new InvalidOperationException("That allocation has already been reversed.");
        }

        _allocations[allocationIndex] = allocation with { ReversedReason = reason.Trim() };
    }

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    public override string ToString() =>
        $"{Channel} {Amount} on {ReceivedOn:yyyy-MM-dd} ({Status})";
}

/// <summary>Raised when a cheque matures or a deposit is confirmed.</summary>
public sealed record ReceiptCleared(ReceiptId ReceiptId, Money Amount, DateOnly ClearedOn) : IDomainEvent
{
    public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Raised when part of a receipt is applied to something. Handled by posting the entry that
/// moves the money out of Unallocated Receipts.
/// </summary>
public sealed record ReceiptAllocated(
    ReceiptId ReceiptId,
    AllocationTarget Target,
    Guid TargetId,
    Money Amount,
    Actor AllocatedBy,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
