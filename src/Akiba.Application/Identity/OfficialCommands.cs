using Akiba.Application.Abstractions;
using MediatR;

namespace Akiba.Application.Identity;

/// <summary>Returned after a new official is created, carrying the one-time password.</summary>
/// <param name="UserId">The new account's identity primary key.</param>
/// <param name="TemporaryPassword">
/// Shown once to the chairman; the new official sets a permanent password on first sign-in.
/// It is not stored anywhere after this handler returns.
/// </param>
public sealed record CreateOfficialResult(Guid UserId, string TemporaryPassword);

/// <summary>
/// Creates a new official account with the given roles and a generated one-time password.
/// </summary>
/// <param name="Username">The sign-in username — short, unique, no spaces.</param>
/// <param name="DisplayName">
/// The name printed against every ledger entry. Should be what colleagues call them.
/// </param>
/// <param name="Roles">One or more role names (e.g. AccountsClerk, Treasurer, Chairman, Secretary, HR).</param>
public sealed record CreateOfficialCommand(
    string Username,
    string DisplayName,
    IReadOnlyList<string> Roles) : IRequest<CreateOfficialResult>;

internal sealed class CreateOfficialHandler : IRequestHandler<CreateOfficialCommand, CreateOfficialResult>
{
    private readonly IOfficialRepository _officials;

    public CreateOfficialHandler(IOfficialRepository officials)
    {
        _officials = officials;
    }

    public async Task<CreateOfficialResult> Handle(
        CreateOfficialCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Username))
        {
            throw new ArgumentException("Username is required.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.DisplayName))
        {
            throw new ArgumentException("Display name is required.", nameof(command));
        }

        if (command.Roles.Count == 0)
        {
            throw new ArgumentException("At least one role must be assigned.", nameof(command));
        }

        var (userId, password) = await _officials
            .CreateAsync(command.Username.Trim(), command.DisplayName.Trim(), command.Roles, cancellationToken)
            .ConfigureAwait(false);

        return new CreateOfficialResult(userId, password);
    }
}

/// <summary>Returned after a password reset, carrying the new one-time password.</summary>
/// <param name="TemporaryPassword">
/// Read aloud by the chairman; the official must set a permanent password on next sign-in.
/// The TOTP authenticator is also cleared so they must re-enrol.
/// </param>
public sealed record ResetOfficialPasswordResult(string TemporaryPassword);

/// <summary>
/// Resets an official's password and clears their TOTP key, requiring re-enrolment on next
/// sign-in. Use when an official loses access to their authenticator device.
/// </summary>
public sealed record ResetOfficialPasswordCommand(Guid UserId) : IRequest<ResetOfficialPasswordResult>;

internal sealed class ResetOfficialPasswordHandler
    : IRequestHandler<ResetOfficialPasswordCommand, ResetOfficialPasswordResult>
{
    private readonly IOfficialRepository _officials;

    public ResetOfficialPasswordHandler(IOfficialRepository officials)
    {
        _officials = officials;
    }

    public async Task<ResetOfficialPasswordResult> Handle(
        ResetOfficialPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var password = await _officials
            .ResetPasswordAsync(command.UserId, cancellationToken)
            .ConfigureAwait(false);

        return new ResetOfficialPasswordResult(password);
    }
}

/// <summary>
/// Replaces the roles assigned to an official. Passing an empty list removes all roles, which
/// is allowed only as preparation for deactivation — an account with no roles can sign in but
/// cannot perform any action.
/// </summary>
public sealed record SetOfficialRolesCommand(
    Guid UserId,
    IReadOnlyList<string> Roles) : IRequest;

internal sealed class SetOfficialRolesHandler : IRequestHandler<SetOfficialRolesCommand>
{
    private readonly IOfficialRepository _officials;

    public SetOfficialRolesHandler(IOfficialRepository officials)
    {
        _officials = officials;
    }

    public Task Handle(SetOfficialRolesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _officials.SetRolesAsync(command.UserId, command.Roles, cancellationToken);
    }
}

/// <summary>
/// Deactivates an official's account. Any open session fails at the next request. Their
/// ledger entries are permanent and stay attributed to their display name.
/// </summary>
public sealed record DeactivateOfficialCommand(Guid UserId) : IRequest;

internal sealed class DeactivateOfficialHandler : IRequestHandler<DeactivateOfficialCommand>
{
    private readonly IOfficialRepository _officials;

    public DeactivateOfficialHandler(IOfficialRepository officials)
    {
        _officials = officials;
    }

    public Task Handle(DeactivateOfficialCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _officials.SetActiveAsync(command.UserId, false, cancellationToken);
    }
}

/// <summary>Reactivates a previously deactivated account.</summary>
public sealed record ReactivateOfficialCommand(Guid UserId) : IRequest;

internal sealed class ReactivateOfficialHandler : IRequestHandler<ReactivateOfficialCommand>
{
    private readonly IOfficialRepository _officials;

    public ReactivateOfficialHandler(IOfficialRepository officials)
    {
        _officials = officials;
    }

    public Task Handle(ReactivateOfficialCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _officials.SetActiveAsync(command.UserId, true, cancellationToken);
    }
}
