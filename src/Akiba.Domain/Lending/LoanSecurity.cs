using Akiba.Domain.Financial;

namespace Akiba.Domain.Lending;

/// <summary>What is offered as security, as ticked on the application form.</summary>
public enum LoanSecurityKind
{
    Guarantors = 1,
    SocietyShares = 2,

    /// <summary>
    /// Rental income, evidenced by rent statements. Only on the revised form.
    /// </summary>
    RentalIncome = 3,

    Other = 99,
}

/// <summary>
/// One item of security offered against a loan.
/// </summary>
/// <remarks>
/// The form lets an applicant offer more than one - guarantors and society shares together
/// are the usual case - so a loan carries a list rather than a single kind.
/// </remarks>
public sealed record LoanSecurity(LoanSecurityKind Kind, string Details)
{
    public static LoanSecurity Guarantors(string details = "See guarantor schedule") =>
        new(LoanSecurityKind.Guarantors, details);

    public static LoanSecurity SocietyShares(Money shareValue) =>
        new(LoanSecurityKind.SocietyShares, shareValue.ToString());

    public static LoanSecurity Other(string details) => new(LoanSecurityKind.Other, details);
}

/// <summary>
/// The property details a rental-income loan application carries.
/// </summary>
/// <remarks>
/// <para>
/// Every field here comes from the revised application form, which asks for the property
/// name, its location, the monthly rental income, the number of units, and rent statements
/// as proof.
/// </para>
/// <para>
/// The form establishes that this product exists and what evidence is collected. It does not
/// state the rate, the term scale, or whether the two-thirds affordability rule is applied to
/// rental income, to gross salary, or to both. None of that is assumed anywhere - see
/// docs/open-questions.md, item 7.
/// </para>
/// </remarks>
public sealed record RentalIncomeSecurity
{
    public RentalIncomeSecurity(
        string propertyName,
        string propertyLocation,
        Money monthlyRentalIncome,
        int numberOfUnits,
        bool rentStatementsAttached)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyLocation);

        if (!monthlyRentalIncome.IsPositive)
        {
            throw new ArgumentException(
                "A rental income loan is secured by rental income, so the monthly income must " +
                "be stated and positive.",
                nameof(monthlyRentalIncome));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(numberOfUnits, 1);

        PropertyName = propertyName.Trim();
        PropertyLocation = propertyLocation.Trim();
        MonthlyRentalIncome = monthlyRentalIncome.Round();
        NumberOfUnits = numberOfUnits;
        RentStatementsAttached = rentStatementsAttached;
    }

    public string PropertyName { get; }

    public string PropertyLocation { get; }

    public Money MonthlyRentalIncome { get; }

    public int NumberOfUnits { get; }

    /// <summary>
    /// Whether the rent statements the form requires are on file. The form says to attach
    /// proof of rental income, so an application without it is incomplete.
    /// </summary>
    public bool RentStatementsAttached { get; }

    public LoanSecurity AsSecurity() => new(
        LoanSecurityKind.RentalIncome,
        $"{PropertyName}, {PropertyLocation} - {NumberOfUnits} unit(s), {MonthlyRentalIncome} per month");
}

/// <summary>
/// The cheque a loan was disbursed by, and the voucher filed with it.
/// </summary>
/// <remarks>
/// Two signatories sign every cheque, and a voucher carrying the cheque details is signed and
/// attached to the loan application. Both are recorded so that a figure on a screen leads
/// back to a piece of paper in a file.
/// </remarks>
public sealed record ChequeDetails
{
    public ChequeDetails(
        string chequeNumber,
        string voucherReference,
        Money amount,
        DateOnly drawnOn,
        IReadOnlyList<string> signatories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chequeNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(voucherReference);
        ArgumentNullException.ThrowIfNull(signatories);

        if (!amount.IsPositive)
        {
            throw new ArgumentException("A cheque must be for a positive amount.", nameof(amount));
        }

        if (signatories.Count < 2)
        {
            throw new ArgumentException(
                "Every Akiba cheque carries two signatories.", nameof(signatories));
        }

        ChequeNumber = chequeNumber.Trim();
        VoucherReference = voucherReference.Trim();
        Amount = amount.Round();
        DrawnOn = drawnOn;
        Signatories = [.. signatories];
    }

    public string ChequeNumber { get; }

    public string VoucherReference { get; }

    public Money Amount { get; }

    public DateOnly DrawnOn { get; }

    public IReadOnlyList<string> Signatories { get; }

    public override string ToString() => $"Cheque {ChequeNumber} for {Amount} on {DrawnOn:yyyy-MM-dd}";
}
