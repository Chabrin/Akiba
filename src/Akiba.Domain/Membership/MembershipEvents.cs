using Akiba.Domain.Common;

namespace Akiba.Domain.Membership;

/// <summary>
/// Raised when a member is recorded as having left CAL.
/// </summary>
/// <remarks>
/// Handled in two places. Any loan the member holds is recovered from their final dues, with
/// a shortfall passing to the guarantors. Any loan the member <i>guarantees</i> is flagged,
/// because a guarantor must be a current CAL employee - the borrower is asked to find a
/// replacement, and the exiting member's own funds are held back until one is found.
/// </remarks>
public sealed record MemberExitedEmployment(
    BorrowerId MemberId,
    string MemberName,
    DateOnly ExitedOn,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
