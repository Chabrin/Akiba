using Akiba.Application.Abstractions;
using Akiba.Domain.Financial;
using Akiba.Domain.Membership;
using Akiba.Domain.Notifications;
using MediatR;

namespace Akiba.Application.Notifications;

/// <summary>What a message needs to say, before it is addressed to anybody.</summary>
/// <param name="Subject">The email subject. Ignored for SMS.</param>
/// <param name="Body">The message.</param>
public sealed record NotificationText(string Subject, string Body);

/// <summary>
/// The words Akiba sends, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Every message the society sends a member is written here rather than at the point something
/// happens. A member who receives four messages should recognise all four as coming from the
/// same organisation, and an official asked "what does the arrears reminder actually say?"
/// should have one file to open.
/// </para>
/// <para>
/// The tone is deliberately plain and slightly formal. These go to colleagues about their own
/// money, and a reminder that reads as a threat is one the committee will ask to be turned off.
/// </para>
/// </remarks>
public static class NotificationComposer
{
    private const string Signature =
        "\n\nAkiba Welfare Society\nChabrin Agencies Limited\n" +
        "If a figure looks wrong, quote the date and the reference to the accounts clerk.";

    public static NotificationText LoanApproved(string memberName, string loanNumber, Money amount) =>
        new(
            $"Your loan application has been approved - {loanNumber}",
            $"Dear {memberName},\n\n" +
            $"The committee has approved your application for {amount}. " +
            "The money will be paid out in the next disbursement run and you will be told when " +
            "it has gone." + Signature);

    public static NotificationText LoanDisbursed(
        string memberName, string loanNumber, Money amount, Money instalment, DateOnly firstDue) =>
        new(
            $"Your loan has been paid out - {loanNumber}",
            $"Dear {memberName},\n\n" +
            $"{amount} has been paid out on loan {loanNumber}.\n\n" +
            $"Your instalment is {instalment} a month. The first one falls due on " +
            $"{firstDue:d MMMM yyyy} - there is a month's grace before repayment starts." +
            Signature);

    public static NotificationText MonthlyStatement(
        string memberName, DateOnly asAt, Money shareholding, Money loansOutstanding) =>
        new(
            $"Your Akiba statement as at {asAt:d MMMM yyyy}",
            $"Dear {memberName},\n\n" +
            $"As at {asAt:d MMMM yyyy}:\n\n" +
            $"  Shareholding       {shareholding}\n" +
            $"  Loans outstanding  {loansOutstanding}\n\n" +
            "Your full statement is attached to this message, or the accounts clerk can print " +
            "you one." + Signature);

    public static NotificationText ArrearsReminder(
        string memberName, string loanNumber, Money overdue, int daysLate) =>
        new(
            $"A repayment on {loanNumber} is outstanding",
            $"Dear {memberName},\n\n" +
            $"{overdue} on loan {loanNumber} is {daysLate} day(s) outstanding.\n\n" +
            "If this is a payroll deduction that did not come off, the office is already " +
            "looking into it and you need do nothing. If you pay directly, please contact the " +
            "accounts clerk." + Signature);

    public static NotificationText ShortfallNotice(
        string memberName, int year, int month, Money expected, Money deducted) =>
        new(
            $"Your {new DateOnly(year, month, 1):MMMM yyyy} deduction was short",
            $"Dear {memberName},\n\n" +
            $"Akiba asked for {expected} from your {new DateOnly(year, month, 1):MMMM} payslip " +
            $"and {deducted} came off.\n\n" +
            "The office is establishing why. If part of it was a loan instalment, that loan is " +
            "behind until the difference is made up." + Signature);

    public static NotificationText GuarantorArrearsAlert(
        string guarantorName, string borrowerName, string loanNumber, Money overdue) =>
        new(
            $"A loan you guarantee is in arrears - {loanNumber}",
            $"Dear {guarantorName},\n\n" +
            $"You are a guarantor on loan {loanNumber}, taken by {borrowerName}. " +
            $"{overdue} is outstanding on it.\n\n" +
            "Nothing is being asked of you yet. You are told because the society does not " +
            "approach a guarantor without warning, and because you may be able to help sooner " +
            "than the committee can." + Signature);

    public static NotificationText GuarantorExitAlert(
        string guarantorName, string borrowerName, string loanNumber, string departedName) =>
        new(
            $"A co-guarantor has left CAL - {loanNumber}",
            $"Dear {guarantorName},\n\n" +
            $"{departedName}, who guarantees loan {loanNumber} with you, has left CAL. " +
            $"{borrowerName} has been asked to find a replacement.\n\n" +
            "Your own guarantee is unchanged. You are told because the cover on that loan has " +
            "changed and you are entitled to know." + Signature);

    public static NotificationText DividendDeclared(
        string memberName, int year, Money amount) =>
        new(
            $"The {year} dividend has been declared",
            $"Dear {memberName},\n\n" +
            $"The Annual General Meeting has declared the dividend for {year}. " +
            $"Your share is {amount}.\n\n" +
            "The treasurer will confirm whether it is added to your shareholding or paid out." +
            Signature);
}

/// <summary>
/// Puts a message in the outbox.
/// </summary>
/// <param name="Kind">What it is about.</param>
/// <param name="BorrowerId">Who it is for.</param>
/// <param name="Text">What it says.</param>
/// <param name="Channels">
/// Which channels to try. Each becomes its own outbox row, so one can succeed while the other
/// fails - which is what happens when a member has an email address and a dead phone.
/// </param>
/// <remarks>
/// Queuing is not sending. Nothing leaves the machine here; the dispatcher picks it up.
/// </remarks>
public sealed record QueueNotificationCommand(
    NotificationKind Kind,
    BorrowerId BorrowerId,
    NotificationText Text,
    IReadOnlyList<NotificationChannel> Channels) : IRequest<int>;

