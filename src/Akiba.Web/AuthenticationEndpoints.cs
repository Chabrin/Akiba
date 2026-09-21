using System.Security.Claims;
using Akiba.Infrastructure.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace Akiba.Web;

/// <summary>
/// Sign in, sign out, and enrol an authenticator.
/// </summary>
/// <remarks>
/// <para>
/// These are plain form posts rather than Blazor interactive components, because signing in
/// has to set a cookie and a Blazor circuit cannot - the response headers have already gone by
/// the time a component handles an event. This is the standard shape for authentication in a
/// Blazor Server application, not a shortcut.
/// </para>
/// <para>
/// <b>TOTP is mandatory.</b> A password alone never produces a signed-in session: it produces
/// either a two-factor challenge, or a redirect to enrolment for an account that has not set
/// one up.
/// </para>
/// </remarks>
public static class AuthenticationEndpoints
{
    /// <summary>The claim carrying the name that appears against every entry.</summary>
    public const string DisplayNameClaim = "akiba:display_name";

    /// <summary>
    /// Whether this account has enrolled an authenticator.
    /// </summary>
    /// <remarks>
    /// Carried as a claim so that authorisation can test it without a database read on every
    /// request. Every policy requires it, which is what makes TOTP actually mandatory rather
    /// than merely redirected-to: a password-only session is authenticated but permitted
    /// nothing except enrolling.
    /// </remarks>
    public const string TwoFactorEnrolledClaim = "akiba:2fa_enrolled";

    public static void MapAkibaAuthentication(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Anonymous, necessarily: these are how somebody who is not signed in signs in. The
        // fallback authorization policy would otherwise redirect the sign-in POST to the
        // sign-in page, which is a loop nobody escapes. /auth/authenticator overrides this
        // below, because enrolling does require a session.
        //
        // Rate limited by machine. Account lockout already stops five guesses at one account;
        // this is what stops one machine working through every account, and what stops somebody
        // locking all five officials out of their own system in a few seconds.
        //
        // The antiforgery middleware is turned off for the group and the check is made by hand
        // in each handler instead. These read the form themselves rather than model-binding it,
        // and the middleware only validates endpoints whose parameters it recognises as form
        // data - so relying on it here would mean relying on a check that silently does not run.
        var group = endpoints.MapGroup("/auth")
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(RateLimiting.Authentication);

        group.MapPost("/password", async (
            HttpContext context,
            SignInManager<AkibaUser> signInManager,
            UserManager<AkibaUser> userManager,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Akiba.SignIn");

            if (!await IsRequestGenuineAsync(context, antiforgery, logger))
            {
                return Results.Redirect("/sign-in?error=expired");
            }

            var form = await context.Request.ReadFormAsync();
            var userName = form["userName"].ToString().Trim();
            var password = form["password"].ToString();

            var user = await userManager.FindByNameAsync(userName);

            if (user is null || !user.IsActive)
            {
                // The same message either way. Telling an attacker which usernames exist is a
                // free gift, and there are only five of them.
                logger.LogWarning("Failed sign-in for {UserName} from {Ip}",
                    userName, context.Connection.RemoteIpAddress);

                return Results.Redirect("/sign-in?error=invalid");
            }

            var result = await signInManager.PasswordSignInAsync(
                user, password, isPersistent: false, lockoutOnFailure: true);

            if (result.RequiresTwoFactor)
            {
                return Results.Redirect("/sign-in/code");
            }

            if (result.IsLockedOut)
            {
                logger.LogWarning("Locked out: {UserName}", userName);
                return Results.Redirect("/sign-in?error=lockedout");
            }

            if (!result.Succeeded)
            {
                logger.LogWarning("Failed sign-in for {UserName} from {Ip}",
                    userName, context.Connection.RemoteIpAddress);

                return Results.Redirect("/sign-in?error=invalid");
            }

            // The password was right and there was no second factor, which means this account
            // has never enrolled one. It signs in, and can do nothing but enrol.
            if (!user.TwoFactorEnabled)
            {
                logger.LogInformation("{UserName} signed in and must enrol an authenticator", userName);
                return Results.Redirect("/security/authenticator?required=true");
            }

            return Results.Redirect("/");
        });

        group.MapPost("/code", async (
            HttpContext context,
            SignInManager<AkibaUser> signInManager,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Akiba.SignIn");

            if (!await IsRequestGenuineAsync(context, antiforgery, logger))
            {
                return Results.Redirect("/sign-in?error=expired");
            }

            var form = await context.Request.ReadFormAsync();
            var code = form["code"].ToString().Replace(" ", string.Empty, StringComparison.Ordinal);

            var result = await signInManager.TwoFactorAuthenticatorSignInAsync(
                code, isPersistent: false, rememberClient: false);

            if (result.Succeeded)
            {
                return Results.Redirect("/");
            }

            logger.LogWarning(
                "Failed two-factor code from {Ip}", context.Connection.RemoteIpAddress);

            return Results.Redirect(result.IsLockedOut
                ? "/sign-in?error=lockedout"
                : "/sign-in/code?error=invalid");
        });

        group.MapPost("/sign-out", async (
            HttpContext context,
            SignInManager<AkibaUser> signInManager,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory) =>
        {
            // Forcing somebody to sign out is only an annoyance, but it is an annoyance any
            // other page on the network can cause, and the check costs nothing.
            if (!await IsRequestGenuineAsync(
                    context, antiforgery, loggerFactory.CreateLogger("Akiba.SignIn")))
            {
                return Results.Redirect("/");
            }

            await signInManager.SignOutAsync();

            return Results.Redirect("/sign-in?error=signedout");
        });

        group.MapPost("/authenticator", async (
            HttpContext context,
            UserManager<AkibaUser> userManager,
            SignInManager<AkibaUser> signInManager,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory) =>
        {
            if (!await IsRequestGenuineAsync(
                    context, antiforgery, loggerFactory.CreateLogger("Akiba.SignIn")))
            {
                return Results.Redirect("/security/authenticator?error=expired");
            }

            var form = await context.Request.ReadFormAsync();
            var code = form["code"].ToString().Replace(" ", string.Empty, StringComparison.Ordinal);

            var user = await userManager.GetUserAsync(context.User);

            if (user is null)
            {
                return Results.Redirect("/sign-in");
            }

            var verified = await userManager.VerifyTwoFactorTokenAsync(
                user, userManager.Options.Tokens.AuthenticatorTokenProvider, code);

            if (!verified)
            {
                return Results.Redirect("/security/authenticator?error=invalid");
            }

            await userManager.SetTwoFactorEnabledAsync(user, true);

            // Refresh the cookie so it reflects the enrolment immediately.
            await signInManager.RefreshSignInAsync(user);

            return Results.Redirect("/?enrolled=true");
        }).RequireAuthorization(AkibaPolicies.EnrollingTwoFactor);
    }

