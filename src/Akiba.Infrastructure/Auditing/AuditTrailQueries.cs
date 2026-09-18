using System.Globalization;
using System.Text.Json;
using Akiba.Application.Auditing;
using Akiba.Infrastructure.Persistence;
using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Akiba.Infrastructure.Auditing;

/// <summary>
/// Reads the audit trail.
/// </summary>
/// <remarks>
/// Read-only, and there is no write counterpart anywhere. The trail is written by the audit
/// provider on the connection that made the change, and the table refuses UPDATE and DELETE.
/// </remarks>
public sealed class AuditTrailQueries : IAuditTrailQueries
{
    private readonly AkibaDbContext _context;

    public AuditTrailQueries(AkibaDbContext context) => _context = context;

    public async Task<IReadOnlyList<AuditEntry>> ListAsync(
        ListAuditEntriesQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var from = (query.From ?? DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-1))
            .ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var to = (query.To ?? DateOnly.FromDateTime(DateTime.UtcNow))
            .ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

        var rows = _context.AuditEntries
            .AsNoTracking()
            .Where(entry => entry.OccurredAtUtc >= from && entry.OccurredAtUtc <= to);

        if (!string.IsNullOrWhiteSpace(query.TableName))
        {
            rows = rows.Where(entry => entry.TableName == query.TableName);
        }

        if (query.ActorUserId is { } actorId)
        {
            rows = rows.Where(entry => entry.ActorUserId == actorId);
        }

        var page = await rows
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .Take(Math.Clamp(query.Take, 1, 5_000))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. page.Select(ToEntry)];
    }

    public async Task<IReadOnlyList<AuditEntry>> ForRowAsync(
        string tableName, string primaryKey, CancellationToken cancellationToken = default)
    {
        var rows = await _context.AuditEntries
            .AsNoTracking()
            .Where(entry => entry.TableName == tableName && entry.PrimaryKey == primaryKey)
            .OrderBy(entry => entry.OccurredAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(ToEntry)];
    }

    public async Task<IReadOnlyList<string>> TablesAsync(CancellationToken cancellationToken = default)
    {
        var tables = await _context.AuditEntries
            .AsNoTracking()
            .Select(entry => entry.TableName)
            .Distinct()
            .OrderBy(table => table)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return tables;
    }

    private static AuditEntry ToEntry(AuditEntryRow row) => new(
        row.Id,
        row.OccurredAtUtc,
        row.ActorName,
        row.IpAddress,
        row.TableName,
        row.Action,
        row.PrimaryKey,
        ReadChanges(row.Changes),
        row.Succeeded,
        row.ErrorMessage);

    /// <summary>
    /// Reads the before-and-after values back out of the stored JSON.
    /// </summary>
    /// <remarks>
    /// Tolerant on purpose. The trail outlives the shape of the thing it recorded: a column
    /// renamed or dropped two migrations ago still has entries mentioning it, and a reader that
    /// threw on one of those would make the whole screen unusable at exactly the moment
    /// somebody needed the old records.
    /// </remarks>
    private static IReadOnlyList<AuditChange> ReadChanges(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return
            [
                .. document.RootElement.EnumerateArray().Select(element => new AuditChange(
                    Text(element, "ColumnName") ?? "(unnamed column)",
                    Text(element, "OriginalValue"),
                    Text(element, "NewValue"))),
            ];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Text(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => value.GetRawText(),
        };
    }

    /// <summary>
    /// Writes the trail out as CSV.
    /// </summary>
    /// <remarks>
    /// The brief requires the trail to be exportable, and CSV is what an auditor asks for -
    /// it opens in anything and nothing about it depends on Akiba still running.
    /// </remarks>
    public static byte[] ToCsv(IReadOnlyList<AuditEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var writer = new System.Text.StringBuilder();

        writer.AppendLine(
            "Occurred (UTC),Official,IP address,Table,Action,Row,Column,Before,After,Succeeded,Error");

        foreach (var entry in entries)
        {
            if (entry.Changes.Count == 0)
            {
                writer.AppendLine(Row(entry, null));

                continue;
            }

            foreach (var change in entry.Changes)
            {
                writer.AppendLine(Row(entry, change));
            }
        }

        // With a byte-order mark. GetBytes never writes one, and without it Excel reads a
        // UTF-8 CSV as the system codepage - which turns a member's name into mojibake in the
        // one document an auditor is meant to be able to open anywhere.
        var encoding = new System.Text.UTF8Encoding(true);

        return [.. encoding.GetPreamble(), .. encoding.GetBytes(writer.ToString())];
    }

    private static string Row(AuditEntry entry, AuditChange? change) =>
        string.Join(
            ',',
            [
                Quote(entry.OccurredAtUtc.UtcDateTime.ToString(
                    "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                Quote(entry.Actor),
                Quote(entry.IpAddress),
                Quote(entry.TableName),
                Quote(entry.Action),
                Quote(entry.PrimaryKey),
                Quote(change?.ColumnName),
                Quote(change?.Before),
                Quote(change?.After),
                Quote(entry.Succeeded ? "yes" : "no"),
                Quote(entry.ErrorMessage),
            ]);

    /// <summary>
    /// Quotes a CSV field.
    /// </summary>
    /// <remarks>
    /// Every field is quoted, not only the ones that need it. A member's name contains a comma
    /// often enough, and a trail that shifted a column when it did would be worse than useless.
    /// </remarks>
    private static string Quote(string? value) =>
        $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
