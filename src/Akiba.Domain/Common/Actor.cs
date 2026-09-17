namespace Akiba.Domain.Common;

/// <summary>
/// Whoever performed an action: the accounts clerk who posted an entry, the treasurer who
/// closed a period, the chairman who approved a dividend run.
/// </summary>
/// <remarks>
/// The display name is stored alongside the id on purpose. Ledger entries are permanent and
/// have to stay readable years later, by which time the user account may have been renamed
/// or disabled - a reference that resolves to nothing is not an audit trail.
/// </remarks>
public readonly record struct Actor
{
    public Actor(Guid userId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("An actor must have a user id.", nameof(userId));
        }

        UserId = userId;
        DisplayName = displayName;
    }

    public Guid UserId { get; }

    public string DisplayName { get; }

    public bool IsSpecified => UserId != Guid.Empty;

    public override string ToString() => IsSpecified ? DisplayName : "(unknown actor)";
}
