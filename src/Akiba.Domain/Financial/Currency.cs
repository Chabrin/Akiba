namespace Akiba.Domain.Financial;

/// <summary>
/// An ISO 4217 currency and the number of decimal places its minor unit uses.
/// </summary>
/// <remarks>
/// Akiba operates in Kenyan shillings and nothing else. This type exists not to support
/// multi-currency accounting, but so that <see cref="Money"/> cannot silently add two
/// amounts that are not the same kind of thing - and so that the day a second currency
/// does appear, it is a new value here rather than a rewrite of every calculation.
/// </remarks>
public readonly record struct Currency
{
    /// <summary>The Kenyan shilling. The only currency Akiba transacts in.</summary>
    public static readonly Currency Kes = new("KES", decimalPlaces: 2);

    private Currency(string code, int decimalPlaces)
    {
        Code = code;
        DecimalPlaces = decimalPlaces;
    }

    /// <summary>The three-letter ISO 4217 code, e.g. <c>KES</c>.</summary>
    public string Code { get; }

    /// <summary>
    /// How many decimal places the minor unit has. Two for the shilling: 100 cents.
    /// </summary>
    public int DecimalPlaces { get; }

    /// <summary>
    /// False for <c>default(Currency)</c>, which carries no code and is not a currency.
    /// </summary>
    /// <remarks>
    /// A struct can always be default-constructed, so this is the only way to tell a real
    /// currency from an uninitialised field. <see cref="Money"/> refuses to do arithmetic
    /// on an unspecified currency rather than quietly treating it as shillings.
    /// </remarks>
    public bool IsSpecified => !string.IsNullOrEmpty(Code);

    /// <summary>
    /// Creates a currency. Akiba only ever uses <see cref="Kes"/>; this exists so the
    /// currency-mismatch guard can be tested, and so adding a currency later does not mean
    /// reopening this type.
    /// </summary>
    /// <exception cref="ArgumentException">The code is not three letters.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The decimal places are negative or above 4.</exception>
    public static Currency Of(string code, int decimalPlaces = 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException(
                $"'{code}' is not a three-letter uppercase ISO 4217 code.", nameof(code));
        }

        // Four is the ceiling because money is stored as numeric(19,4).
        ArgumentOutOfRangeException.ThrowIfNegative(decimalPlaces);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decimalPlaces, 4);

        return new Currency(code, decimalPlaces);
    }

    public override string ToString() => IsSpecified ? Code : "(unspecified currency)";
}
