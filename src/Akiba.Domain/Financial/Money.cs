using System.Globalization;

namespace Akiba.Domain.Financial;

/// <summary>
/// An amount of money in a currency. The only way money is represented in Akiba.
/// </summary>
/// <remarks>
/// <para>
/// The amount is a <see cref="decimal"/>, which is base-10 and exact for the arithmetic
/// Akiba does. <see cref="double"/> and <see cref="float"/> are base-2 and cannot represent
/// 0.10 at all; ten of them do not sum to 1.00. In a system that must reconcile against a
/// bank statement and reproduce a figure a member wrote down by hand, that is
/// disqualifying, and no amount of rounding at the end repairs it.
/// </para>
/// <para>
/// Money carries the precision it was given and rounds only when asked, because rounding
/// twice is how a figure ends up a cent away from anything anybody can reproduce. The
/// ledger rounds once, at the point of posting. See <see cref="MoneyRounding"/>.
/// </para>
/// <para>
/// Bare <see cref="decimal"/> is not allowed to cross a layer boundary. If a method
/// signature says <c>decimal</c> and means shillings, that is a bug.
/// </para>
/// </remarks>
public readonly record struct Money : IComparable<Money>, IComparable, IFormattable
{
    /// <summary>Creates an amount in a currency.</summary>
    /// <exception cref="ArgumentException">The currency is <c>default(Currency)</c>.</exception>
    public Money(decimal amount, Currency currency)
    {
        if (!currency.IsSpecified)
        {
            throw new ArgumentException(
                "Money must have a currency. Use Currency.Kes.", nameof(currency));
        }

        Amount = amount;
        Currency = currency;
    }

    /// <summary>The amount, at whatever precision it was created with.</summary>
    public decimal Amount { get; }

    /// <summary>The currency the amount is in.</summary>
    public Currency Currency { get; }

    /// <summary>Zero in the given currency.</summary>
    public static Money Zero(Currency currency) => new(0m, currency);

    /// <summary>Zero shillings. The overwhelmingly common case.</summary>
    public static Money ZeroKes => new(0m, Currency.Kes);

    /// <summary>Creates a shilling amount.</summary>
    public static Money Kes(decimal amount) => new(amount, Currency.Kes);

    public bool IsZero => Amount == 0m;

    public bool IsPositive => Amount > 0m;

    public bool IsNegative => Amount < 0m;

    /// <summary>The amount rounded to the currency's decimal places under the single policy.</summary>
    /// <seealso cref="MoneyRounding"/>
    public Money Round() => new(MoneyRounding.Round(Amount, Currency), Currency);

    public Money Abs() => new(Math.Abs(Amount), Currency);

    /// <summary>
    /// Multiplies by a rate - an interest rate, a percentage, a proportion. The result keeps
    /// full precision; round it when it is posted, not here.
    /// </summary>
    public Money Multiply(decimal rate) => new(Amount * rate, Currency);

    // ---------------------------------------------------------------------
    // Allocation
    // ---------------------------------------------------------------------

    /// <summary>
    /// Splits this amount into <paramref name="parts"/> pieces that sum back to it exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Splitting money is where financial systems lose cents. Divide KSh 100 three ways with
    /// naive rounding and you get 33.33 three times, which is 99.99. One cent has vanished;
    /// the journal entry no longer balances and its constructor rejects it.
    /// </para>
    /// <para>
    /// This works in whole minor units, where the division is exact, and hands the remaining
    /// cents to the earliest parts one at a time. Earliest-first is a deliberate choice, not
    /// an accident of the loop: the odd extra cent lands on the first instalment, which is
    /// the one closest to the disbursement and the easiest for a member to check.
    /// </para>
    /// <para>
    /// Negative amounts split symmetrically - the extra cents are negative too.
    /// </para>
    /// </remarks>
    /// <param name="parts">How many pieces. Must be at least one.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="parts"/> is less than one.</exception>
    public IReadOnlyList<Money> Allocate(int parts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parts, 1);
        EnsureUsable();

        var totalMinorUnits = MoneyRounding.ToMinorUnits(Amount, Currency);

        // Truncates toward zero, so the remainder carries the sign of the total and the
        // negative case falls out of the same arithmetic as the positive one.
        var baseShare = decimal.Truncate(totalMinorUnits / parts);
        var remainder = totalMinorUnits - (baseShare * parts);

        var extraCents = (int)Math.Abs(remainder);
        var step = Math.Sign(remainder);

        var allocation = new Money[parts];

        for (var index = 0; index < parts; index++)
        {
            var minorUnits = baseShare + (index < extraCents ? step : 0);
            allocation[index] = new Money(
                MoneyRounding.FromMinorUnits(minorUnits, Currency), Currency);
        }

        return allocation;
    }

    /// <summary>
    /// Splits this amount in proportion to <paramref name="weights"/>, summing back to it
    /// exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used wherever Akiba divides money unevenly:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <b>Guarantor liability.</b> A guarantor who guaranteed 50,000 of a 200,000 total bears
    /// 0.25 of the outstanding balance, and the guarantors' shares must sum to the whole -
    /// a lost cent here is a member being pursued for the wrong figure.
    /// </item>
    /// <item>
    /// <b>Dividends.</b> Allocated by shareholding, and the total distributed must equal the
    /// total available exactly, or the dividend entry will not balance.
    /// </item>
    /// </list>
    /// <para>
    /// The method is largest-remainder: give everyone their whole minor units, then hand the
    /// leftover cents to whoever was cut off by the most. Ties go to the earliest weight, so
    /// the same inputs always produce the same split - two runs of a dividend computation
    /// must agree to the cent or the treasurer cannot review it.
    /// </para>
    /// </remarks>
    /// <param name="weights">
    /// One non-negative weight per recipient. They need not sum to one - shareholdings and
    /// guaranteed amounts are used directly.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The list is empty, contains a negative weight, or sums to zero.
    /// </exception>
    public IReadOnlyList<Money> Allocate(IReadOnlyList<decimal> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        EnsureUsable();

        if (weights.Count == 0)
        {
            throw new ArgumentException("At least one weight is required.", nameof(weights));
        }

        if (weights.Any(weight => weight < 0m))
        {
            throw new ArgumentException(
                "Weights cannot be negative. A guarantor cannot guarantee a negative amount " +
                "and a member cannot hold negative shares.",
                nameof(weights));
        }

        var totalWeight = weights.Sum();

        if (totalWeight == 0m)
        {
            throw new ArgumentException(
                "Weights sum to zero, so there is no basis on which to divide the amount.",
                nameof(weights));
        }

        var totalMinorUnits = MoneyRounding.ToMinorUnits(Amount, Currency);
        var sign = Math.Sign(totalMinorUnits);
        var absoluteTotal = Math.Abs(totalMinorUnits);

        var wholeUnits = new decimal[weights.Count];
        var shortfalls = new decimal[weights.Count];

        for (var index = 0; index < weights.Count; index++)
        {
            var exact = absoluteTotal * weights[index] / totalWeight;
            wholeUnits[index] = decimal.Floor(exact);
            shortfalls[index] = exact - wholeUnits[index];
        }

        var leftover = (int)(absoluteTotal - wholeUnits.Sum());

        // Hand out the leftover cents to the largest shortfalls first; on a tie, to the
        // earliest weight, so the split is reproducible run to run.
        var order = Enumerable.Range(0, weights.Count)
            .OrderByDescending(index => shortfalls[index])
            .ThenBy(index => index);

        foreach (var index in order.Take(leftover))
        {
            wholeUnits[index] += 1m;
        }

        var allocation = new Money[weights.Count];

        for (var index = 0; index < weights.Count; index++)
        {
            allocation[index] = new Money(
                MoneyRounding.FromMinorUnits(wholeUnits[index] * sign, Currency), Currency);
        }

        return allocation;
    }

    // ---------------------------------------------------------------------
    // Operators
    // ---------------------------------------------------------------------

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static Money operator -(Money value)
    {
        value.EnsureUsable();
        return new Money(-value.Amount, value.Currency);
    }

    public static Money operator *(Money money, decimal rate) => money.Multiply(rate);

    public static Money operator *(decimal rate, Money money) => money.Multiply(rate);

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    // Named alternatives, required by analysers for operator-bearing types and occasionally
    // clearer at a call site than the symbol.
    public static Money Add(Money left, Money right) => left + right;

    public static Money Subtract(Money left, Money right) => left - right;

    public static Money Negate(Money value) => -value;

    // ---------------------------------------------------------------------
    // Comparison and formatting
    // ---------------------------------------------------------------------

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        Money other => CompareTo(other),
        _ => throw new ArgumentException($"Cannot compare Money with {obj.GetType().Name}.", nameof(obj)),
    };

    /// <summary>Formats as, for example, <c>KES 27,500.00</c>.</summary>
    /// <remarks>
    /// Invariant culture, deliberately: this string ends up in logs, audit records and
    /// assertion messages, where it must mean the same thing on the Nairobi machine and on
    /// a CI runner. Formatting money for a person to read is the presentation layer's job.
    /// </remarks>
    public override string ToString() => ToString(null, CultureInfo.InvariantCulture);

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        if (!Currency.IsSpecified)
        {
            return "(unspecified money)";
        }

        formatProvider ??= CultureInfo.InvariantCulture;
        format ??= "N" + Currency.DecimalPlaces.ToString(CultureInfo.InvariantCulture);

        return $"{Currency.Code} {Amount.ToString(format, formatProvider)}";
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        left.EnsureUsable();
        right.EnsureUsable();

        if (left.Currency != right.Currency)
        {
            throw new CurrencyMismatchException(left.Currency, right.Currency);
        }
    }

    /// <summary>
    /// Rejects <c>default(Money)</c>. A struct can always be default-constructed, so an
    /// uninitialised field would otherwise behave as zero shillings and hide the bug that
    /// created it.
    /// </summary>
    private void EnsureUsable()
    {
        if (!Currency.IsSpecified)
        {
            throw new InvalidOperationException(
                "This Money has no currency, which means it is default(Money) - an " +
                "uninitialised field or a struct that skipped its constructor. Use " +
                "Money.ZeroKes or new Money(amount, Currency.Kes).");
        }
    }
}
