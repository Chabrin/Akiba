using Akiba.Domain.Common;
using Akiba.Domain.Financial;

namespace Akiba.Domain.Dividends;

/// <summary>Identifies a dividend run.</summary>
public readonly record struct DividendRunId(Guid Value)
{
    public static DividendRunId New() => new(Guid.NewGuid());

    public bool IsSpecified => Value != Guid.Empty;

    public override string ToString() => Value.ToString();
}

/// <summary>
/// How far a dividend run has got.
/// </summary>
/// <remarks>
/// Four states and three people. The clerk computes, the treasurer reviews, the chairman
/// approves, and only then may it post. <b>A run is never posted automatically</b>, however
/// obviously correct its arithmetic is - the point of the sequence is that three officials
/// have looked at the figure before it becomes every member's entitlement.
/// </remarks>
public enum DividendRunStatus
{
    /// <summary>Computed. Nobody has looked at it.</summary>
    Draft = 1,

    /// <summary>The treasurer has reviewed it.</summary>
    Reviewed = 2,

    /// <summary>The chairman has approved it. It may now be posted.</summary>
    Approved = 3,

    /// <summary>Posted to the ledger. Members are owed.</summary>
    Posted = 4,

    /// <summary>Abandoned before posting, with a reason.</summary>
    Withdrawn = 5,
}

/// <summary>One member's entitlement on a dividend run.</summary>
/// <param name="MemberId">Whose.</param>
/// <param name="MembershipNumber">Their number.</param>
/// <param name="FullName">Their name.</param>
/// <param name="SharesAccountId">Their share account, for the settlement posting.</param>
/// <param name="BasisAmount">
/// The figure their share was worked out from: their closing shareholding, or their average
/// over the year, depending on the basis in force.
/// </param>
/// <param name="Amount">What they get.</param>
public sealed record DividendLine(
    Guid MemberId,
    string MembershipNumber,
    string FullName,
    Guid SharesAccountId,
    Money BasisAmount,
    Money Amount);

/// <summary>
/// A year's dividend: computed, reviewed, approved, and only then posted.
/// </summary>
/// <remarks>
/// <para>
/// The amount distributed is <b>interest earned net of bank charges</b>. The society's bank
/// account earns no interest but incurs charges, and the charges are deducted before the
/// dividend is worked out - which is why the income and expenditure account shows them
/// separately rather than folding them into a surplus.
/// </para>
/// <para>
/// Allocation goes through <see cref="Money.Allocate(IReadOnlyList{decimal})"/>, so the total
/// distributed equals the total available <i>exactly</i>. That is not tidiness: the posting
/// is a journal entry, and a journal entry whose lines do not sum to zero cannot be
/// constructed. A dividend that lost a cent in rounding would simply fail to post.
/// </para>
/// </remarks>
public sealed class DividendRun : AggregateRoot<DividendRunId>
{
    private readonly List<DividendLine> _lines;

