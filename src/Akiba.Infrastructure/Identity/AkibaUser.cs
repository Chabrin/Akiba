using Microsoft.AspNetCore.Identity;

namespace Akiba.Infrastructure.Identity;

/// <summary>
/// An official who signs in to Akiba.
/// </summary>
/// <remarks>
/// <para>
/// There are four of these, plus HR. Not members - members have no login and there is no
/// member portal. A <see cref="AkibaUser"/> is somebody who runs the society, and their
/// <see cref="DisplayName"/> is what appears against every entry they post.
/// </para>
/// <para>
/// The display name is stored here and copied onto each journal entry rather than being looked
/// up when an entry is read. A ledger entry is permanent and must stay legible after the
/// account that made it has been renamed or disabled; a reference that resolves to nothing is
/// not an audit trail.
/// </para>
/// </remarks>
public sealed class AkibaUser : IdentityUser<Guid>
{
    /// <summary>The name that appears against every entry this person posts.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// False once somebody has left the society. Their entries stay in the ledger forever.
    /// </summary>
    /// <remarks>
    /// Officials are deactivated, never deleted. Deleting one would orphan every entry they
    /// posted, which is the opposite of what a permanent record is for.
    /// </remarks>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? DeactivatedAtUtc { get; set; }

    /// <summary>
    /// Whether this account still needs to set up its authenticator.
    /// </summary>
    /// <remarks>
    /// TOTP is mandatory for every account. A new official can sign in once to enrol, and can
    /// do nothing else until they have.
    /// </remarks>
    public bool MustEnrolTwoFactor => !TwoFactorEnabled;
}

/// <summary>An Akiba role. Guid keys, to match the users.</summary>
public sealed class AkibaRole : IdentityRole<Guid>
{
    public AkibaRole()
    {
    }

    public AkibaRole(string roleName)
        : base(roleName)
    {
    }

    /// <summary>What this role is for, shown on the users screen.</summary>
    public string Description { get; set; } = string.Empty;
}
