using Akiba.Domain.Financial;
using Akiba.Domain.Membership;

namespace Akiba.Domain.Guaranteeing;

/// <summary>
/// One guarantor's undertaking on one loan, as signed on the application form.
/// </summary>
/// <remarks>
/// <para>
/// The form's guarantor table has a row per guarantor carrying payroll number, name, share
/// value and guaranteed amount, and it is signed. All four are recorded, because the share
/// value is what the officials saw at the time and the guaranteed amount is what determines
/// liability.
/// </para>
/// <para>
/// A guarantor must be an Akiba member and a current CAL employee. The number of guarantors
/// is not capped - the questionnaire says only that it should be "a reasonable number" - so
/// nothing here enforces a maximum.
/// </para>
/// </remarks>
public sealed record Guarantee
{
    public Guarantee(
        BorrowerId guarantorId,
        string guarantorName,
        PayrollNumber guarantorPayrollNumber,
        Money guaranteedAmount,
        Money shareValueAtSigning,
        DateOnly signedOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guarantorName);

        if (!guarantorId.IsSpecified)
        {
            throw new ArgumentException("A guarantee must name its guarantor.", nameof(guarantorId));
        }

        if (!guaranteedAmount.IsPositive)
        {
            throw new ArgumentException(
                "A guarantee must be for a positive amount. A guarantor who guarantees nothing " +
                "is not a guarantor.",
                nameof(guaranteedAmount));
        }

        if (shareValueAtSigning.IsNegative)
        {
            throw new ArgumentException(
                "A share value cannot be negative.", nameof(shareValueAtSigning));
        }

        GuarantorId = guarantorId;
        GuarantorName = guarantorName.Trim();
        GuarantorPayrollNumber = guarantorPayrollNumber;
        GuaranteedAmount = guaranteedAmount.Round();
        ShareValueAtSigning = shareValueAtSigning.Round();
        SignedOn = signedOn;
    }

    public BorrowerId GuarantorId { get; }

    /// <summary>
    /// The guarantor's name as written on the form.
    /// </summary>
    /// <remarks>
    /// Stored alongside the id, like <see cref="Common.Actor"/>, because the guarantee is a
    /// permanent record that must stay readable after the member record has changed.
    /// </remarks>
    public string GuarantorName { get; }

    public PayrollNumber GuarantorPayrollNumber { get; }

    /// <summary>The amount this guarantor put their name to.</summary>
    public Money GuaranteedAmount { get; }

    /// <summary>
    /// The guarantor's own shareholding when they signed, as recorded on the form.
    /// </summary>
    /// <remarks>
    /// A snapshot rather than a live figure: it is what the approving officials actually saw,
    /// and re-deriving it later would answer a different question.
    /// </remarks>
    public Money ShareValueAtSigning { get; }

    public DateOnly SignedOn { get; }

    /// <summary>
    /// Whether the guarantor has been released. A guarantor cannot withdraw before the loan
    /// clears, so this is only ever set when the loan is settled or the guarantee is replaced.
    /// </summary>
    public bool IsReleased { get; init; }

    public override string ToString() =>
        $"{GuarantorName} for {GuaranteedAmount}" + (IsReleased ? " (released)" : string.Empty);
}
