namespace Akiba.Domain.Financial;

/// <summary>
/// Thrown when two amounts in different currencies are combined.
/// </summary>
/// <remarks>
/// Akiba is a single-currency system, so in practice this never fires in production. It
/// exists because the alternative - letting the arithmetic proceed and producing a number -
/// would produce a figure that looks right and is meaningless, which is the worst outcome
/// available to a system of record.
/// </remarks>
public sealed class CurrencyMismatchException : InvalidOperationException
{
    public CurrencyMismatchException(Currency left, Currency right)
        : base($"Cannot combine an amount in {left} with an amount in {right}.")
    {
        Left = left;
        Right = right;
    }

    public CurrencyMismatchException()
        : base("Cannot combine amounts in different currencies.")
    {
    }

    public CurrencyMismatchException(string message)
        : base(message)
    {
    }

    public CurrencyMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public Currency Left { get; }

    public Currency Right { get; }
}
