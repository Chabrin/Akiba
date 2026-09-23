using Akiba.Application;
using Akiba.Application.Abstractions;
using Akiba.Application.Notifications;
using Akiba.Application.Members;
using Akiba.Domain.Common;
using Akiba.Domain.Membership;
using Akiba.Domain.Notifications;
using Akiba.Infrastructure;
using Akiba.Infrastructure.Notifications;
using Akiba.Infrastructure.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Akiba.Infrastructure.Tests.Persistence;

/// <summary>
/// The outbox against a real database.
/// </summary>
/// <remarks>
/// The claim worth testing is not "a message can be queued" but <b>"nothing sends outside
/// Production"</b>. A development database full of invented members with plausible phone
/// numbers is exactly what must never be texted, and the only way to be sure is to run the
/// dispatcher with a sender that would shout if it were ever called.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class NotificationOutboxTests : IAsyncLifetime
{
    private static readonly Actor Clerk =
        new(Guid.Parse("0000A11B-0000-0000-0000-000000000001"), "Oliver Kamau");

    private readonly PostgresFixture _postgres;
    private ServiceProvider _services = null!;
    private RefusingSender _email = null!;
    private ZoneId _zoneId;

    public NotificationOutboxTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await StartAsync(sendingIsAllowed: false);

    public async Task DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task A_message_is_written_down_before_anything_is_attempted()
    {
        var memberId = await EnrolAsync("0001", "grace@example.test");

        var queued = await SendAsync(new QueueNotificationCommand(
            NotificationKind.LoanApproved,
            memberId,
            new NotificationText("Approved", "Your application has been approved."),
            [NotificationChannel.Email, NotificationChannel.Sms]));

        queued.Should().Be(2, because: "the member has both an email address and a phone number");

        var outbox = await SendAsync(new ListNotificationsQuery());

        outbox.Should().HaveCount(2);
        outbox.Should().AllSatisfy(message => message.Status.Should().Be(NotificationStatus.Pending));
        outbox.Should().Contain(message => message.RecipientAddress == "grace@example.test");
    }

    [Fact]
    public async Task Nothing_sends_outside_Production()
    {
        var memberId = await EnrolAsync("0001", "grace@example.test");

        await SendAsync(new QueueNotificationCommand(
            NotificationKind.LoanDisbursed,
            memberId,
            new NotificationText("Paid out", "Your loan has been paid out."),
            [NotificationChannel.Email]));

        var outcome = await SendAsync(new DispatchNotificationsCommand());

        outcome.Attempted.Should().Be(1);
        outcome.Sent.Should().Be(0);
        outcome.Suppressed.Should().Be(1);

        // The sender was never reached. If it had been, it would have thrown.
        _email.Calls.Should().Be(0);

        var outbox = await SendAsync(new ListNotificationsQuery());
        var message = outbox.Should().ContainSingle().Subject;

        message.Status.Should().Be(NotificationStatus.Suppressed);
        message.Body.Should().NotBeEmpty(because: "the message is kept so it can be looked at");
        message.Verdict.Should().StartWith("Not sent:");
    }

    [Fact]
    public async Task A_member_with_no_email_address_is_not_an_error()
    {
        // The society's own register carries members with no email at all. A system that threw
        // here would stop a loan being disbursed because somebody never gave one.
        var memberId = await EnrolAsync("0002", email: null);

        var queued = await SendAsync(new QueueNotificationCommand(
            NotificationKind.LoanApproved,
            memberId,
            new NotificationText("Approved", "Your application has been approved."),
            [NotificationChannel.Email, NotificationChannel.Sms]));

        queued.Should().Be(1, because: "only the phone number is there to send to");

        var outbox = await SendAsync(new ListNotificationsQuery());

        outbox.Should().ContainSingle()
            .Which.Channel.Should().Be(NotificationChannel.Sms);
    }

    [Fact]
    public async Task A_kind_the_office_has_turned_off_is_never_queued()
    {
        var memberId = await EnrolAsync("0001", "grace@example.test");

        // The statement is not texted by default: as an SMS it arrives as three chargeable
        // fragments and is unreadable.
        var queued = await SendAsync(new QueueNotificationCommand(
            NotificationKind.MonthlyStatement,
            memberId,
            new NotificationText("Your statement", "Shareholding: KES 54,000.00"),
            [NotificationChannel.Email, NotificationChannel.Sms]));

        queued.Should().Be(1);

        var outbox = await SendAsync(new ListNotificationsQuery());

        outbox.Should().ContainSingle()
            .Which.Channel.Should().Be(NotificationChannel.Email);
    }

    [Fact]
    public async Task In_Production_a_failure_is_recorded_and_the_message_waits()
    {
        await _services.DisposeAsync();
        await StartAsync(sendingIsAllowed: true);

        var memberId = await EnrolAsync("0001", "grace@example.test");

        await SendAsync(new QueueNotificationCommand(
            NotificationKind.LoanApproved,
            memberId,
            new NotificationText("Approved", "Your application has been approved."),
            [NotificationChannel.Email]));

        var outcome = await SendAsync(new DispatchNotificationsCommand());

        outcome.Sent.Should().Be(0);
        outcome.Failed.Should().Be(1);
        _email.Calls.Should().Be(1, because: "sending is allowed here, so the sender is reached");

        var message = (await SendAsync(new ListNotificationsQuery())).Single();

        message.Status.Should().Be(NotificationStatus.Failed);
        message.Attempts.Should().Be(1);
        message.Note.Should().Contain("mail server");
        message.IsWaiting.Should().BeTrue(because: "an outbox retries rather than losing it");
    }

    [Fact]
    public async Task A_message_that_keeps_failing_eventually_needs_a_person()
    {
        await _services.DisposeAsync();
        await StartAsync(sendingIsAllowed: true);

        var memberId = await EnrolAsync("0001", "grace@example.test");

        await SendAsync(new QueueNotificationCommand(
            NotificationKind.LoanApproved,
            memberId,
            new NotificationText("Approved", "Your application has been approved."),
            [NotificationChannel.Email]));

        for (var pass = 0; pass < Notification.MaximumAttempts; pass++)
        {
            await SendAsync(new DispatchNotificationsCommand());
        }

        var message = (await SendAsync(new ListNotificationsQuery())).Single();

        message.Status.Should().Be(NotificationStatus.GaveUp);
        message.IsWaiting.Should().BeFalse();

        // And a further pass does not touch it again, so it cannot keep costing money.
        var callsBefore = _email.Calls;
        var outcome = await SendAsync(new DispatchNotificationsCommand());

        outcome.Attempted.Should().Be(0);
        _email.Calls.Should().Be(callsBefore);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task StartAsync(bool sendingIsAllowed)
    {
        await _postgres.ResetAsync();

        _email = new RefusingSender(NotificationChannel.Email, sendingIsAllowed);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAkibaApplication();
        services.AddAkibaInfrastructure(_postgres.ConnectionString);
        services.AddAkibaTestUser(Clerk);

        services.AddSingleton<INotificationPolicy>(
            new NotificationPolicy(sendingIsAllowed, "Not Production."));

        services.AddSingleton<INotificationSender>(_email);
        services.AddSingleton<INotificationSender>(
            new RefusingSender(NotificationChannel.Sms, sendingIsAllowed));

        _services = services.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ChartOfAccountsSeeder>().SeedAsync();

        _zoneId = await CreateZoneAsync();
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

    private Task<BorrowerId> EnrolAsync(string membershipNumber, string? email) =>
        SendAsync(new EnrolMemberCommand(
            membershipNumber, $"CAL/{membershipNumber}", "Grace", "Njeri", null,
            "28765432", "0712345678", email, _zoneId, false));

    /// <summary>
    /// A sender that refuses, and shouts if it is reached when it should not have been.
    /// </summary>
    /// <remarks>
    /// The throw is the whole point of it. "Nothing sends outside Production" is only proved by
    /// something that would fail loudly if the claim were false - a sender that quietly
    /// returned success would let the test pass either way.
    /// </remarks>
    private sealed class RefusingSender : INotificationSender
    {
        private readonly bool _mayBeCalled;

        public RefusingSender(NotificationChannel channel, bool mayBeCalled)
        {
            Channel = channel;
            _mayBeCalled = mayBeCalled;
        }

        public NotificationChannel Channel { get; }

        public int Calls { get; private set; }

        public Task<string?> SendAsync(
            Notification notification, CancellationToken cancellationToken = default)
        {
            if (!_mayBeCalled)
            {
                throw new InvalidOperationException(
                    "A sender was reached while sending was not allowed. Nothing may leave the " +
                    "machine outside Production.");
            }

            Calls++;

            return Task.FromResult<string?>("The mail server refused the message.");
        }
    }
}
