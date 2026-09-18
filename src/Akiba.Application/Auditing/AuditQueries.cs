using MediatR;

namespace Akiba.Application.Auditing;

/// <summary>One recorded change, as the panel and the export show it.</summary>
/// <param name="Id">The entry.</param>
/// <param name="OccurredAtUtc">When.</param>
/// <param name="ActorName">Who, or null where no official was signed in.</param>
/// <param name="IpAddress">The machine it came from.</param>
/// <param name="TableName">What was changed.</param>
/// <param name="Action">Insert, Update or Delete.</param>
/// <param name="PrimaryKey">Which row.</param>
/// <param name="Changes">Before and after, per column. Empty for an insert.</param>
/// <param name="Succeeded">Whether the change itself went through.</param>
/// <param name="ErrorMessage">Why it did not, where it did not.</param>
public sealed record AuditEntry(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    string? ActorName,
    string? IpAddress,
    string TableName,
    string Action,
    string PrimaryKey,
    IReadOnlyList<AuditChange> Changes,
    bool Succeeded,
    string? ErrorMessage)
{
    /// <summary>Who acted, in words, including when nobody was signed in.</summary>
    public string Actor => string.IsNullOrWhiteSpace(ActorName)
        ? "(no signed-in official)"
        : ActorName;

    /// <summary>What changed, in one line for a table row.</summary>
    public string Summary => Action switch
    {
        "Insert" => $"Created in {TableName}",
        "Delete" => $"Deleted from {TableName}",
        _ => Changes.Count == 0
            ? $"Saved in {TableName} with no field changed"
            : $"{string.Join(", ", Changes.Take(3).Select(change => change.ColumnName))}" +
              (Changes.Count > 3 ? $" and {Changes.Count - 3} more" : string.Empty) +
              $" changed in {TableName}",
    };
}

/// <summary>One column's before and after.</summary>
/// <param name="ColumnName">Which column.</param>
/// <param name="Before">What it was.</param>
/// <param name="After">What it became.</param>
public sealed record AuditChange(string ColumnName, string? Before, string? After);

/// <summary>What to look for in the trail.</summary>
/// <param name="From">Earliest, inclusive. Defaults to a month back.</param>
/// <param name="To">Latest, inclusive.</param>
/// <param name="TableName">One table, or every table.</param>
/// <param name="ActorUserId">One official, or everybody.</param>
/// <param name="Take">
/// How many to return. The trail grows without limit, so a screen always asks for a page of it.
/// </param>
public sealed record ListAuditEntriesQuery(
    DateOnly? From = null,
    DateOnly? To = null,
    string? TableName = null,
    Guid? ActorUserId = null,
    int Take = 200) : IRequest<IReadOnlyList<AuditEntry>>;

/// <summary>Everything that has been changed about one row, oldest first.</summary>
/// <param name="TableName">Which table.</param>
/// <param name="PrimaryKey">Which row, as the trail records it.</param>
public sealed record GetAuditHistoryQuery(string TableName, string PrimaryKey)
    : IRequest<IReadOnlyList<AuditEntry>>;

/// <summary>The tables that appear in the trail, for the filter.</summary>
public sealed record ListAuditedTablesQuery : IRequest<IReadOnlyList<string>>;

/// <summary>
/// Reads the audit trail.
/// </summary>
/// <remarks>
/// Reads only. There is deliberately no way to write, correct or remove an entry from anywhere
/// in Akiba - the trail is written by the audit provider and is refused UPDATE and DELETE by
/// the database itself.
/// </remarks>
public interface IAuditTrailQueries
{
    Task<IReadOnlyList<AuditEntry>> ListAsync(
        ListAuditEntriesQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEntry>> ForRowAsync(
        string tableName, string primaryKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> TablesAsync(CancellationToken cancellationToken = default);
}

internal sealed class ListAuditEntriesHandler
    : IRequestHandler<ListAuditEntriesQuery, IReadOnlyList<AuditEntry>>
{
    private readonly IAuditTrailQueries _trail;

    public ListAuditEntriesHandler(IAuditTrailQueries trail) => _trail = trail;

    public Task<IReadOnlyList<AuditEntry>> Handle(
        ListAuditEntriesQuery query, CancellationToken cancellationToken) =>
        _trail.ListAsync(query, cancellationToken);
}

internal sealed class GetAuditHistoryHandler
    : IRequestHandler<GetAuditHistoryQuery, IReadOnlyList<AuditEntry>>
{
    private readonly IAuditTrailQueries _trail;

    public GetAuditHistoryHandler(IAuditTrailQueries trail) => _trail = trail;

    public Task<IReadOnlyList<AuditEntry>> Handle(
        GetAuditHistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return _trail.ForRowAsync(query.TableName, query.PrimaryKey, cancellationToken);
    }
}

internal sealed class ListAuditedTablesHandler
    : IRequestHandler<ListAuditedTablesQuery, IReadOnlyList<string>>
{
    private readonly IAuditTrailQueries _trail;

    public ListAuditedTablesHandler(IAuditTrailQueries trail) => _trail = trail;

    public Task<IReadOnlyList<string>> Handle(
        ListAuditedTablesQuery query, CancellationToken cancellationToken) =>
        _trail.TablesAsync(cancellationToken);
}
