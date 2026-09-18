namespace Akiba.Infrastructure.Persistence.Rows;

/// <summary>A year's dividend run and how far it has got.</summary>
internal sealed class DividendRunRow
{
    public Guid Id { get; set; }

    public int Year { get; set; }

    public decimal InterestEarned { get; set; }

    public decimal BankCharges { get; set; }

    public string CurrencyCode { get; set; } = "KES";

    /// <summary>
    /// Which basis was used, stored as the name rather than as an enum.
    /// </summary>
    /// <remarks>
    /// The basis is unsettled, so the set of them may grow. A name read back and shown on a
    /// posted run stays meaningful even if the strategy it came from is later retired; an enum
    /// value whose member has been renumbered does not.
    /// </remarks>
    public string BasisName { get; set; } = string.Empty;

    public string BasisExplanation { get; set; } = string.Empty;

    public int Status { get; set; }

    public Guid? ComputedByUserId { get; set; }

    public string? ComputedByName { get; set; }

    public DateTimeOffset? ComputedAtUtc { get; set; }

    public Guid? ReviewedByUserId { get; set; }

    public string? ReviewedByName { get; set; }

    public DateTimeOffset? ReviewedAtUtc { get; set; }

    public Guid? ApprovedByUserId { get; set; }

    public string? ApprovedByName { get; set; }

    public DateTimeOffset? ApprovedAtUtc { get; set; }

    public DateOnly? PostedOn { get; set; }

    public DateTimeOffset? PostedAtUtc { get; set; }

    public string? WithdrawnReason { get; set; }

    public List<DividendLineRow> Lines { get; set; } = [];
}

/// <summary>One member's entitlement on a run.</summary>
internal sealed class DividendLineRow
{
    public Guid Id { get; set; }

    public Guid DividendRunId { get; set; }

    public Guid MemberId { get; set; }

    public string MembershipNumber { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public Guid SharesAccountId { get; set; }

    /// <summary>The figure the share was worked out from, under the basis in force.</summary>
    public decimal BasisAmount { get; set; }

    public decimal Amount { get; set; }

    public string CurrencyCode { get; set; } = "KES";

    /// <summary>Preserves the order the run computed them in, which is the allocation order.</summary>
    public int Sequence { get; set; }
}
