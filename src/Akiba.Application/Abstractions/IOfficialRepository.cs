namespace Akiba.Application.Abstractions;

/// <summary>One official as the management screen shows them.</summary>
/// <param name="UserId">Identity primary key.</param>
/// <param name="Username">The name they sign in with.</param>
/// <param name="DisplayName">The name recorded against every entry they post.</param>
/// <param name="Roles">Every role currently assigned, sorted alphabetically.</param>
/// <param name="IsActive">False once deactivated — entries they posted are permanent regardless.</param>
/// <param name="TwoFactorEnrolled">True once they have set up their authenticator.</param>
/// <param name="CreatedAtUtc">When the account was created.</param>
/// <param name="DeactivatedAtUtc">When the account was deactivated, or null if still active.</param>
public sealed record OfficialSummary(
    Guid UserId,
    string Username,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool IsActive,
    bool TwoFactorEnrolled,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DeactivatedAtUtc);

/// <summary>
/// Reads and writes Akiba user accounts. Lives in Application as a port, implemented in
/// Infrastructure using ASP.NET Core Identity — so Application has no Identity dependency.
/// </summary>
public interface IOfficialRepository
{
    /// <summary>Every account, ordered by username.</summary>
    Task<IReadOnlyList<OfficialSummary>> ListAsync(CancellationToken ct = default);

    /// <summary>One account by identity primary key, or null if not found.</summary>
    Task<OfficialSummary?> FindByIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new official account with the given roles and a generated one-time password.
    /// The caller reads the password aloud to the new official; it is not stored anywhere.
    /// </summary>
    Task<(Guid UserId, string TemporaryPassword)> CreateAsync(
        string username,
        string displayName,
        IReadOnlyList<string> roles,
        CancellationToken ct = default);

    /// <summary>
    /// Generates a new one-time password and clears the TOTP key, forcing re-enrolment on
    /// the next sign-in.
    /// </summary>
    Task<string> ResetPasswordAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Replaces the official's roles with exactly the supplied list.</summary>
    Task SetRolesAsync(Guid userId, IReadOnlyList<string> roles, CancellationToken ct = default);

    /// <summary>
    /// Activates or deactivates an account. Deactivating sets a lockout so any open session
    /// fails on the next request. Entries the official posted are permanent.
    /// </summary>
    Task SetActiveAsync(Guid userId, bool active, CancellationToken ct = default);
}
