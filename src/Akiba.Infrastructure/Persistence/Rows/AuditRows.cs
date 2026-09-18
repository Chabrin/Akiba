namespace Akiba.Infrastructure.Persistence.Rows;

/// <summary>
/// One recorded change: who, when, to what, and what it was before and after.
/// </summary>
/// <remarks>
/// <para>
/// Written by the audit provider through raw SQL on the same connection as the change it
/// records, not through the change tracker - auditing the audit would be a loop. EF Core maps
/// it here so that the trail can be <i>read</i> and exported through the same repository layer
/// as everything else.
/// </para>
/// <para>
/// <b>There is no mapped way to update or delete one.</b> The table also carries a database
/// trigger refusing both, so the claim that the trail is immutable holds against somebody with
/// a psql prompt, not only against Akiba's own code.
/// </para>
/// </remarks>
internal sealed class AuditEntryRow
{
    public Guid Id { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>The signed-in official, where there was one.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Their name as it was at the time, so the trail stays legible after a rename.</summary>
    public string? ActorName { get; set; }

    public string? IpAddress { get; set; }

    public string TableName { get; set; } = string.Empty;

    /// <summary>Insert, Update or Delete.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>The row's key, written the way an official would quote it.</summary>
    public string PrimaryKey { get; set; } = string.Empty;

    /// <summary>Before and after, per column. JSON.</summary>
    public string? Changes { get; set; }

    /// <summary>Every column's value. JSON. What was inserted, or what was deleted.</summary>
    public string? EntityValues { get; set; }

    public bool Succeeded { get; set; }

    public string? ErrorMessage { get; set; }
}
