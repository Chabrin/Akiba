namespace Akiba.Domain.Lending;

/// <summary>The kinds of loan Akiba makes.</summary>
public enum LoanProduct
{
    /// <summary>
    /// A member's ordinary loan. Flat 10% of principal, added once at disbursement, with the
    /// term taken from the graduated scale.
    /// </summary>
    Normal = 1,

    /// <summary>
    /// A member's emergency loan. At most KSh 25,000 over 5 months at a flat 10%:
    /// 25,000 + 2,500 = 27,500, repaid at 5,500 a month.
    /// </summary>
    Emergency = 2,

    /// <summary>
    /// A loan to a non-member client. 3% per month on the principal, for at most 5 months.
    /// </summary>
    Client = 3,

    /// <summary>
    /// A member's loan secured by rental income rather than by payslip.
    /// </summary>
    /// <remarks>
    /// The revised application form establishes that this product exists and what evidence is
    /// collected - property name, location, monthly rent, number of units, and rent statements.
    /// <b>It does not state the rate, the term scale or the affordability basis.</b> Nothing
    /// is assumed; see docs/open-questions.md, item 7.
    /// </remarks>
    RentalIncome = 4,
}

public static class LoanProductExtensions
{
    /// <summary>Whether this product is available to members only.</summary>
    public static bool RequiresMembership(this LoanProduct product) => product != LoanProduct.Client;

    public static string DisplayName(this LoanProduct product) => product switch
    {
        LoanProduct.Normal => "Normal loan",
        LoanProduct.Emergency => "Emergency loan",
        LoanProduct.Client => "Client loan",
        LoanProduct.RentalIncome => "Rental income loan",
        _ => product.ToString(),
    };
}
