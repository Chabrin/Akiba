using Akiba.Application.Abstractions;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Domain.Reconciliation;
using FluentValidation;
using MediatR;

namespace Akiba.Application.Ledger;

/// <summary>One thing standing between a month and its close.</summary>
/// <param name="Problem">What is wrong, in an official's words.</param>
/// <param name="Remedy">What to do about it.</param>
public sealed record ClosingObstacle(string Problem, string Remedy);

/// <summary>
/// Whether a month can be closed, and what is stopping it.
/// </summary>
/// <param name="Year">The month.</param>
/// <param name="Month">The month.</param>
/// <param name="MonthEnd">Its last date.</param>
/// <param name="AlreadyClosed">Whether it is closed already.</param>
/// <param name="TrialBalanceDifference">Should be zero.</param>
/// <param name="Obstacles">Everything outstanding. Empty means it may close.</param>
public sealed record PeriodClosePreflight(
    int Year,
    int Month,
    DateOnly MonthEnd,
    bool AlreadyClosed,
    Money TrialBalanceDifference,
    IReadOnlyList<ClosingObstacle> Obstacles)
{
    public bool MayClose => !AlreadyClosed && Obstacles.Count == 0;
}

/// <summary>
/// Checks a month against everything that must be true before it is closed.
/// </summary>
/// <remarks>
/// Read before acting. The treasurer sees exactly what is outstanding rather than a refusal,
/// which is the difference between a control and an obstacle.
/// </remarks>
public sealed record GetPeriodClosePreflightQuery(int Year, int Month)
    : IRequest<PeriodClosePreflight>;

