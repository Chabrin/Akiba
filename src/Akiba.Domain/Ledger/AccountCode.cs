namespace Akiba.Domain.Ledger;

/// <summary>
/// The human-readable code for an account, such as <c>1000</c> for Bank or
/// <c>2100-0007</c> for a particular member's shares.
/// </summary>
/// <remarks>
/// Officials refer to accounts by code on paper and in conversation, so the code is part of
/// the domain rather than a display concern. It is unique across the chart of accounts.
/// </remarks>
public readonly record struct AccountCode
{
    private AccountCode(string value) => Value = value;

    public string Value { get; }

    public bool IsSpecified => !string.IsNullOrEmpty(Value);

    public static AccountCode Of(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim();

        if (trimmed.Length > 32)
        {
            throw new ArgumentException(
                "An account code may be at most 32 characters.", nameof(value));
        }

        if (!trimmed.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.'))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid account code. Use letters, digits, hyphens and dots.",
                nameof(value));
        }

        return new AccountCode(trimmed);
    }

    public override string ToString() => IsSpecified ? Value : "(no code)";
}
