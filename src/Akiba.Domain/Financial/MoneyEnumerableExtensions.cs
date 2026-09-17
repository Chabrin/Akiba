namespace Akiba.Domain.Financial;

/// <summary>
/// Summing money. Ledger balances are derived by summing entries, so this is on the hot
/// path of nearly every figure Akiba reports.
/// </summary>
public static class MoneyEnumerableExtensions
{
    /// <summary>
    /// Sums a sequence of amounts, returning zero in <paramref name="currency"/> when the
    /// sequence is empty.
    /// </summary>
    /// <remarks>
    /// The currency is required rather than inferred from the first element, because the
    /// empty case is the common one and it has no first element. A member who joined last
    /// week has no ledger entries yet, and their shareholding is zero shillings - not an
    /// exception, and not <c>default(Money)</c>.
    /// </remarks>
    /// <exception cref="CurrencyMismatchException">
    /// An element is in a different currency.
    /// </exception>
    public static Money Sum(this IEnumerable<Money> source, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(source);

        var total = Money.Zero(currency);

        foreach (var amount in source)
        {
            total += amount;
        }

        return total;
    }

    /// <summary>
    /// Sums a projection of a sequence, returning zero in <paramref name="currency"/> when
    /// the sequence is empty.
    /// </summary>
    public static Money Sum<TSource>(
        this IEnumerable<TSource> source,
        Func<TSource, Money> selector,
        Currency currency)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);

        return source.Select(selector).Sum(currency);
    }
}
