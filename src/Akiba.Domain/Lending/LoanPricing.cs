using Akiba.Domain.Financial;

namespace Akiba.Domain.Lending;

/// <summary>
/// What a loan costs and how long it runs: the figures that go on the application form and
/// then into the schedule.
/// </summary>
/// <param name="Product">Which product priced it.</param>
/// <param name="Principal">The amount borrowed.</param>
/// <param name="Interest">The interest charged, at posting precision.</param>
/// <param name="TermMonths">How many instalments.</param>
/// <param name="TermScaleVersion">
/// The version of the graduated scale this term came from. Stored with the loan so that a
/// later change to the bands does not rewrite what the member agreed to.
/// </param>
public sealed record LoanTerms(
    LoanProduct Product,
    Money Principal,
    Money Interest,
    int TermMonths,
    int TermScaleVersion)
{
    /// <summary>Principal plus interest: what the member owes in total.</summary>
    public Money TotalRepayable => Principal + Interest;

    /// <summary>
    /// The monthly instalment where the total divides evenly. The actual schedule is built
    /// with <see cref="Money.Allocate(int)"/>, which is the only thing that guarantees the
    /// instalments sum back to the total exactly.
    /// </summary>
    public Money NominalInstalment => (TotalRepayable * (1m / TermMonths)).Round();
}

/// <summary>
/// The rules for one loan product: how its interest is calculated, and what limits apply.
/// </summary>
public sealed record LoanProductDefinition(
    LoanProduct Product,
    IInterestStrategy InterestStrategy,
    Money? MaximumPrincipal,
    int? FixedTermMonths,
    int? MaximumTermMonths,
    bool UsesGraduatedScale)
{
    /// <summary>
    /// Whether this product's term is fixed by rule. An emergency loan runs five months
    /// whatever the applicant writes on the form.
    /// </summary>
    public bool HasFixedTerm => FixedTermMonths.HasValue;
}

/// <summary>
/// The configured loan products.
/// </summary>
/// <remarks>
/// Each product's rate and limits come from the answered questionnaire. Nothing here is
/// inferred: where the committee did not state a figure, the product carries an
/// <see cref="UnconfiguredInterestStrategy"/> that refuses to price anything.
/// </remarks>
public static class LoanProductCatalogue
{
    /// <summary>
    /// Flat 10% of principal, for both member products. The questionnaire: "Interest is a
    /// flat rate of 10% of the principal, be it normal or emergency."
    /// </summary>
    public const decimal MemberFlatRate = 0.10m;

    /// <summary>3% per month for non-member clients, for at most five months.</summary>
    public const decimal ClientMonthlyRate = 0.03m;

    /// <summary>An emergency loan always runs five months.</summary>
    public const int EmergencyLoanTermMonths = 5;

    /// <summary>A client loan runs at most five months.</summary>
    public const int ClientLoanMaximumTermMonths = 5;

    /// <summary>A member may hold two loans at once, and no more.</summary>
    public const int MaximumConcurrentLoansPerMember = 2;

    /// <summary>The most a member may take as an emergency loan.</summary>
    public static Money EmergencyLoanMaximum { get; } = Money.Kes(25_000m);

    public static LoanProductDefinition Normal { get; } = new(
        LoanProduct.Normal,
        new FlatRateInterestStrategy(MemberFlatRate),
        MaximumPrincipal: null,
        FixedTermMonths: null,
        MaximumTermMonths: null,
        UsesGraduatedScale: true);

    public static LoanProductDefinition Emergency { get; } = new(
        LoanProduct.Emergency,
        new FlatRateInterestStrategy(MemberFlatRate),
        MaximumPrincipal: EmergencyLoanMaximum,
        FixedTermMonths: EmergencyLoanTermMonths,
        MaximumTermMonths: EmergencyLoanTermMonths,
        UsesGraduatedScale: false);

    public static LoanProductDefinition Client { get; } = new(
        LoanProduct.Client,
        new MonthlyRateInterestStrategy(ClientMonthlyRate),
        MaximumPrincipal: null,
        FixedTermMonths: null,
        MaximumTermMonths: ClientLoanMaximumTermMonths,
        UsesGraduatedScale: false);

    /// <summary>
    /// The rental income loan. Modelled, but unpriced - see docs/open-questions.md, item 7.
    /// </summary>
    public static LoanProductDefinition RentalIncome { get; } = new(
        LoanProduct.RentalIncome,
        new UnconfiguredInterestStrategy(LoanProduct.RentalIncome, "docs/open-questions.md item 7"),
        MaximumPrincipal: null,
        FixedTermMonths: null,
        MaximumTermMonths: null,
        UsesGraduatedScale: false);

    public static LoanProductDefinition For(LoanProduct product) => product switch
    {
        LoanProduct.Normal => Normal,
        LoanProduct.Emergency => Emergency,
        LoanProduct.Client => Client,
        LoanProduct.RentalIncome => RentalIncome,
        _ => throw new ArgumentOutOfRangeException(nameof(product), product, "Unknown loan product."),
    };
}

/// <summary>
/// Works out a loan's interest and term from its product, its principal and the borrower's
/// circumstances.
/// </summary>
public sealed class LoanPricing
{
    private readonly GraduatedTermScale _termScale;
    private readonly TenureBasedTerms _tenureTerms;

