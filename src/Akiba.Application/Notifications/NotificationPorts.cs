using Akiba.Domain.Notifications;

namespace Akiba.Application.Notifications;

/// <summary>Reads and writes the outbox.</summary>
public interface INotificationOutbox
{
    Task<Notification?> FindByIdAsync(
        NotificationId id, CancellationToken cancellationToken = default);

    /// <summary>Messages the dispatcher should try, oldest first.</summary>
    Task<IReadOnlyList<Notification>> WaitingAsync(
        int take, CancellationToken cancellationToken = default);

    /// <summary>The outbox for the screen, newest first.</summary>
    Task<IReadOnlyList<Notification>> RecentAsync(
        int take, NotificationStatus? status = null, CancellationToken cancellationToken = default);

    void Add(Notification notification);

    void Update(Notification notification);
}

/// <summary>
/// Sends a message on one channel.
/// </summary>
/// <remarks>
/// Behind an interface so that SMS can be swapped or turned off without touching anything that
/// decides a message should be sent - the brief asks for that, and it is also what lets every
/// environment except Production substitute a sender that writes the message down and posts
/// nothing.
/// </remarks>
public interface INotificationSender
{
    NotificationChannel Channel { get; }

    /// <summary>
    /// Attempts delivery.
    /// </summary>
    /// <returns>
    /// Nothing on success. The reason on failure - which is returned rather than thrown,
    /// because a mailbox being full is an ordinary outcome for an outbox, not an exception.
    /// </returns>
    Task<string?> SendAsync(Notification notification, CancellationToken cancellationToken = default);
}

/// <summary>
/// Which messages the society has chosen to send, and whether anything sends at all.
/// </summary>
/// <remarks>
/// Two separate questions, deliberately. "Is this environment allowed to send?" is a property
/// of the deployment and is not negotiable - <b>nothing sends outside Production</b>. "Does the
/// office want arrears reminders?" is a decision the committee makes and changes.
/// </remarks>
public interface INotificationPolicy
{
    /// <summary>
    /// Whether anything at all may leave this machine.
    /// </summary>
    /// <remarks>
    /// False everywhere but Production. A development database full of invented members with
    /// plausible-looking phone numbers is exactly the thing that must never be texted.
    /// </remarks>
    bool SendingIsAllowed { get; }

    /// <summary>Why not, when it is not. Shown against every suppressed message.</summary>
    string SuppressionReason { get; }

    /// <summary>Whether the office wants this kind of message on this channel.</summary>
    bool IsEnabled(NotificationKind kind, NotificationChannel channel);

    /// <summary>
    /// Whether a member's position is forbidden from leaving the building.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On by default.</b> With it on, every message is written for the counter: it is
    /// queued, it appears on the messages screen, and an official gives it to the member in
    /// person. No mail server and no SMS gateway is registered at all, so there is no path out
    /// to disable - the absence is the control, not a setting that could be flipped.
    /// </para>
    /// <para>
    /// Turning it off is a committee decision, not an administrator's. BUILD_BRIEF section 9
    /// names email as the confirmed channel for member statements, and both positions are
    /// defensible: email reaches a member who has moved away, the counter means a member's
    /// figures never cross a network the society does not own. They are opposites, so one is
    /// the default and the other is explicit.
    /// </para>
    /// </remarks>
    bool InternalOnly { get; }
}
