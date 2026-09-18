using System.Globalization;
using System.Text.Json;
using Audit.Core;
using Audit.EntityFramework;
using Akiba.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Akiba.Infrastructure.Auditing;

/// <summary>
/// Who did a thing, and from where.
/// </summary>
/// <param name="UserId">The signed-in official's user id, where there is one.</param>
/// <param name="DisplayName">Their name, stored beside the id so the trail stays legible.</param>
/// <param name="IpAddress">The machine the change came from.</param>
/// <remarks>
/// A change with no subject is still recorded - a migration or a startup seed genuinely has
/// no signed-in official, and an audit trail that quietly dropped those would be worse than
/// one that says "(no signed-in official)".
/// </remarks>
public sealed record AuditSubject(Guid? UserId, string? DisplayName, string? IpAddress);

/// <summary>
/// Akiba's audit trail.
/// </summary>
/// <remarks>
/// <para>
/// Built on Audit.NET rather than on a hand-rolled <c>SaveChanges</c> interceptor, because the
/// brief says so and because only one of the two should ever exist - two audit mechanisms
/// disagreeing about what happened is worse than either alone.
/// </para>
/// <para>
/// Every write to every table is recorded with the actor, the time, the before and after values
/// and the IP address. <b>The trail is append-only at the database level</b>, not merely by
/// convention: a trigger refuses UPDATE and DELETE on the table, so an official with a psql
/// prompt cannot quietly edit history either.
/// </para>
/// <para>
/// Audit.NET's configuration is process-wide, which suits a system with one database and one
/// application. It is set up once at startup and the subject is read per change through a
/// callback the host supplies, so the HTTP concerns stay in Akiba.Web where they belong.
/// </para>
/// </remarks>
public static class AuditTrail
{
    /// <summary>Tables that are not audited, and why.</summary>
    /// <remarks>
    /// The audit table itself, because auditing the audit is a loop. The ASP.NET Identity token
    /// and login tables, because they hold authenticator secrets and rotate on every sign-in -
    /// recording them would fill the trail with noise and copy secrets into a second place.
    /// </remarks>
    private static readonly HashSet<string> NotAudited = new(StringComparer.OrdinalIgnoreCase)
    {
        "audit_entries",
        "AspNetUserTokens",
        "AspNetUserLogins",
    };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>
    /// Wires the audit trail up. Called once, from the composition root.
    /// </summary>
    /// <param name="subject">
    /// Reads who is acting and from where. Called on the thread doing the work, so an
    /// implementation may use the current HTTP context.
    /// </param>
    public static void Configure(Func<AuditSubject?> subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        Audit.Core.Configuration.Setup()
            .UseCustomProvider(new AkibaAuditDataProvider(subject))
            .WithCreationPolicy(EventCreationPolicy.InsertOnEnd);

        Audit.EntityFramework.Configuration.Setup()
            .ForContext<AkibaDbContext>(config => config
                .IncludeEntityObjects()
                .AuditEventType("{database}:{context}"));
    }

    /// <summary>The interceptor to hand to <c>AddDbContext</c>.</summary>
    public static AuditSaveChangesInterceptor Interceptor() => new();

    /// <summary>Whether a table is audited.</summary>
    internal static bool IsAudited(string? table) =>
        !string.IsNullOrWhiteSpace(table) && !NotAudited.Contains(table);

    /// <summary>
    /// Writes audit entries into Akiba's own database, on the connection that made the change.
    /// </summary>
    /// <remarks>
    /// Deliberately the same connection and the same transaction as the change being audited.
    /// A separate connection would leave the two able to disagree - a rolled-back transaction
    /// with an audit row saying it happened - and that is precisely the failure an audit trail
    /// must not have.
    /// </remarks>
    private sealed class AkibaAuditDataProvider : AuditDataProvider
    {
        private const string Insert = $"""
            INSERT INTO "{AkibaDbContext.Schema}"."audit_entries"
                ("Id", "OccurredAtUtc", "ActorUserId", "ActorName", "IpAddress",
                 "TableName", "Action", "PrimaryKey", "Changes", "EntityValues",
                 "Succeeded", "ErrorMessage")
            VALUES
                (@id, @occurred, @actorId, @actorName, @ip,
                 @table, @action, @key, CAST(@changes AS jsonb), CAST(@entity AS jsonb),
                 @succeeded, @error)
            """;

        private readonly Func<AuditSubject?> _subject;

        public AkibaAuditDataProvider(Func<AuditSubject?> subject) => _subject = subject;

        public override object InsertEvent(AuditEvent auditEvent) =>
            InsertEventAsync(auditEvent, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<object> InsertEventAsync(
            AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(auditEvent);

            var framework = auditEvent.GetEntityFrameworkEvent();

            if (framework?.GetDbContext() is not AkibaDbContext context)
            {
                return Guid.Empty;
            }

            var subject = _subject();
            var occurred = DateTime.SpecifyKind(auditEvent.StartDate, DateTimeKind.Utc);

            var written = 0;

            foreach (var entry in framework.Entries.Where(entry => IsAudited(entry.Table)))
            {
                await WriteAsync(context, entry, framework, subject, occurred, cancellationToken)
                    .ConfigureAwait(false);

                written++;
            }

            return written;
        }

        private static async Task WriteAsync(
            AkibaDbContext context,
            EventEntry entry,
            EntityFrameworkEvent framework,
            AuditSubject? subject,
            DateTime occurredAtUtc,
            CancellationToken cancellationToken)
        {
            var connection = context.Database.GetDbConnection();

            await using var command = connection.CreateCommand();
            command.CommandText = Insert;

            if (context.Database.CurrentTransaction is { } current
                && current.GetDbTransaction() is { } transaction)
            {
                command.Transaction = transaction;
            }

            Add(command, "id", Guid.NewGuid());
            Add(command, "occurred", occurredAtUtc);
            Add(command, "actorId", subject?.UserId);
            Add(command, "actorName", subject?.DisplayName);
            Add(command, "ip", subject?.IpAddress);
            Add(command, "table", entry.Table);
            Add(command, "action", entry.Action);
            Add(command, "key", DescribeKey(entry));
            Add(command, "changes", Serialise(entry.Changes));
            Add(command, "entity", Serialise(entry.ColumnValues));
            Add(command, "succeeded", framework.Success);
            Add(command, "error", framework.ErrorMessage);

            var wasClosed = connection.State != System.Data.ConnectionState.Open;

            if (wasClosed)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (wasClosed)
                {
                    await connection.CloseAsync().ConfigureAwait(false);
                }
            }
        }

        private static void Add(System.Data.Common.DbCommand command, string name, object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        /// <summary>The row's key, written the way an official would quote it.</summary>
        private static string DescribeKey(EventEntry entry) =>
            entry.PrimaryKey is null or { Count: 0 }
                ? string.Empty
                : string.Join(
                    ", ",
                    entry.PrimaryKey.Select(pair =>
                        $"{pair.Key}={Convert.ToString(pair.Value, CultureInfo.InvariantCulture)}"));

        private static string? Serialise(object? value) =>
            value is null ? null : JsonSerializer.Serialize(value, Json);
    }
}
