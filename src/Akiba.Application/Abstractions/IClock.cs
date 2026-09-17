namespace Akiba.Application.Abstractions;

/// <summary>
/// The current time, as a dependency.
/// </summary>
/// <remarks>
/// Every journal entry records when it was posted, and every period close records when it
/// closed. Reading the clock directly would make those untestable, and a financial system
/// whose date handling cannot be tested is a financial system whose date handling is wrong.
/// </remarks>
public interface IClock
{
    /// <summary>The current instant, in UTC. Stored as timestamptz.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Today's date in Africa/Nairobi.
    /// </summary>
    /// <remarks>
    /// Entry dates are calendar dates in Nairobi, not UTC dates. Late in the evening the two
    /// differ, and an entry posted at 02:00 Nairobi belongs to that day, not the previous one.
    /// </remarks>
    DateOnly TodayInNairobi { get; }
}
