using Akiba.Application.Notifications;
using Akiba.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Akiba.Infrastructure.Persistence.Repositories;

/// <summary>Stores and retrieves queued messages.</summary>
internal sealed class NotificationOutbox : INotificationOutbox
{
    private readonly AkibaDbContext _context;

    public NotificationOutbox(AkibaDbContext context) => _context = context;

    public async Task<Notification?> FindByIdAsync(
        NotificationId id, CancellationToken cancellationToken = default)
    {
        var row = await _context.Notifications
            .AsNoTracking()
            .FirstOrDefaultAsync(notification => notification.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : NotificationMapper.ToDomain(row);
    }

    public async Task<IReadOnlyList<Notification>> WaitingAsync(
        int take, CancellationToken cancellationToken = default)
    {
        var rows = await _context.Notifications
            .AsNoTracking()
            .Where(notification =>
                notification.Status == (int)NotificationStatus.Pending
                || notification.Status == (int)NotificationStatus.Failed)
            .OrderBy(notification => notification.QueuedAtUtc)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(NotificationMapper.ToDomain)];
    }

    public async Task<IReadOnlyList<Notification>> RecentAsync(
        int take, NotificationStatus? status = null, CancellationToken cancellationToken = default)
    {
        var rows = _context.Notifications.AsNoTracking();

        if (status is { } wanted)
        {
            rows = rows.Where(notification => notification.Status == (int)wanted);
        }

        var page = await rows
            .OrderByDescending(notification => notification.QueuedAtUtc)
            .Take(Math.Clamp(take, 1, 2_000))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. page.Select(NotificationMapper.ToDomain)];
    }

    public void Add(Notification notification) =>
        _context.Notifications.Add(NotificationMapper.ToRow(notification));

    public void Update(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var existing =
            _context.Notifications.Local.FirstOrDefault(row => row.Id == notification.Id.Value)
            ?? _context.Notifications.First(row => row.Id == notification.Id.Value);

        _context.Entry(existing).CurrentValues.SetValues(NotificationMapper.ToRow(notification));
    }
}
