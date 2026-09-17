using Akiba.Domain.Financial;

namespace Akiba.Domain.Lending;

/// <summary>
/// How interest is calculated for a loan product.
/// </summary>
/// <remarks>
/// <para>
/// Registered per product, so that adding a product or changing a rate is a configuration
/// change rather than an edit to a calculation somewhere.
/// </para>
/// <para>
/// Note what none of these strategies do: none of them is reducing-balance, and none is
/// expressed per annum. Akiba's member loans charge a flat percentage of the principal,
/// added once at disbursement. That is what the paper ledger shows and what members expect.
/// </para>
/// </remarks>
public interface IInterestStrategy
{
    /// <summary>How the rate is described to a person - on a statement, or in the panel.</summary>
    string Description { get; }

    /// <summary>
    /// The interest charged on a principal over a term.
    /// </summary>
    /// <remarks>
    /// Returns full precision. Rounding happens when the figure is posted, not here.
    /// </remarks>
    Money InterestOn(Money principal, int termMonths);

    /// <summary>
    /// Throws if the committee has not set this product's rate.
    /// </summary>
    /// <remarks>
    /// Called before anything else is worked out, so that a product nobody has priced fails
    /// with "no rate has been set" rather than with whatever the next missing rule happens to
    /// be. A configured strategy does nothing here.
    /// </remarks>
    void EnsureConfigured()
    {
    }
}

/// <summary>
/// A flat percentage of the principal, added once at disbursement and not affected by the
/// term. Used by the members' normal and emergency loans, both at 10%.
/// </summary>
/// <remarks>
/// A 25,000 emergency loan attracts 2,500 whether it runs five months or one. That is
/// deliberate on Akiba's part, not an oversight to be corrected: the charge is for the loan,
/// not for the time.
/// </remarks>
public sealed class FlatRateInterestStrategy : IInterestStrategy
{
    public FlatRateInterestStrategy(decimal rate)
    {
        if (rate < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "An interest rate cannot be negative.");
        }

        Rate = rate;
    }

    /// <summary>The flat rate, as a proportion. 0.10 is 10%.</summary>
    public decimal Rate { get; }

    public string Description => $"Flat {Rate:P0} of principal, charged once at disbursement";

    public Money InterestOn(Money principal, int termMonths)
    {
        EnsureSensible(principal, termMonths);
        return principal * Rate;
    }

    internal static void EnsureSensible(Money principal, int termMonths)
    {
        if (principal.IsNegative)
        {
            throw new ArgumentException("A principal cannot be negative.", nameof(principal));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(termMonths, 1);
    }
}

/// <summary>
/// A percentage of the principal for each month of the term. Used by client loans at 3% a
/// month for at most five months.
/// </summary>
/// <remarks>
/// Still charged on the original principal rather than on the reducing balance, so a five
/// month client loan costs 15% of what was borrowed regardless of how quickly it is repaid.
/// </remarks>
public sealed class MonthlyRateInterestStrategy : IInterestStrategy
{
    public MonthlyRateInterestStrategy(decimal monthlyRate)
    {
        if (monthlyRate < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(monthlyRate), monthlyRate, "An interest rate cannot be negative.");
        }

        MonthlyRate = monthlyRate;
    }

    /// <summary>The rate per month, as a proportion. 0.03 is 3%.</summary>
    public decimal MonthlyRate { get; }

    public string Description => $"{MonthlyRate:P0} per month on principal";

    public Money InterestOn(Money principal, int termMonths)
    {
        FlatRateInterestStrategy.EnsureSensible(principal, termMonths);
        return principal * MonthlyRate * termMonths;
    }
}

/// <summary>
/// No interest. Used by a restructured balance, which carries over what is already owed and
/// attracts no fresh interest.
/// </summary>
public sealed class NoFurtherInterestStrategy : IInterestStrategy
{
    public string Description => "No further interest";

    public Money InterestOn(Money principal, int termMonths)
    {
        FlatRateInterestStrategy.EnsureSensible(principal, termMonths);
        return Money.Zero(principal.Currency);
    }
}

/// <summary>
/// A product whose rate the committee has not set. Refuses to calculate anything.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that the rental income loan can be a real product - modelled, stored,
/// applied for and reported on - without anybody having invented its rate.
/// </para>
/// <para>
/// The alternative would have been to default it to the 10% that normal loans use, which
/// would have been indistinguishable six months later from a rate the committee agreed. A
/// loud failure at the point of calculation is the honest option.
/// </para>
/// </remarks>
public sealed class UnconfiguredInterestStrategy : IInterestStrategy
{
    public UnconfiguredInterestStrategy(LoanProduct product, string openQuestionReference)
    {
        Product = product;
        OpenQuestionReference = openQuestionReference;
    }

    public LoanProduct Product { get; }

    /// <summary>Where the unanswered question is recorded.</summary>
    public string OpenQuestionReference { get; }

    public string Description => $"Rate not set by the committee ({OpenQuestionReference})";

    public Money InterestOn(Money principal, int termMonths) =>
        throw new InterestRateNotSetException(Product, OpenQuestionReference);

    public void EnsureConfigured() =>
        throw new InterestRateNotSetException(Product, OpenQuestionReference);
}

/// <summary>
/// Thrown when a loan is priced with a product whose rate the committee has not decided.
/// </summary>
public sealed class InterestRateNotSetException : InvalidOperationException
{
    public InterestRateNotSetException(LoanProduct product, string openQuestionReference)
        : base(
            $"No interest rate has been set for the {product.DisplayName().ToLowerInvariant()}. " +
            $"The committee has not decided it - see {openQuestionReference}. Do not assume it " +
            "matches another product's rate.")
    {
        Product = product;
    }

    public InterestRateNotSetException()
        : base("No interest rate has been set for this loan product.")
    {
    }

    public InterestRateNotSetException(string message)
        : base(message)
    {
    }

    public InterestRateNotSetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public LoanProduct Product { get; }
}