internal sealed class QueueNotificationHandler : IRequestHandler<QueueNotificationCommand, int>
{
    private readonly INotificationOutbox _outbox;
    private readonly IBorrowerRepository _borrowers;
    private readonly INotificationPolicy _policy;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public QueueNotificationHandler(
        INotificationOutbox outbox,
        IBorrowerRepository borrowers,
        INotificationPolicy policy,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _outbox = outbox;
        _borrowers = borrowers;
        _policy = policy;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task<int> Handle(
        QueueNotificationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var borrower = await _borrowers.FindByIdAsync(command.BorrowerId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No borrower with id {command.BorrowerId}.");

        var queued = 0;

        foreach (var channel in command.Channels.Distinct())
        {
            if (!_policy.IsEnabled(command.Kind, channel))
            {
                continue;
            }

            var address = AddressFor(borrower, channel);

            if (string.IsNullOrWhiteSpace(address))
            {
                // No address is not an error. The register carries members with no email at
                // all, and a system that threw here would stop a loan being disbursed because
                // somebody never gave one.
                continue;
            }

            _outbox.Add(Notification.Queue(
                command.Kind,
                channel,
                borrower.Name.Full,
                address,
                command.Text.Subject,
                command.Text.Body,
                _clock.UtcNow,
                borrower.Id.Value));

            queued++;
        }

        if (queued > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return queued;
    }

    private static string? AddressFor(Borrower borrower, NotificationChannel channel) =>
        channel switch
        {
            NotificationChannel.Email => borrower.Email,
            NotificationChannel.Sms => borrower.Phone.IsSpecified ? borrower.Phone.Value : null,
            _ => null,
        };
}

/// <summary>
/// Tries everything waiting in the outbox.
/// </summary>
/// <param name="Take">How many to attempt in one pass.</param>
/// <remarks>
/// Called by the background dispatcher on a timer, and by a button on the notifications screen
/// so that an official who has just fixed a mail server setting does not have to wait.
/// </remarks>
public sealed record DispatchNotificationsCommand(int Take = 50) : IRequest<DispatchOutcome>;

/// <summary>What one pass of the dispatcher did.</summary>
/// <param name="Attempted">How many were tried.</param>
/// <param name="Sent">How many went.</param>
/// <param name="Failed">How many will be tried again.</param>
/// <param name="Suppressed">How many were deliberately not sent.</param>
public sealed record DispatchOutcome(int Attempted, int Sent, int Failed, int Suppressed);

internal sealed class DispatchNotificationsHandler
    : IRequestHandler<DispatchNotificationsCommand, DispatchOutcome>
{
    private readonly INotificationOutbox _outbox;
    private readonly IEnumerable<INotificationSender> _senders;
    private readonly INotificationPolicy _policy;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public DispatchNotificationsHandler(
        INotificationOutbox outbox,
        IEnumerable<INotificationSender> senders,
        INotificationPolicy policy,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _outbox = outbox;
        _senders = senders;
        _policy = policy;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task<DispatchOutcome> Handle(
        DispatchNotificationsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var waiting = await _outbox.WaitingAsync(command.Take, cancellationToken)
            .ConfigureAwait(false);

        var sent = 0;
        var failed = 0;
        var suppressed = 0;

        foreach (var notification in waiting)
        {
            // Checked here rather than only at the door, so that a message queued while the
            // office had a kind switched on is still suppressed if they switch it off before
            // the dispatcher gets to it.
            if (!_policy.SendingIsAllowed)
            {
                notification.Suppress(_policy.SuppressionReason, _clock.UtcNow);
                suppressed++;
            }
            else if (!_policy.IsEnabled(notification.Kind, notification.Channel))
            {
                notification.Suppress(
                    $"{notification.Kind} messages by {notification.Channel} are turned off.",
                    _clock.UtcNow);

                suppressed++;
            }
            else
            {
                var sender = _senders.FirstOrDefault(s => s.Channel == notification.Channel);

                if (sender is null)
                {
                    notification.Suppress(
                        $"Nothing is configured to send by {notification.Channel}.", _clock.UtcNow);

                    suppressed++;
                }
                else
                {
                    var problem = await sender.SendAsync(notification, cancellationToken)
                        .ConfigureAwait(false);

                    if (problem is null)
                    {
                        notification.MarkSent(_clock.UtcNow);
                        sent++;
                    }
                    else
                    {
                        notification.MarkFailed(problem, _clock.UtcNow);
                        failed++;
                    }
                }
            }

            _outbox.Update(notification);
        }

        if (waiting.Count > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new DispatchOutcome(waiting.Count, sent, failed, suppressed);
    }
}

/// <summary>The outbox, for the screen.</summary>
/// <param name="Take">How many to show.</param>
/// <param name="Status">One status, or all of them.</param>
public sealed record ListNotificationsQuery(int Take = 200, NotificationStatus? Status = null)
    : IRequest<IReadOnlyList<Notification>>;

internal sealed class ListNotificationsHandler
    : IRequestHandler<ListNotificationsQuery, IReadOnlyList<Notification>>
{
    private readonly INotificationOutbox _outbox;

    public ListNotificationsHandler(INotificationOutbox outbox) => _outbox = outbox;

    public Task<IReadOnlyList<Notification>> Handle(
        ListNotificationsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return _outbox.RecentAsync(query.Take, query.Status, cancellationToken);
    }
}
