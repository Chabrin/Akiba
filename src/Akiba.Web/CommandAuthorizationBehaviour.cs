using System.Net;
using Akiba.Application.Dividends;
using Akiba.Application.Ledger;
using Akiba.Application.Migration;
using Akiba.Application.Notifications;
using Akiba.Application.Reconciliation;
using Akiba.Infrastructure.Identity;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace Akiba.Web;

/// <summary>
/// Refused because the signed-in official's role does not cover the action.
/// </summary>
/// <remarks>
/// An <see cref="InvalidOperationException"/> on purpose. Every screen in Akiba already catches
/// that and shows its message inline, in the same place a domain refusal appears - "you reviewed
/// this run, so you cannot also approve it". A refusal on role belongs in exactly that place.
/// As a plain <see cref="UnauthorizedAccessException"/> it was caught by nothing, reached the
/// error boundary, and replaced the whole screen with "an unexpected error occurred" - for three
/// of the four officials, on every action button they could see.
/// </remarks>
public sealed class NotPermittedException(string message) : InvalidOperationException(message);

/// <summary>
/// Enforces a role policy for every MediatR request named *Command. UI authorization is not a
/// security boundary: interactive component events and future callers must pass this check too.
/// Unknown commands default to the clerk policy; privileged exceptions are explicit below.
/// </summary>
internal sealed class CommandAuthorizationBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly AuthenticationStateProvider _authenticationState;
    private readonly IAuthorizationService _authorization;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IHostEnvironment _environment;

    public CommandAuthorizationBehaviour(
        AuthenticationStateProvider authenticationState,
        IAuthorizationService authorization,
        IHttpContextAccessor httpContextAccessor,
        IHostEnvironment environment)
    {
        _authenticationState = authenticationState;
        _authorization = authorization;
        _httpContextAccessor = httpContextAccessor;
        _environment = environment;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (!request.GetType().Name.EndsWith("Command", StringComparison.Ordinal))
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }

        var context = _httpContextAccessor.HttpContext;

        // The development data seeder is deliberately anonymous but only mapped outside
        // Production and restricted to loopback. It drives the same commands as the UI.
        if (!_environment.IsProduction()
            && context?.Request.Path == "/api/dev/seed-demo"
            && context.Connection.RemoteIpAddress is { } remote
            && IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }

        // This one command is also called by the trusted hosted outbox worker, which has no
        // request or Blazor authentication state. Interactive and HTTP callers still need the
        // clerk role, so this exception cannot be selected by a browser request.
        if (request is DispatchNotificationsCommand && context is null)
        {
            try
            {
                // Receiving any state means this is an interactive caller (even when the
                // principal is anonymous), so authorization below still decides it.
                await _authenticationState.GetAuthenticationStateAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // ServerAuthenticationStateProvider throws outside an active Blazor circuit.
                // The only non-HTTP caller of this request is the hosted dispatcher above.
                return await next(cancellationToken).ConfigureAwait(false);
            }
        }

        var principal = (await _authenticationState.GetAuthenticationStateAsync()
            .ConfigureAwait(false)).User;

        var policy = PolicyFor(request.GetType());
        var result = await _authorization.AuthorizeAsync(principal, resource: null, policy)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new NotPermittedException(
                $"This is done by {WhoMayDo(policy)}, and your account does not hold that role. "
                + "Nothing was changed.");
        }

        return await next(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The policy a command needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched on the command types themselves, not their names as strings. With strings, a
    /// renamed command quietly fell through to the clerk default - so renaming
    /// ApproveDividendRunCommand would have let the clerk approve dividends, and nothing would
    /// have said so. Now a rename is a compile error here.
    /// </para>
    /// <para>
    /// Each officer-specific entry follows the officer the domain names. Where a domain method
    /// takes <c>Actor treasurer</c> or <c>Actor chairman</c>, the command that calls it is
    /// gated on that office - see the role-by-command tests.
    /// </para>
    /// </remarks>
    internal static string PolicyFor(Type requestType) => requestType switch
    {
        _ when requestType == typeof(ClosePeriodCommand) => AkibaPolicies.ClosesPeriods,
        _ when requestType == typeof(ReviewDividendRunCommand) => AkibaPolicies.ClosesPeriods,

        // BankReconciliation.SignOff takes the treasurer. This was defaulting to the clerk,
        // which refused the treasurer and let whoever prepared a reconciliation sign off
        // their own work.
        _ when requestType == typeof(SignOffReconciliationCommand) => AkibaPolicies.ClosesPeriods,

        _ when requestType == typeof(ReopenPeriodCommand) => AkibaPolicies.ApprovesDividends,
        _ when requestType == typeof(ApproveDividendRunCommand) => AkibaPolicies.ApprovesDividends,

        _ when requestType == typeof(DryRunOpeningBalancesCommand) => AkibaPolicies.LoadsOpeningBalances,
        _ when requestType == typeof(CommitOpeningBalancesCommand) => AkibaPolicies.LoadsOpeningBalances,

        // Loan decisions currently have no authoritative link between user identities and the
        // zone/office representative register. Keep them clerk-entered until that model is agreed.
        _ => AkibaPolicies.RecordsMoney,
    };

    /// <summary>The office a policy belongs to, as an official would say it.</summary>
    private static string WhoMayDo(string policy) => policy switch
    {
        AkibaPolicies.ClosesPeriods => "the treasurer",
        AkibaPolicies.ApprovesDividends => "the chairman",
        AkibaPolicies.LoadsOpeningBalances => "the treasurer or the chairman",
        _ => "the accounts clerk",
    };
}
