namespace Akiba.Domain.Financial;

/// <summary>
/// The one and only place Akiba decides how money is rounded.
/// </summary>
/// <remarks>
/// <para>
/// The policy is <see cref="MidpointRounding.AwayFromZero"/> to the currency's decimal
/// places - two, for the shilling. That is the rounding a person does by hand, and Akiba
/// is replacing ledgers that were kept by hand: a member who works out their own instalment
/// on paper should get the number the system got.
/// </para>
/// <para>
/// It is deliberately NOT <see cref="MidpointRounding.ToEven"/>, which .NET uses by default
/// for <see cref="Math.Round(decimal)"/>. Banker's rounding is the better choice when you
/// are trying to avoid statistical drift across millions of independent roundings. Akiba
/// has around forty members, and its problem is not drift - it is a treasurer being able to
/// explain a figure to the person it belongs to.
/// </para>
/// <para>
/// <b>Do not round anywhere else.</b> Rounding is applied at the point of posting to the
/// ledger, not during intermediate arithmetic - rounding twice is how a figure ends up a
/// cent away from what anybody can reproduce. <see cref="Money"/> therefore carries the
/// full precision it was given and rounds only when asked.
/// </para>
/// </remarks>
public static class MoneyRounding
{
    /// <summary>
    /// How midpoints are resolved: 0.005 rounds to 0.01, and -0.005 to -0.01.
    /// </summary>
    public const MidpointRounding Policy = MidpointRounding.AwayFromZero;

    /// <summary>Rounds an amount to the currency's decimal places under the policy.</summary>
    public static decimal Round(decimal amount, Currency currency)
    {
        EnsureSpecified(currency);
        return Math.Round(amount, currency.DecimalPlaces, Policy);
    }

    /// <summary>
    /// Converts an amount to whole minor units - cents, for the shilling - so that splitting
    /// it can be done in integers. Money divides exactly in minor units and does not divide
    /// exactly in major ones, which is the entire reason <see cref="Money.Allocate(int)"/>
    /// works.
    /// </summary>
    public static decimal ToMinorUnits(decimal amount, Currency currency)
    {
        EnsureSpecified(currency);
        return Math.Round(amount * MinorUnitFactor(currency), 0, Policy);
    }

    /// <summary>Converts whole minor units back to a major-unit amount.</summary>
    public static decimal FromMinorUnits(decimal minorUnits, Currency currency)
    {
        EnsureSpecified(currency);
        return minorUnits / MinorUnitFactor(currency);
    }

    /// <summary>
    /// 10^<see cref="Currency.DecimalPlaces"/>, computed in <see cref="decimal"/>.
    /// </summary>
    /// <remarks>
    /// Built by repeated multiplication rather than <see cref="Math.Pow"/> on purpose:
    /// <see cref="Math.Pow"/> is binary floating point, and no part of a money calculation
    /// is allowed to pass through a double - not even one that happens to be exact today.
    /// </remarks>
    public static decimal MinorUnitFactor(Currency currency)
    {
        EnsureSpecified(currency);

        var factor = 1m;

        for (var place = 0; place < currency.DecimalPlaces; place++)
        {
            factor *= 10m;
        }

        return factor;
    }

    private static void EnsureSpecified(Currency currency)
    {
        if (!currency.IsSpecified)
        {
            throw new ArgumentException(
                "Cannot round an amount in an unspecified currency. This usually means a " +
                "default(Money) reached a calculation; construct money with " +
                "Money.Zero(Currency.Kes) or new Money(amount, Currency.Kes).",
                nameof(currency));
        }
    }
}
