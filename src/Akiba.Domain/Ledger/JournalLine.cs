using Akiba.Domain.Financial;

namespace Akiba.Domain.Ledger;

/// <summary>
/// One side of a journal entry: an amount posted against an account.
/// </summary>
/// <remarks>
/// <para>
/// The amount is <b>signed</b>, with debits positive and credits negative. Two columns
/// labelled Debit and Credit would be the familiar presentation, but as a storage model they
/// permit states that do not mean anything - both columns filled, or neither - and they turn
/// the balance invariant into a comparison of two sums instead of the far simpler question
/// "does this add to zero?".
/// </para>
/// <para>
/// Use <see cref="Debit"/> and <see cref="Credit"/> to create lines. They take a positive
/// amount and apply the sign, so no call site has to remember the convention.
/// </para>
/// <para>
/// A line is rounded when it is created, because creating a line is the point of posting.
/// Every amount in the ledger is therefore already at posting precision, which is what makes
/// a derived balance an exact sum of exact figures rather than a rounding of a rounding.
/// </para>
/// </remarks>
public sealed class JournalLine
{
    private JournalLine(AccountId accountId, Money signedAmount, string? narration)
    {
        if (!accountId.IsSpecified)
        {
            throw new ArgumentException("A journal line must name an account.", nameof(accountId));
        }

        AccountId = accountId;
        SignedAmount = signedAmount.Round();
        Narration = string.IsNullOrWhiteSpace(narration) ? null : narration.Trim();
    }

    public AccountId AccountId { get; }

    /// <summary>The amount, positive for a debit and negative for a credit.</summary>
    public Money SignedAmount { get; }

    /// <summary>Optional detail for this line, where the entry's narration is not enough.</summary>
    public string? Narration { get; }

    public Currency Currency => SignedAmount.Currency;

    public BalanceSide Side => SignedAmount.IsNegative ? BalanceSide.Credit : BalanceSide.Debit;

    /// <summary>The amount without its sign, as it would be written in a debit or credit column.</summary>
    public Money Magnitude => SignedAmount.Abs();

    /// <summary>Posts a debit. Increases an asset or expense; decreases a liability, equity or income.</summary>
    /// <exception cref="ArgumentException">The amount is negative.</exception>
    public static JournalLine Debit(AccountId accountId, Money amount, string? narration = null)
    {
        EnsureNotNegative(amount, nameof(amount));
        return new JournalLine(accountId, amount, narration);
    }

    /// <summary>Posts a credit. Increases a liability, equity or income; decreases an asset or expense.</summary>
    /// <exception cref="ArgumentException">The amount is negative.</exception>
    public static JournalLine Credit(AccountId accountId, Money amount, string? narration = null)
    {
        EnsureNotNegative(amount, nameof(amount));
        return new JournalLine(accountId, -amount, narration);
    }

    /// <summary>
    /// Rebuilds a line from storage, where the sign is already applied. For the persistence
    /// layer only.
    /// </summary>
    public static JournalLine Rehydrate(AccountId accountId, Money signedAmount, string? narration) =>
        new(accountId, signedAmount, narration);

    /// <summary>This line with its sign flipped, for building a reversing entry.</summary>
    public JournalLine Negated() => new(AccountId, -SignedAmount, Narration);

    private static void EnsureNotNegative(Money amount, string parameterName)
    {
        if (amount.IsNegative)
        {
            throw new ArgumentException(
                $"Post {amount.Abs()} to the other side instead of posting a negative amount. " +
                "Debit and Credit both take a positive figure and apply the sign themselves.",
                parameterName);
        }
    }

    public override string ToString() =>
        $"{Side} {Magnitude} to {AccountId}" + (Narration is null ? string.Empty : $" ({Narration})");
}
