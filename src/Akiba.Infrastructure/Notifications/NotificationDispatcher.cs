using Akiba.Application.Notifications;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Akiba.Infrastructure.Notifications;

/// <summary>
/// Works through the outbox on a timer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this rather than Hangfire, which the brief names.</b> Hangfire earns its keep when
/// jobs are many, varied, and spread across machines that need a shared queue and a dashboard
/// to watch it. Akiba is one machine sending a few dozen messages a month, and it already has
/// the two things Hangfire would have brought: a durable queue - the outbox table, which is a
/// better record than a job store because it keeps the message itself - and a screen showing
/// every message with what became of it.
/// </para>
/// <para>
/// What Hangfire would have added on top is a second database schema, a second set of tables in
/// the nightly backup, and a dashboard that is another authenticated surface to secure. The
/// same reasoning that took Docker out applies here. <b>This is a deviation from the brief and
/// it is a judgement call, not an oversight</b> - if the society would rather have the
/// dashboard, the swap touches this file and the registration, and nothing else.
/// </para>
/// <para>
/// The interval is deliberately unhurried. None of these messages is urgent to the minute, and
/// a loop that hammers an SMS gateway which is accepting and charging for messages it fails to
/// deliver would spend the society's money without anybody watching.
/// </para>
/// </remarks>
internal sealed class NotificationDispatcher : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Long enough that the application is serving requests before anything is sent.
    /// </summary>
    /// <remarks>
    /// Startup already applies migrations and seeds the chart of accounts. Competing with that
    /// for the database on the one machine the society owns helps nobody.
    /// </remarks>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(
        IServiceScopeFactory scopes, ILogger<NotificationDispatcher> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);

        do
        {
            await DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await SafelyWaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private async Task DispatchOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();

            var outcome = await scope.ServiceProvider
                .GetRequiredService<ISender>()
                .Send(new DispatchNotificationsCommand(), stoppingToken)
                .ConfigureAwait(false);

            if (outcome.Attempted > 0)
            {
                _logger.LogInformation(
                    "Notification dispatch: {Attempted} attempted, {Sent} sent, {Failed} to " +
                    "retry, {Suppressed} suppressed.",
                    outcome.Attempted, outcome.Sent, outcome.Failed, outcome.Suppressed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception exception)
        {
            // Deliberately broad, and deliberately swallowed. A dispatcher that dies on one bad
            // pass takes every later message with it, silently - which is precisely the failure
            // an outbox exists to prevent. The messages stay in the table and the next pass
            // tries again.
            _logger.LogError(exception, "A notification dispatch pass failed. It will run again.");
        }
    }

    private static async Task<bool> SafelyWaitAsync(
        PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
