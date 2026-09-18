using Akiba.Domain.Ledger;

namespace Akiba.Domain.Membership;

/// <summary>
/// A CAL employee who holds shares in Akiba.
/// </summary>
/// <remarks>
/// <para>
/// <b>Membership begins at the first share contribution, not at the application letter.</b>
/// Joining involves writing to the chairman, and that letter is filed as a document - but the
/// date that counts, and that any tenure rule would be measured from, is the date money first
/// arrived. That date is therefore derived from the ledger, not stored here. See
/// <see cref="Shareholding.MembershipStartDate"/>.
/// </para>
/// <para>
/// There is no <c>Shareholding</c> property on this class and there never will be. A member's
/// shareholding is the balance of their share account, derived as at a date.
/// </para>
/// </remarks>
public sealed class Member : Borrower
{
    private Member(
        BorrowerId id,
        MembershipNumber membershipNumber,
        PayrollNumber payrollNumber,
        PersonName name,
        NationalId nationalId,
        PhoneNumber phone,
        string? email,
        ZoneId zoneId,
        AccountId sharesAccountId)
        : base(id, name, nationalId, phone, email)
    {
        MembershipNumber = membershipNumber;
        PayrollNumber = payrollNumber;
        ZoneId = zoneId;
        SharesAccountId = sharesAccountId;
        EmploymentStatus = EmploymentStatus.Employed;
    }

    public MembershipNumber MembershipNumber { get; private set; }

    /// <summary>
    /// The member's CAL payroll number, where they have one.
    /// </summary>
    /// <remarks>
    /// Optional. The deduction register carries shareholders who are not on the payroll, and
    /// they are deducted by other means. <see cref="IsOnPayroll"/> is the question most rules
    /// actually want to ask.
    /// </remarks>
    public PayrollNumber PayrollNumber { get; private set; }

    /// <summary>
    /// Whether this member is deducted at source through CAL payroll.
    /// </summary>
    /// <remarks>
    /// This is what arrears reasoning turns on. A member deducted at source cannot really miss
    /// a payment; one who is not, can.
    /// </remarks>
    public bool IsOnPayroll => PayrollNumber.IsSpecified;

    /// <summary>
    /// The zone or office whose representatives approve this member's loan applications.
    /// </summary>
    public ZoneId ZoneId { get; private set; }

    /// <summary>
    /// The member's share account. Their shareholding is this account's balance as at a date.
    /// </summary>
    public AccountId SharesAccountId { get; }

    public EmploymentStatus EmploymentStatus { get; private set; }

    /// <summary>The date the member left CAL, where they have.</summary>
    public DateOnly? ExitedOn { get; private set; }

    /// <summary>
    /// A member who is also a CAL landlord. Their deductions run on a separate schedule from
    /// the employee one, and the cheque is drawn from the main account rather than the
    /// business account.
    /// </summary>
    public bool IsLandlord { get; private set; }

    public bool IsActive => EmploymentStatus == EmploymentStatus.Employed;

    public override bool HoldsShares => true;

    public static Member Join(
        MembershipNumber membershipNumber,
        PayrollNumber payrollNumber,
        PersonName name,
        NationalId nationalId,
        PhoneNumber phone,
        string? email,
        ZoneId zoneId,
        AccountId sharesAccountId)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!sharesAccountId.IsSpecified)
        {
            throw new ArgumentException(
                "A member needs a share account from the outset; their shareholding is that " +
                "account's balance.",
                nameof(sharesAccountId));
        }

        if (!zoneId.IsSpecified)
        {
            throw new ArgumentException(
                "A member belongs to a zone or office, whose representatives approve their " +
                "loan applications.",
                nameof(zoneId));
        }

        return new Member(
            BorrowerId.New(),
            membershipNumber,
            payrollNumber,
            name,
            nationalId,
            phone,
            email,
            zoneId,
            sharesAccountId);
    }

    /// <summary>Rebuilds a member from storage. For the persistence layer only.</summary>
    public static Member Rehydrate(
        BorrowerId id,
        MembershipNumber membershipNumber,
        PayrollNumber payrollNumber,
        PersonName name,
        NationalId nationalId,
        PhoneNumber phone,
        string? email,
        ZoneId zoneId,
        AccountId sharesAccountId,
        EmploymentStatus employmentStatus,
        DateOnly? exitedOn,
        bool isLandlord) =>
        new(id, membershipNumber, payrollNumber, name, nationalId, phone, email, zoneId, sharesAccountId)
        {
            EmploymentStatus = employmentStatus,
            ExitedOn = exitedOn,
            IsLandlord = isLandlord,
        };

    /// <summary>
    /// Records that the member has left CAL.
    /// </summary>
    /// <remarks>
    /// This is consequential rather than administrative. Any loan balance is recovered from
    /// final dues, and a shortfall passes to the guarantors. Separately, a guarantor must be a
    /// current CAL employee, so every loan this member guarantees is flagged - the borrower is
    /// asked to find a replacement where the loan balance exceeds their own shares, and this
    /// member's funds are not released until one is found.
    /// </remarks>
    public void ExitEmployment(DateOnly exitedOn, DateTimeOffset recordedAtUtc)
    {
        if (EmploymentStatus == EmploymentStatus.Exited)
        {
            throw new InvalidOperationException(
                $"{Name} was already recorded as having left CAL on {ExitedOn:yyyy-MM-dd}.");
        }

        EmploymentStatus = EmploymentStatus.Exited;
        ExitedOn = exitedOn;

        Raise(new MemberExitedEmployment(Id, Name.Full, exitedOn, recordedAtUtc));
    }

    /// <summary>Reverses an exit recorded in error, or a member rejoining CAL.</summary>
    public void ReinstateEmployment()
    {
        EmploymentStatus = EmploymentStatus.Employed;
        ExitedOn = null;
    }

    public void MarkAsLandlord() => IsLandlord = true;

    public void ClearLandlordStatus() => IsLandlord = false;

    public void TransferToZone(ZoneId zoneId)
    {
        if (!zoneId.IsSpecified)
        {
            throw new ArgumentException("A member must belong to a zone.", nameof(zoneId));
        }

        ZoneId = zoneId;
    }
}
