using Akiba.Application.Notifications;
using Akiba.Domain.Notifications;

namespace Akiba.Infrastructure.Notifications;

/// <summary>
/// Which messages the society sends, and whether this machine may send anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing sends outside Production.</b> That is not a setting the office can change from a
/// screen: it is decided by the deployment, because the one thing that must never happen is a
/// development database full of invented members being texted real messages - or worse, a
/// development database restored from a backup of the real one.
/// </para>
/// <para>
/// Which <i>kinds</i> are sent is the office's decision. Every kind defaults on except SMS for
/// the two long ones, because a statement does not fit in a text message and sending it as
/// three costs three times as much.
/// </para>
/// </remarks>
public sealed class NotificationPolicy : INotificationPolicy
{
    private readonly HashSet<string> _disabled;

    /// <param name="sendingIsAllowed">True only in Production.</param>
    /// <param name="suppressionReason">Why not, when not.</param>
    /// <param name="disabled">
    /// Kinds the office has turned off, as <c>Kind</c> or <c>Kind:Channel</c> - so
    /// <c>ArrearsReminder</c> turns it off everywhere and <c>ArrearsReminder:Sms</c> leaves the
    /// email in place.
    /// </param>
    public NotificationPolicy(
        bool sendingIsAllowed, string suppressionReason, IEnumerable<string>? disabled = null)
    {
        SendingIsAllowed = sendingIsAllowed;
        SuppressionReason = suppressionReason;

        _disabled = new HashSet<string>(
            disabled ?? DefaultDisabled, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What is off unless the office says otherwise.
    /// </summary>
    /// <remarks>
    /// A statement and a shortfall notice both run to several lines. As an SMS each would be
    /// split into three chargeable messages and arrive unreadable, so they go by email only
    /// until somebody decides otherwise.
    /// </remarks>
    public static IReadOnlyList<string> DefaultDisabled { get; } =
    [
        $"{NotificationKind.MonthlyStatement}:{NotificationChannel.Sms}",
        $"{NotificationKind.ShortfallNotice}:{NotificationChannel.Sms}",
    ];

    public bool SendingIsAllowed { get; }

    public string SuppressionReason { get; }

    public bool IsEnabled(NotificationKind kind, NotificationChannel channel) =>
        !_disabled.Contains(kind.ToString())
        && !_disabled.Contains($"{kind}:{channel}");
}
