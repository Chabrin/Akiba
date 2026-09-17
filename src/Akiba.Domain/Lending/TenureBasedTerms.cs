using Akiba.Domain.Financial;

namespace Akiba.Domain.Lending;

/// <summary>Longer terms for large loans, by how long someone has been a member.</summary>
/// <param name="FromYears">Lowest membership length in the band, inclusive.</param>
/// <param name="ToYears">Highest membership length, inclusive. Null for the top band.</param>
/// <param name="Months">The repayment term.</param>
public sealed record TenureBand(int FromYears, int? ToYears, int Months)
{
    public bool Contains(int membershipYears) =>
        membershipYears >= FromYears && (ToYears is null || membershipYears <= ToYears);
}

/// <summary>
/// Tenure-based repayment terms for loans of KSh 400,001 and above.
/// </summary>
/// <remarks>
/// <para>
/// <b>The status of this rule is genuinely unclear and the code does not resolve it.</b>
/// The minutes of 6 September 2026 present it as <i>proposed</i> and record no adoption.
/// But the current loan application form already prints all three tiers in its payment
/// duration guide, alongside the 30-month band - so either it is in use, or the form is
/// ahead of the minutes.
/// </para>
/// <para>
/// It is therefore built, tested, and <b>off by default</b>. Turning it on is a
/// configuration change the committee makes, not a decision taken in code.
/// See docs/open-questions.md, item 8.
/// </para>
/// </remarks>
public sealed class TenureBasedTerms
{
    private TenureBasedTerms(bool isEnabled, Money appliesAtOrAbove, IReadOnlyList<TenureBand> bands)
    {
        IsEnabled = isEnabled;
        AppliesAtOrAbove = appliesAtOrAbove;
        Bands = bands;
    }

    /// <summary>Whether the committee has adopted this. Default false.</summary>
    public bool IsEnabled { get; }

    /// <summary>The loan size at which tenure terms come into play: KSh 400,001.</summary>
    public Money AppliesAtOrAbove { get; }

    public IReadOnlyList<TenureBand> Bands { get; }

    /// <summary>The rule as proposed, switched off. This is the default Akiba runs under.</summary>
    public static TenureBasedTerms Disabled { get; } = new(
        isEnabled: false,
        appliesAtOrAbove: Money.Kes(400_000.01m),
        bands: ProposedBands);

    /// <summary>The rule as proposed, switched on. Only if the committee confirms it.</summary>
    public static TenureBasedTerms Enabled { get; } = new(
        isEnabled: true,
        appliesAtOrAbove: Money.Kes(400_000.01m),
        bands: ProposedBands);

    private static IReadOnlyList<TenureBand> ProposedBands =>
    [
        new TenureBand(7, 9, 36),
        new TenureBand(10, 14, 40),
        new TenureBand(15, null, 48),
    ];

    /// <summary>
    /// The extended term for a balance and a membership length, or null where the rule does
    /// not apply - because it is switched off, the loan is too small, or the member has not
    /// been in Akiba long enough.
    /// </summary>
    public int? TermFor(Money balance, int? membershipYears)
    {
        if (!IsEnabled || membershipYears is null || balance < AppliesAtOrAbove)
        {
            return null;
        }

        return Bands.FirstOrDefault(band => band.Contains(membershipYears.Value))?.Months;
    }
}
