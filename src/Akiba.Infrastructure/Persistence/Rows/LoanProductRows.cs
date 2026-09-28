namespace Akiba.Infrastructure.Persistence.Rows;

/// <summary>One row in <c>akiba.loan_product_configs</c>.</summary>
internal sealed class LoanProductConfigRow
{
    /// <summary>The <see cref="Domain.Lending.LoanProduct"/> enum value.</summary>
    public int Product { get; set; }

    /// <summary>Interest as a decimal proportion — e.g. 0.10 for 10%.</summary>
    public decimal InterestRate { get; set; }

    /// <summary>True when the rate applies per month; false for a flat one-time charge.</summary>
    public bool RateIsMonthly { get; set; }

    /// <summary>Hard ceiling on the principal, in KES. Null means no committee-set ceiling.</summary>
    public decimal? MaxPrincipalKes { get; set; }

    /// <summary>Fixed term in months, when the product has no choice. Null for a variable term.</summary>
    public int? FixedTermMonths { get; set; }

    /// <summary>Maximum term in months for products whose term is bounded but not fixed.</summary>
    public int? MaximumTermMonths { get; set; }

    /// <summary>
    /// Whether the product's term comes from the graduated scale rather than from a fixed cap.
    /// </summary>
    public bool UsesGraduatedScale { get; set; }

    /// <summary>
    /// False only for products whose rate the committee has not yet approved — they will price
    /// via <see cref="Domain.Lending.UnconfiguredInterestStrategy"/>.
    /// </summary>
    public bool IsConfigured { get; set; }

    /// <summary>The committee-minute date on which this configuration took effect.</summary>
    public DateOnly EffectiveFrom { get; set; }
}

/// <summary>One band in <c>akiba.term_scale_bands</c>.</summary>
internal sealed class TermScaleBandRow
{
    public int Id { get; set; }

    public int ScaleVersion { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    /// <summary>Lowest principal in the band, inclusive, in KES.</summary>
    public decimal MinPrincipalKes { get; set; }

    /// <summary>Highest principal in the band, inclusive, in KES. Null for the top band.</summary>
    public decimal? MaxPrincipalKes { get; set; }

    public int MaxTermMonths { get; set; }
}
