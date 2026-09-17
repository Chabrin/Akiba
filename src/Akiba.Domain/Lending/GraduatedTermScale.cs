using Akiba.Domain.Financial;

namespace Akiba.Domain.Lending;

/// <summary>One band of the graduated repayment scale.</summary>
/// <param name="From">Lowest balance in the band, inclusive.</param>
/// <param name="To">Highest balance in the band, inclusive. Null for the top band.</param>
/// <param name="Months">The repayment term for balances in this band.</param>
public sealed record TermBand(Money From, Money? To, int Months)
{
    public bool Contains(Money balance) =>
        balance >= From && (To is null || balance <= To.Value);
}

/// <summary>
/// The graduated repayment scale: how long a member gets to repay, by amount.
/// </summary>
/// <remarks>
/// <para>
/// <b>Versioned, and a loan keeps the version it was written under.</b> When the committee
/// changes a band, existing loans are not recalculated - a member's agreed instalment does
/// not change because a rule changed after they signed. That is why a loan stores its scale
/// version rather than looking the scale up afresh.
/// </para>
/// <para>
/// The scale starts at 30,000. <b>No term is defined below that</b>, on the application form
/// or in the minutes, so <see cref="TermFor"/> returns null rather than guessing.
/// See docs/open-questions.md, item 5.
/// </para>
/// </remarks>
public sealed class GraduatedTermScale
{
    private GraduatedTermScale(int version, DateOnly effectiveFrom, IReadOnlyList<TermBand> bands)
    {
        Version = version;
        EffectiveFrom = effectiveFrom;
        Bands = bands;
    }

    public int Version { get; }

    public DateOnly EffectiveFrom { get; }

    public IReadOnlyList<TermBand> Bands { get; }

    /// <summary>The lowest balance the scale covers.</summary>
    public Money Floor => Bands[0].From;

    /// <summary>
    /// The scale as printed on the loan application form and recorded in the minutes of
    /// 6 September 2026.
    /// </summary>
    public static GraduatedTermScale Version1 { get; } = new(
        version: 1,
        effectiveFrom: new DateOnly(2026, 9, 6),
        bands:
        [
            new TermBand(Money.Kes(30_000m), Money.Kes(50_000m), 8),
            new TermBand(Money.Kes(50_000.01m), Money.Kes(100_000m), 12),
            new TermBand(Money.Kes(100_000.01m), Money.Kes(150_000m), 16),
            new TermBand(Money.Kes(150_000.01m), Money.Kes(200_000m), 20),
            new TermBand(Money.Kes(200_000.01m), Money.Kes(400_000m), 24),
            new TermBand(Money.Kes(400_000.01m), null, 30),
        ]);

    /// <summary>Builds a new version of the scale, for when the committee changes a band.</summary>
    public static GraduatedTermScale NewVersion(
        int version,
        DateOnly effectiveFrom,
        IReadOnlyList<TermBand> bands)
    {
        ArgumentNullException.ThrowIfNull(bands);

        if (bands.Count == 0)
        {
            throw new ArgumentException("A term scale needs at least one band.", nameof(bands));
        }

        return new GraduatedTermScale(version, effectiveFrom, bands);
    }

    /// <summary>
    /// The repayment term for a balance, or null where the scale does not cover it.
    /// </summary>
    /// <remarks>
    /// Null means "the committee has not said", not "zero months". The caller must treat it
    /// as a blocked application rather than substituting a term of its own.
    /// </remarks>
    public int? TermFor(Money balance)
    {
        var band = Bands.FirstOrDefault(band => band.Contains(balance));

        return band?.Months;
    }

    /// <summary>
    /// The repayment term for a balance, or throws where the scale does not cover it.
    /// </summary>
    /// <exception cref="TermNotDefinedException">The balance falls outside every band.</exception>
    public int RequireTermFor(Money balance) =>
        TermFor(balance) ?? throw new TermNotDefinedException(balance, this);
}

/// <summary>
/// Thrown when a balance falls outside every band of the graduated scale - in practice,
/// below KSh 30,000.
/// </summary>
public sealed class TermNotDefinedException : InvalidOperationException
{
    public TermNotDefinedException(Money balance, GraduatedTermScale scale)
        : base(
            $"The graduated scale (version {scale?.Version}) defines no repayment term for " +
            $"{balance}. It starts at {scale?.Floor} and nothing below that is covered by the " +
            "application form or the minutes. This is open question 5 - ask the committee " +
            "rather than choosing a term.")
    {
        Balance = balance;
        ScaleVersion = scale?.Version ?? 0;
    }

    public TermNotDefinedException()
        : base("The graduated scale defines no repayment term for this balance.")
    {
    }

    public TermNotDefinedException(string message)
        : base(message)
    {
    }

    public TermNotDefinedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public Money Balance { get; }

    public int ScaleVersion { get; }
}
