using System.Runtime.CompilerServices;
using System.Security.Claims;
using Akiba.Application.Dividends;
using Akiba.Application.Ledger;
using Akiba.Application.Members;
using Akiba.Application.Migration;
using Akiba.Application.Receipting;
using Akiba.Application.Reconciliation;
using Akiba.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Akiba.Web.Tests;

/// <summary>
/// Who may do what, checked against the policies the application actually registers.
/// </summary>
/// <remarks>
/// <para>
/// Every write in Akiba goes through <see cref="CommandAuthorizationBehaviour{TRequest,TResponse}"/>,
/// so this table is the security boundary for the whole ledger - a screen hiding a button is
/// not. The policies come from the running host rather than being rebuilt here, so a change to
/// IdentityConfiguration is tested as shipped rather than as remembered.
/// </para>
/// <para>
/// Each officer-specific row follows the officer the domain itself names. Where a domain method
/// takes <c>Actor treasurer</c> or <c>Actor chairman</c>, the command that reaches it is closed
/// to everybody else - which is how signing off a reconciliation was found defaulting to the
/// clerk, letting whoever prepared a reconciliation approve their own work.
/// </para>
/// </remarks>
public sealed class CommandAuthorizationTests : IClassFixture<AkibaApplication>
{
    private static readonly string[] Everyone =
        [AkibaRoles.AccountsClerk, AkibaRoles.Treasurer, AkibaRoles.Chairman, AkibaRoles.Secretary, AkibaRoles.Hr];

    private readonly AkibaApplication _application;

    public CommandAuthorizationTests(AkibaApplication application) => _application = application;

    /// <summary>Each command, and the offices allowed to send it.</summary>
    private static readonly (Type Command, string[] Allowed)[] Rules =
    [
        // The clerk records money. Nobody else enters or changes a figure.
        (typeof(RecordPayrollReceiptCommand), [AkibaRoles.AccountsClerk]),
        (typeof(EnrolMemberCommand), [AkibaRoles.AccountsClerk]),

        // AccountingPeriod.Close(treasurer), DividendRun.Review(treasurer),
        // BankReconciliation.SignOff(treasurer).
        (typeof(ClosePeriodCommand), [AkibaRoles.Treasurer]),
        (typeof(ReviewDividendRunCommand), [AkibaRoles.Treasurer]),
        (typeof(SignOffReconciliationCommand), [AkibaRoles.Treasurer]),

        // AccountingPeriod.Reopen(chairman), DividendRun.Approve(chairman).
        (typeof(ReopenPeriodCommand), [AkibaRoles.Chairman]),
        (typeof(ApproveDividendRunCommand), [AkibaRoles.Chairman]),

        // Either officer, because on a fresh install the only account is the chairman's.
        (typeof(CommitOpeningBalancesCommand), [AkibaRoles.Treasurer, AkibaRoles.Chairman]),
        (typeof(DryRunOpeningBalancesCommand), [AkibaRoles.Treasurer, AkibaRoles.Chairman]),
    ];

    public static TheoryData<Type, string, bool> Matrix()
    {
        var data = new TheoryData<Type, string, bool>();

        foreach (var (command, allowed) in Rules)
        {
            foreach (var role in Everyone)
            {
                data.Add(command, role, allowed.Contains(role));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Each_command_is_open_to_its_office_and_closed_to_the_rest(
        Type command, string role, bool allowed)
    {
        var (reached, refusal) = await SendAsync(command, Official(role));

        if (allowed)
        {
            refusal.Should().BeNull(because: $"{role} is the office that {command.Name} belongs to");
            reached.Should().BeTrue();
        }
        else
        {
            refusal.Should().BeOfType<NotPermittedException>(
                because: $"{role} must not be able to send {command.Name}");
            reached.Should().BeFalse(because: "a refused command must never reach its handler");
        }
    }

    [Fact]
    public async Task The_right_role_without_an_enrolled_authenticator_is_refused()
    {
        // A password alone signs somebody in to enrol an authenticator and nothing more. That
        // has to hold for writes, not only for pages.
        var (reached, refusal) = await SendAsync(
            typeof(RecordPayrollReceiptCommand), Official(AkibaRoles.AccountsClerk, enrolled: false));

        refusal.Should().BeOfType<NotPermittedException>();
        reached.Should().BeFalse();
    }

    [Fact]
    public async Task Nobody_signed_in_is_refused()
    {
        var (reached, refusal) = await SendAsync(
            typeof(RecordPayrollReceiptCommand), new ClaimsPrincipal(new ClaimsIdentity()));

        refusal.Should().BeOfType<NotPermittedException>();
        reached.Should().BeFalse();
    }

    [Fact]
    public async Task A_refusal_names_the_office_and_says_nothing_changed()
    {
        // It is an InvalidOperationException so every screen shows it inline, where a domain
        // refusal appears, instead of the error boundary replacing the page with "an
        // unexpected error occurred" - which is what three of the four officials saw.
        var (_, refusal) = await SendAsync(typeof(SignOffReconciliationCommand), Official(AkibaRoles.AccountsClerk));

        refusal.Should().BeAssignableTo<InvalidOperationException>();
        refusal!.Message.Should().Contain("the treasurer").And.Contain("Nothing was changed");
    }

    [Fact]
    public async Task Queries_are_not_gated_by_command_policy()
    {
        // Reads are governed by the page and endpoint policies. This behaviour is about writes,
        // and gating reads here too would refuse the treasurer the screens they sign off from.
        var (reached, refusal) = await SendAsync(
            typeof(ListMembersQuery), new ClaimsPrincipal(new ClaimsIdentity()));

        refusal.Should().BeNull();
        reached.Should().BeTrue();
    }

    /// <summary>
    /// Runs one request through the behaviour as a given official.
    /// </summary>
    /// <remarks>
    /// The request is built without its constructor. The behaviour only ever looks at what kind
    /// of request it has been handed, never at its contents, so a blank one is enough - and it
    /// means the table above can name any command without knowing how to fill one in.
    /// </remarks>
    private async Task<(bool Reached, Exception? Refusal)> SendAsync(Type requestType, ClaimsPrincipal user)
    {
        var services = _application.Services;

        var behaviour = new CommandAuthorizationBehaviour<object, bool>(
            new FixedAuthenticationState(user),
            services.GetRequiredService<IAuthorizationService>(),
            new HttpContextAccessor(),
            services.GetRequiredService<IHostEnvironment>());

        var request = RuntimeHelpers.GetUninitializedObject(requestType);
        var reached = false;

        try
        {
            await behaviour.Handle(request, _ =>
            {
                reached = true;
                return Task.FromResult(true);
            }, CancellationToken.None);

            return (reached, null);
        }
        catch (InvalidOperationException refusal)
        {
            return (reached, refusal);
        }
    }

    private static ClaimsPrincipal Official(string role, bool enrolled = true)
    {
        List<Claim> claims = [new(ClaimTypes.Name, "official"), new(ClaimTypes.Role, role)];

        if (enrolled)
        {
            claims.Add(new Claim(AuthenticationEndpoints.TwoFactorEnrolledClaim, "true"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private sealed class FixedAuthenticationState(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }
}
