using System.Security.Cryptography;
using Akiba.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Akiba.Infrastructure.Identity;

/// <summary>
/// Creates the five roles, and the break-glass account if there is nobody at all.
/// </summary>
public static class IdentityStartup
{
    /// <summary>
    /// The account created when Akiba starts against an empty user table.
    /// </summary>
    /// <remarks>
    /// It exists so somebody can get in at all on a fresh install. It is <b>not</b> a
    /// break-glass account in the sense the README describes - those credentials are held by
    /// the treasurer and the chairman and are set up during deployment. This one must be used
    /// once, to create the real accounts, and then deactivated.
    /// </remarks>
    public const string SetupUserName = "setup";

    private static readonly (string Role, string Description)[] Roles =
    [
        (AkibaRoles.AccountsClerk, "The only role that creates or edits records"),
        (AkibaRoles.Treasurer, "Views all; verifies balances, closes periods, reviews dividend runs"),
        (AkibaRoles.Chairman, "Views all; approves dividend runs, reopens periods, approves write-offs"),
        (AkibaRoles.Secretary, "Views all"),
        (AkibaRoles.Hr, "Downloads the monthly deduction schedules, and nothing else"),
    ];

    /// <summary>Creates any role that does not exist. Safe to run on every startup.</summary>
    public static async Task EnsureRolesAsync(
        RoleManager<AkibaRole> roles,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var (role, description) in Roles)
        {
            if (await roles.RoleExistsAsync(role).ConfigureAwait(false))
            {
                continue;
            }

            var result = await roles.CreateAsync(new AkibaRole(role) { Description = description })
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not create the {role} role: {Describe(result)}");
            }

            logger.LogInformation("Created role {Role}", role);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Creates a one-time setup account if there are no users at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The password is not in the source and is not defaulted. It comes from
    /// <c>AKIBA_SETUP_PASSWORD</c>, and where that is not set Akiba generates one, writes it to
    /// the startup log once, and never shows it again. A known default password on a system
    /// holding member financial records is not a convenience, it is a way in.
    /// </para>
    /// <para>
    /// The account is created with TOTP not yet enrolled, so whoever uses it must set up an
    /// authenticator before they can do anything.
    /// </para>
    /// </remarks>
    public static async Task<string?> EnsureSetupUserAsync(
        UserManager<AkibaUser> users,
        ILogger logger,
        string? configuredPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(logger);

        if (users.Users.Any())
        {
            return null;
        }

        var password = string.IsNullOrWhiteSpace(configuredPassword)
            ? GeneratePassword()
            : configuredPassword;

        var user = new AkibaUser
        {
            UserName = SetupUserName,
            Email = null,
            DisplayName = "Setup account",
            EmailConfirmed = true,
        };

        var created = await users.CreateAsync(user, password).ConfigureAwait(false);

        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not create the setup account: {Describe(created)}");
        }

        // It needs the clerk's rights to create the real accounts, and the chairman's to be
        // able to unpick anything it gets wrong before the real officials exist.
        await users.AddToRolesAsync(user, [AkibaRoles.AccountsClerk, AkibaRoles.Chairman])
            .ConfigureAwait(false);

        logger.LogWarning(
            "No users existed, so a one-time setup account was created. " +
            "Sign in as '{UserName}', enrol an authenticator, create the real accounts for the " +
            "clerk, treasurer, chairman and secretary, then DEACTIVATE this one.",
            SetupUserName);

        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            logger.LogWarning(
                "Setup password (shown once, not stored anywhere else): {Password}", password);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return password;
    }

    /// <summary>
    /// A password nobody chose and nobody can guess.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Long enough that it does not matter that it goes through a log file once, and built from
    /// a cryptographic source rather than <see cref="Random"/>.
    /// </para>
    /// <para>
    /// <see cref="RandomNumberGenerator.GetString"/> rather than a byte modulo the alphabet
    /// length: 256 does not divide by 57, so taking the remainder makes the first few letters
    /// of the alphabet slightly likelier than the rest. The effect here is small, but a biased
    /// password generator in a financial system is not a thing to leave written down for the
    /// next person to copy.
    /// </para>
    /// </remarks>
    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        const string symbols = "!@#$%^&*-_=+";

        var password = RandomNumberGenerator.GetString(alphabet, 24);

        // Identity's policy wants a digit, an upper, a lower and a symbol. The alphabet above
        // covers the first three by weight of probability; these guarantee all four.
        return password
            + RandomNumberGenerator.GetString(symbols, 1)
            + RandomNumberGenerator.GetString("23456789", 1);
    }

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(error => error.Description));
}

/// <summary>Applies migrations, seeds the chart of accounts, and sets up identity.</summary>
public static class AkibaStartup
{
    public static async Task PrepareAsync(
        AkibaDbContext context,
        ChartOfAccountsSeeder seeder,
        RoleManager<AkibaRole> roles,
        UserManager<AkibaUser> users,
        ILogger logger,
        string? setupPassword,
        CancellationToken cancellationToken = default)
    {
        await DatabaseStartup.MigrateAndSeedAsync(context, seeder, logger, cancellationToken)
            .ConfigureAwait(false);

        await EnsureRolesAsync(roles, logger, cancellationToken).ConfigureAwait(false);

        await IdentityStartup.EnsureSetupUserAsync(users, logger, setupPassword, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task EnsureRolesAsync(
        RoleManager<AkibaRole> roles, ILogger logger, CancellationToken cancellationToken) =>
        IdentityStartup.EnsureRolesAsync(roles, logger, cancellationToken);
}
