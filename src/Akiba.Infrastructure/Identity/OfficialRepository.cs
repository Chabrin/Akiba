using System.Security.Cryptography;
using Akiba.Application.Abstractions;
using Microsoft.AspNetCore.Identity;

namespace Akiba.Infrastructure.Identity;

/// <summary>
/// Manages the five official accounts using ASP.NET Core Identity. The Application layer
/// talks to <see cref="IOfficialRepository"/>; this class is its only implementation.
/// </summary>
internal sealed class OfficialRepository : IOfficialRepository
{
    private readonly UserManager<AkibaUser> _users;

    public OfficialRepository(UserManager<AkibaUser> users)
    {
        _users = users;
    }

    public async Task<IReadOnlyList<OfficialSummary>> ListAsync(CancellationToken ct = default)
    {
        var users = _users.Users.OrderBy(u => u.UserName).ToList();
        var result = new List<OfficialSummary>(users.Count);

        foreach (var user in users)
        {
            var roles = await _users.GetRolesAsync(user).ConfigureAwait(false);
            result.Add(ToSummary(user, roles));
        }

        return result;
    }

    public async Task<OfficialSummary?> FindByIdAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(userId.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        var roles = await _users.GetRolesAsync(user).ConfigureAwait(false);
        return ToSummary(user, roles);
    }

    public async Task<(Guid UserId, string TemporaryPassword)> CreateAsync(
        string username,
        string displayName,
        IReadOnlyList<string> roles,
        CancellationToken ct = default)
    {
        var password = GeneratePassword();

        var user = new AkibaUser
        {
            UserName = username,
            DisplayName = displayName,
            EmailConfirmed = true,
        };

        var created = await _users.CreateAsync(user, password).ConfigureAwait(false);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not create account for '{username}': {Describe(created)}");
        }

        if (roles.Count > 0)
        {
            var assigned = await _users.AddToRolesAsync(user, roles).ConfigureAwait(false);
            if (!assigned.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Account created but roles could not be assigned: {Describe(assigned)}");
            }
        }

        return (user.Id, password);
    }

    public async Task<string> ResetPasswordAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(userId.ToString()).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No official with id {userId}.");

        var password = GeneratePassword();

        // Disable TOTP and wipe the key — they must re-enrol on the next sign-in.
        await _users.SetTwoFactorEnabledAsync(user, false).ConfigureAwait(false);
        await _users.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false);

        var token = await _users.GeneratePasswordResetTokenAsync(user).ConfigureAwait(false);
        var reset = await _users.ResetPasswordAsync(user, token, password).ConfigureAwait(false);
        if (!reset.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not reset password: {Describe(reset)}");
        }

        return password;
    }

    public async Task SetRolesAsync(
        Guid userId, IReadOnlyList<string> roles, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(userId.ToString()).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No official with id {userId}.");

        var current = await _users.GetRolesAsync(user).ConfigureAwait(false);

        var toRemove = current.Except(roles).ToList();
        if (toRemove.Count > 0)
        {
            var removed = await _users.RemoveFromRolesAsync(user, toRemove).ConfigureAwait(false);
            if (!removed.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not remove roles: {Describe(removed)}");
            }
        }

        var toAdd = roles.Except(current).ToList();
        if (toAdd.Count > 0)
        {
            var added = await _users.AddToRolesAsync(user, toAdd).ConfigureAwait(false);
            if (!added.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not add roles: {Describe(added)}");
            }
        }
    }

    public async Task SetActiveAsync(Guid userId, bool active, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(userId.ToString()).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No official with id {userId}.");

        user.IsActive = active;
        user.DeactivatedAtUtc = active ? null : DateTimeOffset.UtcNow;

        // Deactivating locks the account immediately; any open session fails on the next request.
        // Reactivating clears the lockout end date so they can sign in again.
        await _users.SetLockoutEnabledAsync(user, !active).ConfigureAwait(false);
        await _users.SetLockoutEndDateAsync(
            user, active ? null : DateTimeOffset.MaxValue).ConfigureAwait(false);

        var updated = await _users.UpdateAsync(user).ConfigureAwait(false);
        if (!updated.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not update account: {Describe(updated)}");
        }
    }

    private static OfficialSummary ToSummary(AkibaUser user, IList<string> roles) =>
        new(
            user.Id,
            user.UserName ?? string.Empty,
            user.DisplayName,
            [.. roles.Order()],
            user.IsActive,
            user.TwoFactorEnabled,
            user.CreatedAtUtc,
            user.DeactivatedAtUtc);

    /// <summary>
    /// A 18-character random password from an unambiguous alphabet, with a symbol and digit
    /// appended to satisfy Identity's complexity rules. The same approach as
    /// <see cref="IdentityStartup"/>'s setup-account generator.
    /// </summary>
    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        const string symbols = "!@#$%^&*-_=+";

        return RandomNumberGenerator.GetString(alphabet, 16)
            + RandomNumberGenerator.GetString(symbols, 1)
            + RandomNumberGenerator.GetString("23456789", 1);
    }

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => e.Description));
}
