namespace Akiba.Domain.Ledger;

/// <summary>Identifies a ledger account.</summary>
/// <remarks>
/// Strongly typed rather than a bare <see cref="Guid"/> so that an account id cannot be
/// passed where a loan id or a member id is expected. In a system where most identifiers
/// are GUIDs, the compiler is the only thing that will notice.
/// </remarks>
public readonly record struct AccountId(Guid Value)
{
    public static AccountId New() => new(Guid.NewGuid());

    public bool IsSpecified => Value != Guid.Empty;

    public override string ToString() => Value.ToString();
}

/// <summary>Identifies a journal entry.</summary>
public readonly record struct JournalEntryId(Guid Value)
{
    public static JournalEntryId New() => new(Guid.NewGuid());

    public bool IsSpecified => Value != Guid.Empty;

    public override string ToString() => Value.ToString();
}

/// <summary>Identifies an accounting period.</summary>
public readonly record struct AccountingPeriodId(Guid Value)
{
    public static AccountingPeriodId New() => new(Guid.NewGuid());

    public bool IsSpecified => Value != Guid.Empty;

    public override string ToString() => Value.ToString();
}