    /// <summary>
    /// Whether this post came from Akiba's own form rather than from somewhere else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without it, a page anywhere else can post to <c>/auth/password</c> and sign an official
    /// into an account the attacker controls - after which everything that official records
    /// that afternoon lands in the attacker's books rather than the society's. It is a quieter
    /// attack than most, and a system of record is exactly where it does damage.
    /// </para>
    /// <para>
    /// A failure is treated as an expired page rather than as an attack, because that is what
    /// it almost always is: a sign-in screen left open overnight. The official is sent back to
    /// a fresh one.
    /// </para>
    /// </remarks>
    private static async Task<bool> IsRequestGenuineAsync(
        HttpContext context, IAntiforgery antiforgery, ILogger logger)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);

            return true;
        }
        catch (AntiforgeryValidationException exception)
        {
            logger.LogWarning(
                exception,
                "Rejected a form post to {Path} from {Ip}: no valid antiforgery token.",
                context.Request.Path,
                context.Connection.RemoteIpAddress);

            return false;
        }
    }
}

/// <summary>
/// Puts the display name and the roles into the sign-in cookie.
/// </summary>
/// <remarks>
/// The display name travels as a claim so that posting an entry does not have to read the user
/// table, and so the name recorded on the entry is the one the person had when they posted it.
/// </remarks>
public sealed class AkibaClaimsPrincipalFactory
    : UserClaimsPrincipalFactory<AkibaUser, AkibaRole>
{
    public AkibaClaimsPrincipalFactory(
        UserManager<AkibaUser> userManager,
        RoleManager<AkibaRole> roleManager,
        Microsoft.Extensions.Options.IOptions<IdentityOptions> options)
        : base(userManager, roleManager, options)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AkibaUser user)
    {
        var identity = await base.GenerateClaimsAsync(user).ConfigureAwait(false);

        ArgumentNullException.ThrowIfNull(user);

        identity.AddClaim(new Claim(
            AuthenticationEndpoints.DisplayNameClaim,
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName ?? "Unknown" : user.DisplayName));

        identity.AddClaim(new Claim(
            AuthenticationEndpoints.TwoFactorEnrolledClaim,
            user.TwoFactorEnabled ? "true" : "false"));

        return identity;
    }
}
