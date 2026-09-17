using Akiba.Application.Abstractions;

namespace Akiba.Infrastructure.Time;

/// <summary>
/// The real clock.
/// </summary>
/// <remarks>
/// Timestamps are stored UTC as timestamptz. Entry dates are calendar dates in Nairobi,
/// because an entry posted at 02:00 Nairobi belongs to that day and not the previous one -
/// which is what a UTC date would say.
/// </remarks>
internal sealed class SystemClock : IClock
{
    /// <summary>
    /// East Africa Time. Kenya does not observe daylight saving, so the offset is a constant
    /// +03:00 - but this resolves the zone properly rather than hard-coding it, because a
    /// hard-coded offset is the kind of shortcut that is right until it is not.
    /// </summary>
    private static readonly TimeZoneInfo Nairobi = ResolveNairobi();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateOnly TodayInNairobi =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Nairobi).DateTime);

    private static TimeZoneInfo ResolveNairobi()
    {
        // The IANA id works on Linux, where Akiba runs in production, and on modern Windows.
        // The Windows id is the fallback for an older development machine.
        foreach (var id in new[] { "Africa/Nairobi", "E. Africa Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Try the next id.
            }
            catch (InvalidTimeZoneException)
            {
                // Corrupt zone data; try the next id.
            }
        }

        throw new InvalidOperationException(
            "Could not resolve the Africa/Nairobi time zone. Akiba records entry dates as " +
            "Nairobi calendar dates and cannot fall back to UTC without dating entries wrongly.");
    }
}
