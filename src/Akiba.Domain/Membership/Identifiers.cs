namespace Akiba.Domain.Membership;

/// <summary>
/// Identifies a borrower - a member, or a non-member client.
/// </summary>
/// <remarks>
/// One identifier type for both, because a loan is made to a borrower and does not care
/// which kind it is. What differs is that a member also holds shares.
/// </remarks>
public readonly record struct BorrowerId(Guid Value)
{
    public static BorrowerId New() => new(Guid.NewGuid());

    public bool IsSpecified => Value != Guid.Empty;

    public override string ToString() => Value.ToString();
}

/// <summary>Identifies a zone or office, through which loan approval is routed.</summary>
public readonly record struct ZoneId(Guid Value)
{
    public static ZoneId New() => new(Guid.NewGuid());

    public bool IsSpecified => Value != Guid.Empty;

    public override string ToString() => Value.ToString();
}
