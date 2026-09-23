namespace Akiba.Infrastructure.Identity;

/// <summary>
/// The five roles, exactly as the questionnaire describes them.
/// </summary>
/// <remarks>
/// <para>
/// The shape to notice: <b>only the accounts clerk creates or edits anything.</b> The
/// treasurer, chairman and secretary see everything and each has their own approval action,
/// but none of them enters a figure. That is the society's own answer - "only one user the
/// accounts clerk should be able to edit" - and it is what makes the audit trail meaningful,
/// because there is exactly one person who could have typed any given number.
/// </para>
/// <para>
/// HR is deliberately the narrowest role in the system. They download the deduction schedules
/// and can see nothing else - not a balance, not a loan, not a member's position.
/// </para>
/// </remarks>
public static class AkibaRoles
{
    /// <summary>The only role that creates or edits records.</summary>
    public const string AccountsClerk = "AccountsClerk";

    /// <summary>Views all; verifies balances, closes periods, reviews dividend runs.</summary>
    public const string Treasurer = "Treasurer";

    /// <summary>Views all; approves dividend runs, reopens periods, approves write-offs.</summary>
    public const string Chairman = "Chairman";

    /// <summary>Views all.</summary>
    public const string Secretary = "Secretary";

    /// <summary>Downloads the monthly deduction schedules, and nothing else.</summary>
    public const string Hr = "HR";

    public static IReadOnlyList<string> All { get; } =
        [AccountsClerk, Treasurer, Chairman, Secretary, Hr];

    /// <summary>Everyone who may see the society's figures. Every role except HR.</summary>
    public static IReadOnlyList<string> CanViewLedger { get; } =
        [AccountsClerk, Treasurer, Chairman, Secretary];
}

/// <summary>
/// The authorization policies pages are guarded with.
/// </summary>
/// <remarks>
/// Named for what somebody is doing rather than for who they are, so a page says
/// <c>[Authorize(Policy = AkibaPolicies.RecordsMoney)]</c> - which stays right if the
/// committee moves a duty between roles.
/// </remarks>
public static class AkibaPolicies
{
    /// <summary>Entering or changing anything. The accounts clerk alone.</summary>
    public const string RecordsMoney = "RecordsMoney";

    /// <summary>Seeing the society's figures. Everyone but HR.</summary>
    public const string ViewsLedger = "ViewsLedger";

    /// <summary>Closing a period, reviewing a dividend run. The treasurer.</summary>
    public const string ClosesPeriods = "ClosesPeriods";

    /// <summary>Reopening a period, approving a dividend run or a write-off. The chairman.</summary>
    public const string ApprovesDividends = "ApprovesDividends";

    /// <summary>Downloading the deduction schedules. HR, and the clerk who produces them.</summary>
    public const string DownloadsSchedules = "DownloadsSchedules";

    /// <summary>
    /// Loading the society's existing register. The treasurer or the chairman.
    /// </summary>
    /// <remarks>
    /// Its own policy rather than borrowing the treasurer's, for a practical reason. This runs
    /// once, on a system where the only account that exists is the one the installer created -
    /// and that account is the chairman's. Gating the migration on the treasurer alone means
    /// the society cannot load its own register until it has finished creating accounts it
    /// cannot yet create the members for.
    ///
    /// The accountability is not weakened by this. The form records the treasurer's name as
    /// the sign-off on the totals whoever is typing, because the claim that matters is that
    /// the figures match the paper register, not that a particular person pressed a button.
    /// </remarks>
    public const string LoadsOpeningBalances = "LoadsOpeningBalances";

    /// <summary>
    /// Setting up an authenticator.
    /// </summary>
    /// <remarks>
    /// The one policy that does not require a second factor, because it is how somebody gets
    /// one. Everything else in Akiba requires it, so a password-only session can reach this
    /// screen and nothing else.
    /// </remarks>
    public const string EnrollingTwoFactor = "EnrollingTwoFactor";
}