    /// <summary>
    /// Creates a pricer.
    /// </summary>
    /// <param name="termScale">The version of the graduated scale in force.</param>
    /// <param name="tenureTerms">
    /// The tenure-based extension. Pass <see cref="TenureBasedTerms.Disabled"/> unless the
    /// committee has confirmed the rule.
    /// </param>
    public LoanPricing(GraduatedTermScale? termScale = null, TenureBasedTerms? tenureTerms = null)
    {
        _termScale = termScale ?? GraduatedTermScale.Version1;
        _tenureTerms = tenureTerms ?? TenureBasedTerms.Disabled;
    }

    /// <summary>
    /// Prices a loan.
    /// </summary>
    /// <param name="product">The product applied for.</param>
    /// <param name="principal">The amount applied for.</param>
    /// <param name="membershipYears">
    /// How long the borrower has been a member, used only by the tenure-based terms. Null for
    /// a client, or where that rule is off.
    /// </param>
    /// <param name="requestedTermMonths">
    /// The repayment period the applicant stated on the form. Optional; where it is omitted
    /// the product's own term applies.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The principal is not positive, exceeds the product's maximum, or the requested term is
    /// longer than the product allows.
    /// </exception>
    /// <exception cref="TermNotDefinedException">The graduated scale does not cover the principal.</exception>
    /// <exception cref="InterestRateNotSetException">The product has no rate set.</exception>
    public LoanTerms Price(
        LoanProduct product,
        Money principal,
        int? membershipYears = null,
        int? requestedTermMonths = null)
    {
        var definition = LoanProductCatalogue.For(product);

        // Checked first so that an unpriced product reports its missing rate rather than
        // whatever other rule it also happens to be missing.
        definition.InterestStrategy.EnsureConfigured();

        if (!principal.IsPositive)
        {
            throw new ArgumentException("A loan principal must be positive.", nameof(principal));
        }

        if (definition.MaximumPrincipal is { } maximum && principal > maximum)
        {
            throw new ArgumentException(
                $"A {product.DisplayName().ToLowerInvariant()} may not exceed {maximum}; " +
                $"{principal} was applied for.",
                nameof(principal));
        }

        var termMonths = TermFor(definition, principal, membershipYears, requestedTermMonths);
        var interest = definition.InterestStrategy.InterestOn(principal, termMonths).Round();

        return new LoanTerms(product, principal.Round(), interest, termMonths, _termScale.Version);
    }

    /// <summary>
    /// The terms for a restructured balance: the graduated scale applied to the balance being
    /// restructured, attracting no fresh interest.
    /// </summary>
    public LoanTerms PriceRestructure(Money outstandingBalance)
    {
        if (!outstandingBalance.IsPositive)
        {
            throw new ArgumentException(
                "A restructured balance must be positive.", nameof(outstandingBalance));
        }

        var termMonths = _termScale.RequireTermFor(outstandingBalance);

        return new LoanTerms(
            LoanProduct.Normal,
            outstandingBalance.Round(),
            Money.Zero(outstandingBalance.Currency),
            termMonths,
            _termScale.Version);
    }

    /// <summary>
    /// Works out how many months a loan runs for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The graduated scale gives a <b>maximum</b>, not a fixed term. The application form's
    /// duration guide is headed "Maximum Repayment Period" and the form asks the applicant to
    /// state the period they want, so a member borrowing 60,000 may ask for eight months even
    /// though the scale allows twelve. A shorter term is accepted; a longer one is not.
    /// </para>
    /// <para>
    /// Where the applicant states nothing, the scale's figure applies. That is the common case
    /// and it is how the paper ledger reads.
    /// </para>
    /// </remarks>
    private int TermFor(
        LoanProductDefinition definition,
        Money principal,
        int? membershipYears,
        int? requestedTermMonths)
    {
        if (requestedTermMonths is { } requested)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(requested, 1, nameof(requestedTermMonths));
        }

        if (definition.FixedTermMonths is { } fixedTerm)
        {
            return fixedTerm;
        }

        var maximumTerm = MaximumTermFor(definition, principal, membershipYears);

        if (requestedTermMonths is not { } wanted)
        {
            return maximumTerm;
        }

        if (wanted > maximumTerm)
        {
            throw new ArgumentException(
                $"A {definition.Product.DisplayName().ToLowerInvariant()} of {principal} may run " +
                $"for at most {maximumTerm} months; {wanted} was applied for.",
                nameof(requestedTermMonths));
        }

        return wanted;
    }

    private int MaximumTermFor(
        LoanProductDefinition definition,
        Money principal,
        int? membershipYears)
    {
        if (!definition.UsesGraduatedScale)
        {
            // A client loan is not governed by the graduated scale; it simply caps at five
            // months. Without a stated term there is nothing else to fall back on.
            return definition.MaximumTermMonths
                ?? throw new InvalidOperationException(
                    $"The {definition.Product.DisplayName().ToLowerInvariant()} has no term rule. " +
                    "State the repayment period from the application form.");
        }

        // Where the committee has adopted the tenure rule, a long-standing member with a large
        // loan gets the longer term. Off by default.
        if (_tenureTerms.TermFor(principal, membershipYears) is { } tenureTerm)
        {
            return tenureTerm;
        }

        var scaleTerm = _termScale.RequireTermFor(principal);

        return definition.MaximumTermMonths is { } cap ? Math.Min(scaleTerm, cap) : scaleTerm;
    }
}
