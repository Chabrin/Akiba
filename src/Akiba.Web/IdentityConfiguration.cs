using Akiba.Application.Abstractions;
using Akiba.Infrastructure.Identity;
using Akiba.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace Akiba.Web;

/// <summary>
/// Registers ASP.NET Core Identity with the society's rules.
/// </summary>
/// <remarks>
/// <para>
/// This lives in Akiba.Web rather than in Akiba.Infrastructure because it is HTTP: cookies,
/// sign-in paths, an <see cref="IHttpContextAccessor"/>. Akiba.Infrastructure owns the user
/// and role entities and their EF Core stores, which is the part that has nothing to do with
/// the web. Putting the cookie configuration there would have meant pulling the whole ASP.NET
/// Core framework into a class library that otherwise only talks to PostgreSQL.
/// </para>
/// <para>
/// <b>TOTP is mandatory for every account.</b> Sign-in requires the second factor, and an
/// account that has not enrolled one can do nothing but enrol it.
/// </para>
/// </remarks>
public static class IdentityConfiguration
{
    /// <param name="services">The container.</param>
    /// <param name="developmentFallbackActor">
    /// Used when nobody is signed in. <b>Must be null in Production.</b> It exists so the
    /// development seeder, which runs with no request behind it, can post entries.
    /// </param>
    /// <param name="requireHttps">
    /// Whether the session cookie may only travel over HTTPS. True unless an administrator has
    /// deliberately turned it off - see <c>Akiba:RequireHttps</c> in docs/deployment.md.
    /// </param>
    public static IServiceCollection AddAkibaIdentity(
        this IServiceCollection services,
        Akiba.Domain.Common.Actor? developmentFallbackActor = null,
        bool requireHttps = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        // PBKDF2-HMAC-SHA256, which is what Identity has used since v3. The iteration count is
        // raised well above the framework default because the whole cost is paid five times a
        // day by five people signing in, and the whole benefit is paid to an attacker who has
        // walked off with the database. OWASP's current floor for this algorithm is 600,000.
        services.Configure<PasswordHasherOptions>(options =>
        {
            options.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
            options.IterationCount = 600_000;
        });

        services.AddIdentity<AkibaUser, AkibaRole>(options =>
            {
                // A financial system of record. Twelve characters is not onerous for five
                // people who sign in once a day.
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;

                // Five attempts, then fifteen minutes. Akiba has five users on a LAN, so nobody
                // is locked out by accident at that volume - and an attacker gets five guesses
                // a quarter of an hour.
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;

                options.User.RequireUniqueEmail = false;

                // There is no member portal and no self sign-up. An account is only ever created
                // by an official who already has one.
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddEntityFrameworkStores<AkibaDbContext>()
            .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/sign-in";
            options.LogoutPath = "/sign-out";
            options.AccessDeniedPath = "/not-permitted";

            // A back-office screen left open on a desk is a risk. Thirty minutes idle, and the
            // cookie does not outlive the browser session.
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = true;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Name = "akiba.session";

            // Over plain HTTP the session cookie travels in clear text across the office
            // network, and so does the password that produced it. Default is Always; an
            // administrator who genuinely cannot serve HTTPS turns it off knowingly, and
            // startup says so in the log every time.
            options.Cookie.SecurePolicy = requireHttps
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
        });

        // Policies are named for what somebody is doing, not for who they are, so a page stays
        // correct if the committee moves a duty between roles.
        //
        // EVERY policy below also requires an enrolled authenticator. That is what makes TOTP
        // mandatory rather than merely redirected-to: signing in with a password alone produces
        // an authenticated session that is permitted nothing except enrolling one. Without this
        // the redirect to the enrolment screen was only a suggestion, and a request that
        // ignored it could read every member's position.
        services.AddAuthorizationBuilder()
            .AddPolicy(AkibaPolicies.EnrollingTwoFactor, policy =>
                policy.RequireAuthenticatedUser())
            .AddPolicy(AkibaPolicies.RecordsMoney, policy =>
                policy.RequireTwoFactor().RequireRole(AkibaRoles.AccountsClerk))
            .AddPolicy(AkibaPolicies.ViewsLedger, policy =>
                policy.RequireTwoFactor().RequireRole([.. AkibaRoles.CanViewLedger]))
            .AddPolicy(AkibaPolicies.ClosesPeriods, policy =>
                policy.RequireTwoFactor().RequireRole(AkibaRoles.Treasurer))
            .AddPolicy(AkibaPolicies.ApprovesDividends, policy =>
                policy.RequireTwoFactor().RequireRole(AkibaRoles.Chairman))
            .AddPolicy(AkibaPolicies.DownloadsSchedules, policy =>
                policy.RequireTwoFactor()
                    .RequireRole(AkibaRoles.Hr, AkibaRoles.AccountsClerk, AkibaRoles.Treasurer))
            // Anything that forgets to name a policy still needs a signed-in, enrolled user.
            // A page added later is locked by default rather than open by default.
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireTwoFactor()
                .Build());

        // The antiforgery cookie gets the same treatment as the session cookie: it is half of
        // the pair that proves a form post came from Akiba's own page.
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "akiba.antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = requireHttps
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
        });

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser>(provider => new HttpContextCurrentUser(
            provider.GetRequiredService<IHttpContextAccessor>(), developmentFallbackActor));

        return services;
    }

}

internal static class AuthorizationPolicyBuilderExtensions
{
    /// <summary>
    /// Requires a signed-in user who has enrolled an authenticator.
    /// </summary>
    /// <remarks>
    /// Applied to every policy in Akiba except enrolment itself. The claim is set when the
    /// sign-in cookie is built, and refreshed the moment somebody completes enrolment.
    /// </remarks>
    public static Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder RequireTwoFactor(
        this Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .RequireAuthenticatedUser()
            .RequireClaim(AuthenticationEndpoints.TwoFactorEnrolledClaim, "true");
    }
}
