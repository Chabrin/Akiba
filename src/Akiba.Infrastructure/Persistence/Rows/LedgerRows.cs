namespace Akiba.Infrastructure.Persistence.Rows;

/// <summary>
/// The stored shape of the ledger tables.
/// </summary>
/// <remarks>
/// <para>
/// These are plain rows, not domain objects. The domain aggregates have private constructors,
/// no setters, and invariants enforced on construction - which is exactly what they should
/// have, and exactly what makes them awkward for an ORM to materialise.
/// </para>
/// <para>
/// So the mapping is explicit: EF Core owns these rows, and the repositories translate
/// between a row and the aggregate through its <c>Rehydrate</c> factory. That costs a mapper
/// per aggregate and buys two things worth more than the saving. The domain never bends its
/// shape to suit storage, and rehydration goes through the same invariant checks as
/// construction - so a stored entry that no longer balances fails loudly on read instead of
/// becoming a quietly wrong trial balance.
/// </para>
/// </remarks>
internal sealed class AccountRow
{
    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Type { get; set; }

    public int OwnerKind { get; set; }

    public Guid OwnerId { get; set; }

    public bool IsOpen { get; set; }

    public DateOnly? ClosedOn { get; set; }
}

/// <summary>A journal entry header. Its lines hang off it and are never edited.</summary>
internal sealed class JournalEntryRow
{
    public Guid Id { get; set; }

    public DateOnly EntryDate { get; set; }

    public DateOnly ValueDate { get; set; }

    public string Narration { get; set; } = string.Empty;

    public int SourceDocumentKind { get; set; }

    public string SourceDocumentReference { get; set; } = string.Empty;

    public Guid PostedByUserId { get; set; }

    public string PostedByName { get; set; } = string.Empty;

    public DateTimeOffset PostedAtUtc { get; set; }

    /// <summary>The entry this one reverses, where it is a correction.</summary>
    public Guid? ReversesEntryId { get; set; }

    public List<JournalLineRow> Lines { get; set; } = [];
}

/// <summary>
/// One line of a journal entry.
/// </summary>
/// <remarks>
/// <see cref="SignedAmount"/> is numeric(19,4) with debits positive. Never float, never
/// double, not even for a moment.
/// </remarks>
internal sealed class JournalLineRow
{
    public Guid Id { get; set; }

    public Guid JournalEntryId { get; set; }

    public Guid AccountId { get; set; }

    public decimal SignedAmount { get; set; }

    public string CurrencyCode { get; set; } = "KES";

    public string? Narration { get; set; }

    /// <summary>Preserves the order lines were written in, which is how they read on a voucher.</summary>
    public int Sequence { get; set; }
}

internal sealed class AccountingPeriodRow
{
    public Guid Id { get; set; }

    public int Kind { get; set; }

    public DateOnly Start { get; set; }

    public DateOnly End { get; set; }

    public int Status { get; set; }

    public Guid? ClosedByUserId { get; set; }

    public string? ClosedByName { get; set; }

    public DateTimeOffset? ClosedAtUtc { get; set; }

    public Guid? ReopenedByUserId { get; set; }

    public string? ReopenedByName { get; set; }

    public string? ReopenedReason { get; set; }

    public DateTimeOffset? ReopenedAtUtc { get; set; }
}
