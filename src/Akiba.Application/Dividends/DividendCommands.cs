using Akiba.Application.Abstractions;
using Akiba.Application.Posting;
using Akiba.Domain.Dividends;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using FluentValidation;
using MediatR;

namespace Akiba.Application.Dividends;

/// <summary>Reads and writes dividend runs.</summary>
public interface IDividendRunRepository
{
    Task<DividendRun?> FindByIdAsync(DividendRunId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DividendRun>> AllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs for a year, including withdrawn ones.
    /// </summary>
    /// <remarks>
    /// Used to refuse a second posted run for the same year. A computation that was done and
    /// then dropped is kept, so this can return several.
    /// </remarks>
    Task<IReadOnlyList<DividendRun>> ForYearAsync(
        int year, CancellationToken cancellationToken = default);

    void Add(DividendRun run);

    void Update(DividendRun run);
}

/// <summary>Which basis to compute a run on.</summary>
public enum DividendBasisChoice
{
    /// <summary>
    /// Shareholding at the year end. The default, and the literal reading of the only rule
    /// anybody wrote down.
    /// </summary>
    ClosingShareholding = 1,

    /// <summary>The average of the twelve month-end shareholdings.</summary>
    TimeWeightedShareholding = 2,
}

/// <summary>
/// Computes a year's dividend as a draft.
/// </summary>
/// <param name="Year">The year being distributed.</param>
/// <param name="Basis">
/// How each member's share is worked out. Unsettled by the committee, so it is chosen
/// explicitly and recorded on the run.
/// </param>
/// <remarks>
/// Nothing is posted. The run is a draft until the treasurer reviews it and the chairman
/// approves it, and only then may it reach the ledger.
/// </remarks>
public sealed record ComputeDividendRunCommand(
    int Year,
    DividendBasisChoice Basis = DividendBasisChoice.ClosingShareholding)
    : IRequest<DividendRunId>;

public sealed class ComputeDividendRunValidator : AbstractValidator<ComputeDividendRunCommand>
{
    public ComputeDividendRunValidator() =>
        RuleFor(command => command.Year).InclusiveBetween(2000, 2100);
}

internal sealed class ComputeDividendRunHandler
    : IRequestHandler<ComputeDividendRunCommand, DividendRunId>
{
    private readonly IDividendRunRepository _runs;
    private readonly IBorrowerRepository _borrowers;
    private readonly IAccountRepository _accounts;
    private readonly IBalanceQueries _balances;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public ComputeDividendRunHandler(
        IDividendRunRepository runs,
        IBorrowerRepository borrowers,
        IAccountRepository accounts,
        IBalanceQueries balances,
        ICurrentUser currentUser,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _runs = runs;
        _borrowers = borrowers;
        _accounts = accounts;
        _balances = balances;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task<DividendRunId> Handle(
        ComputeDividendRunCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var existing = await _runs.ForYearAsync(command.Year, cancellationToken).ConfigureAwait(false);

        if (existing.Any(run => run.Status == DividendRunStatus.Posted))
        {
            throw new InvalidOperationException(
                $"The {command.Year} dividend has already been posted. A second one would pay " +
                "the same year twice; correct the first by reversing its journal entry.");
        }

        var yearStart = new DateOnly(command.Year, 1, 1);
        var yearEnd = new DateOnly(command.Year, 12, 31);

        var interestEarned = await MovementOverYearAsync(
            ChartOfAccounts.LoanInterestIncome, yearStart, yearEnd, cancellationToken)
            .ConfigureAwait(false);

        var restructuringFees = await MovementOverYearAsync(
            ChartOfAccounts.RestructuringFees, yearStart, yearEnd, cancellationToken)
            .ConfigureAwait(false);

        var bankCharges = await MovementOverYearAsync(
            ChartOfAccounts.BankCharges, yearStart, yearEnd, cancellationToken)
            .ConfigureAwait(false);

        var shareholdings = await DividendLedgerReader
            .ShareholdingsOverYearAsync(_borrowers, _balances, command.Year, cancellationToken)
            .ConfigureAwait(false);

        var run = DividendRun.Compute(
            command.Year,
            interestEarned + restructuringFees,
            bankCharges,
            shareholdings,
            BasisFor(command.Basis),
            _currentUser.Actor,
            _clock.UtcNow);

        _runs.Add(run);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return run.Id;
    }

    private Task<Money> MovementOverYearAsync(
        string accountCode, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        DividendLedgerReader.MovementOverYearAsync(
            _accounts, _balances, accountCode, from, to, cancellationToken);

    internal static IDividendBasis BasisFor(DividendBasisChoice choice) => choice switch
    {
        DividendBasisChoice.TimeWeightedShareholding => new TimeWeightedShareholdingBasis(),
        _ => new ClosingShareholdingBasis(),
    };
}

/// <summary>
/// Reads the ledger figures a dividend is worked out from.
/// </summary>
/// <remarks>
/// Shared by the computation and by the comparison that shows the committee what choosing one
/// basis over the other is worth. Two places reading the same figures two different ways is
/// how the comparison would stop describing the run.
/// </remarks>
internal static class DividendLedgerReader
{
    /// <summary>
    /// Every member's shareholding at each of the year's twelve month ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Members who left during the year are included. They held shares for part of it, and
    /// leaving CAL is not the same as forfeiting a year's dividend - though whether the
    /// society agrees is open question 14.
    /// </para>
    /// <para>
    /// Ordered by membership number, and that is not cosmetic. The allocation hands its
    /// leftover cents to the earliest weight, so the order decides who gets them. A membership
    /// number never changes; the repository's default order is by surname, which would mean a
    /// member marrying could move a cent from one person to another between two runs of the
    /// same computation. It also puts the schedule in the order the society's own register
    /// reads.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<MemberShareholdingOverYear>> ShareholdingsOverYearAsync(
        IBorrowerRepository borrowers,
        IBalanceQueries balances,
        int year,
        CancellationToken cancellationToken)
    {
        var members = await borrowers.AllMembersAsync(true, cancellationToken).ConfigureAwait(false);

        var shareholdings = new List<MemberShareholdingOverYear>(members.Count);

        foreach (var member in members)
        {
            var monthEnds = new List<Money>(12);

            for (var month = 1; month <= 12; month++)
            {
                var monthEnd = new DateOnly(year, month, DateTime.DaysInMonth(year, month));

                monthEnds.Add(await balances
                    .NaturalBalanceAsAtAsync(member.SharesAccountId, monthEnd, cancellationToken)
                    .ConfigureAwait(false));
            }

            shareholdings.Add(new MemberShareholdingOverYear(
                member.Id.Value,
                member.MembershipNumber.Value,
                member.Name.Full,
                member.SharesAccountId.Value,
                monthEnds[^1],
                monthEnds));
        }

        return
        [
            .. shareholdings.OrderBy(
                member => member.MembershipNumber, StringComparer.Ordinal),
        ];
    }

    /// <summary>What an income or expense account moved by over the year.</summary>
    /// <remarks>
    /// The difference between the two year ends, not the closing balance. Income and expense
    /// accounts have nothing to subtract in Akiba's first year but they do in every year
    /// after it, and a dividend that quietly distributed three years of interest would be
    /// found by the members rather than by the code.
    /// </remarks>
    public static async Task<Money> MovementOverYearAsync(
        IAccountRepository accounts,
        IBalanceQueries balances,
        string accountCode,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var account = await accounts
            .FindByCodeAsync(AccountCode.Of(accountCode), cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            return Money.ZeroKes;
        }

        var opening = await balances
            .NaturalBalanceAsAtAsync(account.Id, from.AddDays(-1), cancellationToken)
            .ConfigureAwait(false);

        var closing = await balances
            .NaturalBalanceAsAtAsync(account.Id, to, cancellationToken)
            .ConfigureAwait(false);

        return (closing - opening).Round();
    }
}

/// <summary>The treasurer records that they have been through the figures.</summary>
public sealed record ReviewDividendRunCommand(DividendRunId RunId) : IRequest;

internal sealed class ReviewDividendRunHandler : IRequestHandler<ReviewDividendRunCommand>
{
    private readonly IDividendRunRepository _runs;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public ReviewDividendRunHandler(
        IDividendRunRepository runs,
        ICurrentUser currentUser,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _runs = runs;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(ReviewDividendRunCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var run = await LoadAsync(_runs, command.RunId, cancellationToken).ConfigureAwait(false);

        run.Review(_currentUser.Actor, _clock.UtcNow);

        _runs.Update(run);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<DividendRun> LoadAsync(
        IDividendRunRepository runs, DividendRunId id, CancellationToken cancellationToken) =>
        await runs.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"No dividend run with id {id}.");
}

/// <summary>The chairman approves a reviewed run.</summary>
public sealed record ApproveDividendRunCommand(DividendRunId RunId) : IRequest;

internal sealed class ApproveDividendRunHandler : IRequestHandler<ApproveDividendRunCommand>
{
    private readonly IDividendRunRepository _runs;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public ApproveDividendRunHandler(
        IDividendRunRepository runs,
        ICurrentUser currentUser,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _runs = runs;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(ApproveDividendRunCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var run = await ReviewDividendRunHandler
            .LoadAsync(_runs, command.RunId, cancellationToken)
            .ConfigureAwait(false);

        run.Approve(_currentUser.Actor, _clock.UtcNow);

        _runs.Update(run);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Posts an approved run to the ledger.
/// </summary>
/// <param name="RunId">Which run.</param>
/// <param name="DeclaredOn">
/// The date of the declaration - the AGM's date, normally. It must be in an open period like
/// any other entry.
/// </param>
public sealed record PostDividendRunCommand(DividendRunId RunId, DateOnly? DeclaredOn = null)
    : IRequest<JournalEntryId>;

internal sealed class PostDividendRunHandler
    : IRequestHandler<PostDividendRunCommand, JournalEntryId>
{
    private readonly IDividendRunRepository _runs;
    private readonly IJournalRepository _journal;
    private readonly IAkibaAccounts _accounts;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public PostDividendRunHandler(
        IDividendRunRepository runs,
        IJournalRepository journal,
        IAkibaAccounts accounts,
        ICurrentUser currentUser,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _runs = runs;
        _journal = journal;
        _accounts = accounts;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task<JournalEntryId> Handle(
        PostDividendRunCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var run = await ReviewDividendRunHandler
            .LoadAsync(_runs, command.RunId, cancellationToken)
            .ConfigureAwait(false);

        var declaredOn = command.DeclaredOn ?? _clock.TodayInNairobi;

        var entry = AkibaPostings.DividendDeclaration(
            await _accounts.RetainedSurplusAsync(cancellationToken).ConfigureAwait(false),
            await _accounts.DividendsPayableAsync(cancellationToken).ConfigureAwait(false),
            [.. run.Lines.Select(line => (line.FullName, line.Amount))],
            declaredOn,
            run.Year,
            _currentUser.Actor,
            _clock.UtcNow);

        // The aggregate refuses unless the run has been reviewed and approved, so this runs
        // before the entry is saved rather than after.
        run.MarkPosted(declaredOn, _clock.UtcNow);

        await _journal.AddAsync(entry, cancellationToken).ConfigureAwait(false);

        _runs.Update(run);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return entry.Id;
    }
}

/// <summary>Abandons a run before it is posted, with a reason.</summary>
public sealed record WithdrawDividendRunCommand(DividendRunId RunId, string Reason) : IRequest;

public sealed class WithdrawDividendRunValidator : AbstractValidator<WithdrawDividendRunCommand>
{
    public WithdrawDividendRunValidator() =>
        RuleFor(command => command.Reason)
            .NotEmpty()
            .WithMessage(
                "Say why the run was abandoned. It is kept rather than deleted, and the next " +
                "AGM may well ask about it.");
}

internal sealed class WithdrawDividendRunHandler : IRequestHandler<WithdrawDividendRunCommand>
{
    private readonly IDividendRunRepository _runs;
    private readonly IUnitOfWork _unitOfWork;

    public WithdrawDividendRunHandler(IDividendRunRepository runs, IUnitOfWork unitOfWork)
    {
        _runs = runs;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(WithdrawDividendRunCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var run = await ReviewDividendRunHandler
            .LoadAsync(_runs, command.RunId, cancellationToken)
            .ConfigureAwait(false);

        run.Withdraw(command.Reason);

        _runs.Update(run);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
