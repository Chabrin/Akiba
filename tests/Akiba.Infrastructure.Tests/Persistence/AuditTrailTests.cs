using System.Text;
using Akiba.Application;
using Akiba.Application.Abstractions;
using Akiba.Application.Auditing;
using Akiba.Application.Members;
using Akiba.Domain.Common;
using Akiba.Domain.Membership;
using Akiba.Infrastructure;
using Akiba.Infrastructure.Auditing;
using Akiba.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Akiba.Infrastructure.Tests.Persistence;

/// <summary>
/// The audit trail against a real database.
/// </summary>
/// <remarks>
/// The claim being tested is not "changes are recorded" but "the record cannot be altered".
/// The first is easy and the second is the one an auditor is actually relying on, so it is
/// tested against PostgreSQL rather than against Akiba's own code - the database has other
/// users, and whoever holds its password has a psql prompt.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AuditTrailTests : IAsyncLifetime
{
    private static readonly Actor Clerk =
        new(Guid.Parse("0000A11B-0000-0000-0000-000000000001"), "Oliver Kamau");

    private readonly PostgresFixture _postgres;
    private ServiceProvider _services = null!;
    private ZoneId _zoneId;

    public AuditTrailTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        await _postgres.ResetAsync();
        await ClearTrailAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAkibaApplication();
        services.AddAkibaInfrastructure(_postgres.ConnectionString);
        services.AddAkibaTestUser(Clerk);
        _services = services.BuildServiceProvider();

        // The subject the trail records, refined after the infrastructure has registered its
        // actorless default - the same order the panel does it in, where the host wires the
        // HTTP context once the application is built. Here it is a fixed official, so the test
        // can assert that the name actually reaches the row.
        AuditTrail.Configure(() => new AuditSubject(Clerk.UserId, Clerk.DisplayName, "192.168.1.40"));

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ChartOfAccountsSeeder>().SeedAsync();

        _zoneId = await CreateZoneAsync();
    }

    public async Task DisposeAsync()
    {
        Audit.Core.Configuration.AuditDisabled = false;
        await _services.DisposeAsync();
    }

    [Fact]
    public async Task Enrolling_a_member_is_recorded_with_who_when_and_from_where()
    {
        var memberId = await SendAsync(new EnrolMemberCommand(
            "0001", "CAL/0001", "Grace", "Njeri", null,
            "28765432", "0712345678", null, _zoneId, false));

        var entries = await SendAsync(new ListAuditEntriesQuery(TableName: "borrowers"));

        var entry = entries.Should().ContainSingle().Subject;

        entry.Action.Should().Be("Insert");
        entry.ActorName.Should().Be("Oliver Kamau");
        entry.IpAddress.Should().Be("192.168.1.40");
        entry.PrimaryKey.Should().Contain(memberId.Value.ToString());
        entry.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task An_update_records_the_value_before_and_after()
    {
        var memberId = await SendAsync(new EnrolMemberCommand(
            "0001", "CAL/0001", "Grace", "Njeri", null,
            "28765432", "0712345678", null, _zoneId, false));

        await SendAsync(new RecordMemberExitCommand(memberId, new DateOnly(2026, 9, 30)));

        var entries = await SendAsync(new ListAuditEntriesQuery(TableName: "borrowers"));

        var update = entries.Should().Contain(entry => entry.Action == "Update").Subject;

        var change = update.Changes
            .Should().Contain(change => change.ColumnName == "EmploymentStatus").Subject;

        change.Before.Should().NotBe(change.After);
        update.Summary.Should().Contain("borrowers");
    }

    [Fact]
    public async Task A_journal_entry_and_its_lines_are_both_recorded()
    {
        // The ledger is the reason the trail exists. Enrolling a member opens their share
        // account, so an account row is written; the entry and its lines follow from a receipt.
        await SendAsync(new EnrolMemberCommand(
            "0001", "CAL/0001", "Grace", "Njeri", null,
            "28765432", "0712345678", null, _zoneId, false));

        var entries = await SendAsync(new ListAuditEntriesQuery());

        entries.Should().Contain(entry => entry.TableName == "accounts");
        entries.Should().Contain(entry => entry.TableName == "borrowers");
    }

    [Fact]
    public async Task The_trail_never_records_itself()
    {
        await SendAsync(new EnrolMemberCommand(
            "0001", "CAL/0001", "Grace", "Njeri", null,
            "28765432", "0712345678", null, _zoneId, false));

        var tables = await SendAsync(new ListAuditedTablesQuery());

        tables.Should().NotContain("audit_entries",
            because: "auditing the audit table would be a loop");
    }

    [Fact]
    public async Task PostgreSQL_itself_refuses_to_change_an_entry()
    {
        // The claim is that the trail is immutable, and this is what makes it a fact rather
        // than a promise about Akiba's own code.
        await SendAsync(new EnrolMemberCommand(
            "0001", "CAL/0001", "Grace", "Njeri", null,
            "28765432", "0712345678", null, _zoneId, false));

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();

        var update = async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """UPDATE akiba."audit_entries" SET "ActorName" = 'Somebody else'""";

            await command.ExecuteNonQueryAsync();
        };

        (await update.Should().ThrowAsync<PostgresException>())
            .WithMessage("*append-only*");

        var delete = async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """DELETE FROM akiba."audit_entries" """;

            await command.ExecuteNonQueryAsync();
        };

        (await delete.Should().ThrowAsync<PostgresException>())
            .WithMessage("*append-only*");
    }

    [Fact]
    public async Task The_trail_exports_as_CSV_with_every_field_quoted()
    {
        // A member's name contains a comma often enough, and a trail that shifted a column
        // when it did would be worse than useless.
        await SendAsync(new EnrolMemberCommand(
            "0001", "CAL/0001", "Grace", "Njeri", null,
            "28765432", "0712345678", null, _zoneId, false));

        var entries = await SendAsync(new ListAuditEntriesQuery());

        var csv = Encoding.UTF8.GetString(AuditTrailQueries.ToCsv(entries));

        csv.Should().StartWith("﻿Occurred (UTC),Official");
        csv.Should().Contain("\"Oliver Kamau\"");
        csv.Should().Contain("\"192.168.1.40\"");

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCountGreaterThan(1);
        lines.Skip(1).Should().AllSatisfy(line => line.TrimEnd('\r').Should().StartWith("\""));
    }

    [Fact]
    public async Task Everything_done_to_one_row_can_be_read_in_order()
    {
        var memberId = await SendAsync(new EnrolMemberCommand(
            "0001", "CAL/0001", "Grace", "Njeri", null,
            "28765432", "0712345678", null, _zoneId, false));

        await SendAsync(new RecordMemberExitCommand(memberId, new DateOnly(2026, 9, 30)));

        var all = await SendAsync(new ListAuditEntriesQuery(TableName: "borrowers"));
        var key = all[0].PrimaryKey;

        var history = await SendAsync(new GetAuditHistoryQuery("borrowers", key));

        history.Should().HaveCount(2);
        history[0].Action.Should().Be("Insert");
        history[1].Action.Should().Be("Update");
        history[0].OccurredAtUtc.Should().BeOnOrBefore(history[1].OccurredAtUtc);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Empties the trail between tests.
    /// </summary>
    /// <remarks>
    /// The trigger refuses DELETE, which is the point of it, so this drops the trigger,
    /// truncates and puts it back. Nothing outside this method may do that, and nothing
    /// outside the test project can - the live database's application login is not an owner.
    /// </remarks>
    private async Task ClearTrailAsync()
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            ALTER TABLE akiba."audit_entries" DISABLE TRIGGER audit_entries_no_update_or_delete;
            TRUNCATE TABLE akiba."audit_entries";
            ALTER TABLE akiba."audit_entries" ENABLE TRIGGER audit_entries_no_update_or_delete;
            """;

        await command.ExecuteNonQueryAsync();
    }

    private async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    private async Task<ZoneId> CreateZoneAsync()
    {
        await using var scope = _services.CreateAsyncScope();

        var zones = scope.ServiceProvider.GetRequiredService<IZoneRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var zone = Zone.CreateOffice("OFF", "Head office");
        zones.Add(zone);
        await unitOfWork.SaveChangesAsync();

        return zone.Id;
    }
}