    private DividendRun(
        DividendRunId id,
        int year,
        Money interestEarned,
        Money bankCharges,
        string basisName,
        string basisExplanation,
        IEnumerable<DividendLine> lines)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basisName);

        if (interestEarned.IsNegative)
        {
            throw new ArgumentException(
                "Interest earned cannot be negative. A year in which loans cost the society " +
                "money is not a year with a dividend, and it is not this class's business.",
                nameof(interestEarned));
        }

        if (bankCharges.IsNegative)
        {
            throw new ArgumentException(
                "Bank charges are a positive amount that is deducted.", nameof(bankCharges));
        }

        Year = year;
        InterestEarned = interestEarned.Round();
        BankCharges = bankCharges.Round();
        BasisName = basisName.Trim();
        BasisExplanation = basisExplanation?.Trim() ?? string.Empty;
        Status = DividendRunStatus.Draft;
        _lines = [.. lines];
    }

    public int Year { get; }

    /// <summary>Interest earned on loans over the year.</summary>
    public Money InterestEarned { get; }

    /// <summary>Bank charges over the year. Deducted before the dividend.</summary>
    public Money BankCharges { get; }

    /// <summary>Interest earned net of bank charges. What is shared out.</summary>
    public Money Distributable => (InterestEarned - BankCharges).Round();

    /// <summary>Which basis was used. Recorded, because it is not settled.</summary>
    public string BasisName { get; }

    /// <summary>What that basis means, in the words printed on the schedule.</summary>
    public string BasisExplanation { get; }

    public DividendRunStatus Status { get; private set; }

    public Actor? ComputedBy { get; private set; }

    public DateTimeOffset? ComputedAtUtc { get; private set; }

    public Actor? ReviewedBy { get; private set; }

    public DateTimeOffset? ReviewedAtUtc { get; private set; }

    public Actor? ApprovedBy { get; private set; }

    public DateTimeOffset? ApprovedAtUtc { get; private set; }

    public DateOnly? PostedOn { get; private set; }

    public DateTimeOffset? PostedAtUtc { get; private set; }

    /// <summary>Why the run was abandoned, where it was.</summary>
    public string? WithdrawnReason { get; private set; }

    public IReadOnlyList<DividendLine> Lines => _lines;

    /// <summary>The sum of what the members get. Equal to <see cref="Distributable"/>.</summary>
    public Money TotalAllocated => _lines.Sum(line => line.Amount, Distributable.Currency);

    /// <summary>The total shareholding the dividend was divided across.</summary>
    public Money TotalBasis => _lines.Sum(line => line.BasisAmount, Distributable.Currency);

    /// <summary>
    /// Shillings of dividend per shilling of shareholding, to six places.
    /// </summary>
    /// <remarks>
    /// Shown on the schedule because it is the figure a member checks their own line against.
    /// Never used to compute an entitlement - the entitlements come from Allocate, and
    /// multiplying by a rounded rate is exactly how a distribution stops summing to the total.
    /// </remarks>
    public decimal RatePerShilling => TotalBasis.IsZero
        ? 0m
        : Math.Round(Distributable.Amount / TotalBasis.Amount, 6, MidpointRounding.AwayFromZero);

    public bool MayBePosted => Status == DividendRunStatus.Approved;

    public bool IsFinished => Status is DividendRunStatus.Posted or DividendRunStatus.Withdrawn;

    /// <summary>
    /// Computes a run.
    /// </summary>
    /// <param name="year">The year being distributed.</param>
    /// <param name="interestEarned">Interest earned on loans over the year.</param>
    /// <param name="bankCharges">Bank charges over the year.</param>
    /// <param name="members">Every member's shareholding over the year, read from the ledger.</param>
    /// <param name="basis">How each member's share is worked out.</param>
    /// <param name="clerk">Who computed it.</param>
    /// <param name="computedAtUtc">When.</param>
    /// <exception cref="InvalidOperationException">
    /// There is nothing to distribute, or nothing to distribute it across.
    /// </exception>
    public static DividendRun Compute(
        int year,
        Money interestEarned,
        Money bankCharges,
        IReadOnlyList<MemberShareholdingOverYear> members,
        IDividendBasis basis,
        Actor clerk,
        DateTimeOffset computedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(basis);

        if (!clerk.IsSpecified)
        {
            throw new ArgumentException("A dividend run records who computed it.", nameof(clerk));
        }

        var distributable = (interestEarned - bankCharges).Round();

        if (!distributable.IsPositive)
        {
            throw new InvalidOperationException(
                $"There is nothing to distribute for {year}: interest earned of {interestEarned} " +
                $"less bank charges of {bankCharges} comes to {distributable}. A year with no " +
                "surplus has no dividend, and saying so is the right answer rather than an error " +
                "to work around.");
        }

        var weights = basis.Weigh(members);

        if (weights.Count != members.Count)
        {
            throw new InvalidOperationException(
                $"The {basis.Name} basis returned {weights.Count} weights for {members.Count} " +
                "members.");
        }

        if (weights.Sum() <= 0m)
        {
            throw new InvalidOperationException(
                $"No member has any shareholding on the {basis.Name} basis, so there is nothing " +
                "to divide the dividend across.");
        }

        // Allocate, not multiply. The shares sum back to the total exactly, which is what lets
        // the posting balance.
        var amounts = distributable.Allocate(weights);

        var lines = members
            .Select((member, index) => new DividendLine(
                member.MemberId,
                member.MembershipNumber,
                member.FullName,
                member.SharesAccountId,
                new Money(weights[index], distributable.Currency).Round(),
                amounts[index]))
            .ToList();

        var run = new DividendRun(
            DividendRunId.New(), year, interestEarned, bankCharges,
            basis.Name, basis.Explanation, lines)
        {
            ComputedBy = clerk,
            ComputedAtUtc = computedAtUtc,
        };

        return run;
    }

    /// <summary>Rebuilds a run from storage. For the persistence layer only.</summary>
    public static DividendRun Rehydrate(
        DividendRunId id,
        int year,
        Money interestEarned,
        Money bankCharges,
        string basisName,
        string basisExplanation,
        DividendRunStatus status,
        Actor? computedBy,
        DateTimeOffset? computedAtUtc,
        Actor? reviewedBy,
        DateTimeOffset? reviewedAtUtc,
        Actor? approvedBy,
        DateTimeOffset? approvedAtUtc,
        DateOnly? postedOn,
        DateTimeOffset? postedAtUtc,
        string? withdrawnReason,
        IEnumerable<DividendLine> lines) =>
        new(id, year, interestEarned, bankCharges, basisName, basisExplanation, lines)
        {
            Status = status,
            ComputedBy = computedBy,
            ComputedAtUtc = computedAtUtc,
            ReviewedBy = reviewedBy,
            ReviewedAtUtc = reviewedAtUtc,
            ApprovedBy = approvedBy,
            ApprovedAtUtc = approvedAtUtc,
            PostedOn = postedOn,
            PostedAtUtc = postedAtUtc,
            WithdrawnReason = withdrawnReason,
        };

    /// <summary>The treasurer has been through the figures.</summary>
    public void Review(Actor treasurer, DateTimeOffset reviewedAtUtc)
    {
        if (!treasurer.IsSpecified)
        {
            throw new ArgumentException("A review records who made it.", nameof(treasurer));
        }

        if (Status != DividendRunStatus.Draft)
        {
            throw new InvalidOperationException(
                $"A {Status} dividend run cannot be reviewed. Reviewing comes first.");
        }

        Status = DividendRunStatus.Reviewed;
        ReviewedBy = treasurer;
        ReviewedAtUtc = reviewedAtUtc;
    }

    /// <summary>
    /// The chairman approves it.
    /// </summary>
    /// <remarks>
    /// Only after the treasurer's review. Two people, in order, and both recorded: the whole
    /// value of the sequence is that it cannot be short-circuited by whoever happens to be at
    /// the keyboard.
    /// </remarks>
    public void Approve(Actor chairman, DateTimeOffset approvedAtUtc)
    {
        if (!chairman.IsSpecified)
        {
            throw new ArgumentException("An approval records who gave it.", nameof(chairman));
        }

        if (Status != DividendRunStatus.Reviewed)
        {
            throw new InvalidOperationException(
                Status == DividendRunStatus.Draft
                    ? "The treasurer reviews a dividend run before the chairman approves it."
                    : $"A {Status} dividend run cannot be approved.");
        }

        if (chairman == ReviewedBy)
        {
            throw new InvalidOperationException(
                $"{chairman} reviewed this run, so they cannot also approve it. The review and " +
                "the approval are two people looking at the figure, which is the only reason " +
                "there are two of them.");
        }

        Status = DividendRunStatus.Approved;
        ApprovedBy = chairman;
        ApprovedAtUtc = approvedAtUtc;
    }

    /// <summary>Records that the approved run has been posted to the ledger.</summary>
    public void MarkPosted(DateOnly postedOn, DateTimeOffset postedAtUtc)
    {
        if (!MayBePosted)
        {
            throw new InvalidOperationException(
                Status == DividendRunStatus.Posted
                    ? $"The {Year} dividend was already posted on {PostedOn:d MMMM yyyy}."
                    : $"A {Status} dividend run cannot be posted. It must be reviewed by the " +
                      "treasurer and approved by the chairman first.");
        }

        Status = DividendRunStatus.Posted;
        PostedOn = postedOn;
        PostedAtUtc = postedAtUtc;

        Raise(new DividendDeclared(Id, Year, Distributable, _lines.Count, postedOn, postedAtUtc));
    }

    /// <summary>Abandons a run before it is posted.</summary>
    /// <remarks>
    /// The run is kept, marked withdrawn, with the reason. A computation that was done and
    /// then dropped is a thing that happened, and the next AGM may well ask about it.
    /// </remarks>
    public void Withdraw(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status == DividendRunStatus.Posted)
        {
            throw new InvalidOperationException(
                "A posted dividend cannot be withdrawn. Reverse the journal entry instead, " +
                "which leaves both the declaration and its reversal visible.");
        }

        Status = DividendRunStatus.Withdrawn;
        WithdrawnReason = reason.Trim();
    }

    /// <summary>What state the run is in, in words for the screen and the schedule.</summary>
    public string Verdict => Status switch
    {
        DividendRunStatus.Draft =>
            $"Computed by {ComputedBy}. Waiting for the treasurer to review it.",

        DividendRunStatus.Reviewed =>
            $"Reviewed by {ReviewedBy}. Waiting for the chairman to approve it.",

        DividendRunStatus.Approved =>
            $"Approved by {ApprovedBy}. Ready to post.",

        DividendRunStatus.Posted =>
            $"Posted on {PostedOn:d MMMM yyyy}. {_lines.Count} member(s) are owed " +
            $"{TotalAllocated} between them.",

        DividendRunStatus.Withdrawn => $"Withdrawn: {WithdrawnReason}",

        _ => "Unrecognised state.",
    };

    public override string ToString() => $"{Year} dividend, {Distributable} ({Status})";
}

/// <summary>Raised when a dividend run is posted.</summary>
public sealed record DividendDeclared(
    DividendRunId RunId,
    int Year,
    Money Distributable,
    int MemberCount,
    DateOnly PostedOn,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