internal sealed class GetPeriodClosePreflightHandler
    : IRequestHandler<GetPeriodClosePreflightQuery, PeriodClosePreflight>
{
    private readonly IAccountingPeriodRepository _periods;
    private readonly IBankReconciliationRepository _reconciliations;
    private readonly IBalanceQueries _balances;

    public GetPeriodClosePreflightHandler(
        IAccountingPeriodRepository periods,
        IBankReconciliationRepository reconciliations,
        IBalanceQueries balances)
    {
        _periods = periods;
        _reconciliations = reconciliations;
        _balances = balances;
    }

    public async Task<PeriodClosePreflight> Handle(
        GetPeriodClosePreflightQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var monthStart = new DateOnly(query.Year, query.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        var existing = await _periods
            .FindMonthAsync(query.Year, query.Month, cancellationToken)
            .ConfigureAwait(false);

        var difference = await _balances
            .TrialBalanceDifferenceAsAtAsync(monthEnd, cancellationToken)
            .ConfigureAwait(false);

        var obstacles = new List<ClosingObstacle>();

        if (!difference.IsZero)
        {
            obstacles.Add(new ClosingObstacle(
                $"The trial balance as at {monthEnd:d MMMM yyyy} is out by {difference.Abs()}.",
                "Every journal entry sums to zero by construction, so this can only mean " +
                "something wrote to the database without going through Akiba. Do not close the " +
                "month; find out what did."));
        }

        var overlapping = await _reconciliations
            .OverlappingAsync(monthStart, monthEnd, cancellationToken)
            .ConfigureAwait(false);

        foreach (var reconciliation in overlapping.Where(r => !r.IsSignedOff))
        {
            obstacles.Add(new ClosingObstacle(
                $"The {reconciliation.AccountLabel} statement to " +
                $"{reconciliation.To:d MMMM yyyy} has not been signed off.",
                "Match its remaining lines, post anything the bank charged that Akiba has not " +
                "recorded, and have the treasurer sign it off."));
        }

        if (!overlapping.Any(r => r.IsSignedOff && r.From <= monthEnd && r.To >= monthEnd))
        {
            obstacles.Add(new ClosingObstacle(
                $"No signed-off bank statement covers {monthEnd:d MMMM yyyy}.",
                "Import and reconcile the statement covering this month first. Closing before " +
                "the statement arrives means the bank charges it reveals can no longer be " +
                "posted into the month they belong to."));
        }

        return new PeriodClosePreflight(
            query.Year,
            query.Month,
            monthEnd,
            existing?.IsClosed ?? false,
            difference,
            obstacles);
    }
}

/// <summary>
/// The treasurer closes a month.
/// </summary>
/// <remarks>
/// <para>
/// Refused while anything is outstanding. A close that can be forced is not a close, and the
/// whole value of a closed month is that a trial balance signed off in it still reads the way
/// it did when it was signed.
/// </para>
/// <para>
/// The period is created here if it does not exist. Periods are made as the ledger reaches
/// them rather than seeded years ahead, so an unseen month is simply an open one.
/// </para>
/// </remarks>
public sealed record ClosePeriodCommand(int Year, int Month) : IRequest<PeriodClosePreflight>;

public sealed class ClosePeriodValidator : AbstractValidator<ClosePeriodCommand>
{
    public ClosePeriodValidator()
    {
        RuleFor(command => command.Month).InclusiveBetween(1, 12);
        RuleFor(command => command.Year).InclusiveBetween(2000, 2100);
    }
}

internal sealed class ClosePeriodHandler : IRequestHandler<ClosePeriodCommand, PeriodClosePreflight>
{
    private readonly ISender _sender;
    private readonly IAccountingPeriodRepository _periods;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public ClosePeriodHandler(
        ISender sender,
        IAccountingPeriodRepository periods,
        ICurrentUser currentUser,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _sender = sender;
        _periods = periods;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task<PeriodClosePreflight> Handle(
        ClosePeriodCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var preflight = await _sender
            .Send(new GetPeriodClosePreflightQuery(command.Year, command.Month), cancellationToken)
            .ConfigureAwait(false);

        if (preflight.AlreadyClosed)
        {
            throw new InvalidOperationException(
                $"{preflight.MonthEnd:MMMM yyyy} is already closed.");
        }

        if (!preflight.MayClose)
        {
            throw new PeriodNotReadyToCloseException(preflight);
        }

        var period = await _periods
            .FindMonthAsync(command.Year, command.Month, cancellationToken)
            .ConfigureAwait(false);

        if (period is null)
        {
            period = AccountingPeriod.ForMonth(preflight.MonthEnd);
            period.Close(_currentUser.Actor, _clock.UtcNow);
            _periods.Add(period);
        }
        else
        {
            period.Close(_currentUser.Actor, _clock.UtcNow);
            _periods.Update(period);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return preflight with { AlreadyClosed = true };
    }
}

/// <summary>
/// The chairman reopens a closed month.
/// </summary>
/// <remarks>
/// Rare, and permanently recorded. It exists because a month occasionally has to be corrected
/// in place - a payroll schedule posted twice - and the alternative is to leave the books
/// knowingly wrong.
/// </remarks>
public sealed record ReopenPeriodCommand(int Year, int Month, string Reason) : IRequest;

public sealed class ReopenPeriodValidator : AbstractValidator<ReopenPeriodCommand>
{
    public ReopenPeriodValidator() =>
        RuleFor(command => command.Reason)
            .NotEmpty()
            .MinimumLength(10)
            .WithMessage(
                "Say why the month is being reopened. It stays on the period for good, and it " +
                "is what an auditor will ask about first.");
}

internal sealed class ReopenPeriodHandler : IRequestHandler<ReopenPeriodCommand>
{
    private readonly IAccountingPeriodRepository _periods;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public ReopenPeriodHandler(
        IAccountingPeriodRepository periods,
        ICurrentUser currentUser,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _periods = periods;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(ReopenPeriodCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var period = await _periods
            .FindMonthAsync(command.Year, command.Month, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{command.Month:00}/{command.Year} was never closed, so it cannot be reopened.");

        period.Reopen(_currentUser.Actor, command.Reason, _clock.UtcNow);

        _periods.Update(period);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Thrown when a month is closed while something is still outstanding.</summary>
public sealed class PeriodNotReadyToCloseException : InvalidOperationException
{
    public PeriodNotReadyToCloseException(PeriodClosePreflight preflight)
        : base(Explain(preflight)) =>
        Obstacles = preflight?.Obstacles ?? [];

    public PeriodNotReadyToCloseException()
        : base("The month cannot be closed yet.") => Obstacles = [];

    public PeriodNotReadyToCloseException(string message)
        : base(message) => Obstacles = [];

    public PeriodNotReadyToCloseException(string message, Exception innerException)
        : base(message, innerException) => Obstacles = [];

    public IReadOnlyList<ClosingObstacle> Obstacles { get; }

    private static string Explain(PeriodClosePreflight preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);

        return $"{preflight.MonthEnd:MMMM yyyy} cannot be closed yet: " +
               string.Join(" ", preflight.Obstacles.Select(obstacle => obstacle.Problem));
    }
}
