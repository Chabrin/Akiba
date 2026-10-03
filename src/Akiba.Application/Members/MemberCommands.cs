using Akiba.Application.Abstractions;
using Akiba.Application.Posting;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Domain.Membership;
using FluentValidation;
using MediatR;

namespace Akiba.Application.Members;

/// <summary>
/// Enrols a member and opens their share account.
/// </summary>
/// <remarks>
/// <para>
/// This does <b>not</b> make them a member as far as any rule is concerned. Membership begins
/// at the first share contribution, and that date is derived from the ledger - so a member
/// enrolled today and first deducted next month has no membership start date in between,
/// which is a real state rather than a gap.
/// </para>
/// <para>
/// What it does do is open the share account, because their shareholding is that account's
/// balance and they cannot have one without it.
/// </para>
/// </remarks>
public sealed record EnrolMemberCommand(
    string MembershipNumber,
    string PayrollNumber,
    string GivenName,
    string FamilyName,
    string? OtherNames,
    string NationalId,
    string Phone,
    string? Email,
    ZoneId ZoneId,
    bool IsLandlord) : IRequest<BorrowerId>;

public sealed class EnrolMemberValidator : AbstractValidator<EnrolMemberCommand>
{
    public EnrolMemberValidator()
    {
        RuleFor(command => command.MembershipNumber).NotEmpty();

        // Deliberately NOT required. The society's own register carries shareholders who are
        // not on the CAL payroll, written as staff number 0. Requiring it here would have
        // refused them one layer above the domain, which had already been fixed to allow them.
        RuleFor(command => command.GivenName).NotEmpty();
        RuleFor(command => command.FamilyName).NotEmpty();
        RuleFor(command => command.NationalId).NotEmpty();
        RuleFor(command => command.Phone).NotEmpty();

        RuleFor(command => command.ZoneId)
            .Must(zone => zone.IsSpecified)
            .WithMessage(
                "A member belongs to a zone or the office, whose representatives approve their " +
                "loan applications.");
    }
}

internal sealed class EnrolMemberHandler : IRequestHandler<EnrolMemberCommand, BorrowerId>
{
    private readonly IBorrowerRepository _borrowers;
    private readonly IZoneRepository _zones;
    private readonly IAkibaAccounts _accounts;
    private readonly IUnitOfWork _unitOfWork;

    public EnrolMemberHandler(
        IBorrowerRepository borrowers,
        IZoneRepository zones,
        IAkibaAccounts accounts,
        IUnitOfWork unitOfWork)
    {
        _borrowers = borrowers;
        _zones = zones;
        _accounts = accounts;
        _unitOfWork = unitOfWork;
    }

    public async Task<BorrowerId> Handle(
        EnrolMemberCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var payrollNumber = PayrollNumber.FromRegister(command.PayrollNumber);

        // Only a payroll number that is actually present can clash. Several members may have
        // none.
        var existing = payrollNumber.IsSpecified
            ? await _borrowers
                .FindMemberByPayrollNumberAsync(payrollNumber, cancellationToken)
                .ConfigureAwait(false)
            : null;

        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"Payroll number {payrollNumber} already belongs to {existing.Name}. HR matches " +
                "the deduction schedule on it, so it cannot be shared.");
        }

        _ = await _zones.FindByIdAsync(command.ZoneId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No zone with id {command.ZoneId}.");

        var memberId = Guid.NewGuid();
        var name = new PersonName(command.GivenName, command.FamilyName, command.OtherNames);

        var sharesAccount = await _accounts
            .OpenMemberSharesAccountAsync(command.MembershipNumber, name.Full, memberId, cancellationToken)
            .ConfigureAwait(false);

        var member = Member.Join(
            MembershipNumber.Of(command.MembershipNumber),
            payrollNumber,
            name,
            NationalId.Of(command.NationalId),
            PhoneNumber.Of(command.Phone),
            command.Email,
            command.ZoneId,
            sharesAccount.Id);

        if (command.IsLandlord)
        {
            member.MarkAsLandlord();
        }

        _borrowers.Add(member);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return member.Id;
    }
}

/// <summary>Processes a member's share withdrawal.</summary>
/// <param name="MemberId">Who is withdrawing.</param>
/// <param name="Amount">How much they want back.</param>
/// <param name="ProcessedOn">The date the withdrawal is processed.</param>
/// <param name="VoucherReference">The withdrawal voucher number for the paper trail.</param>
public sealed record WithdrawSharesCommand(
    BorrowerId MemberId,
    Money Amount,
    DateOnly ProcessedOn,
    string VoucherReference) : IRequest;

public sealed class WithdrawSharesValidator : AbstractValidator<WithdrawSharesCommand>
{
    public WithdrawSharesValidator()
    {
        RuleFor(command => command.MemberId).Must(id => id.IsSpecified);
        RuleFor(command => command.Amount.Amount).GreaterThan(0m);
        RuleFor(command => command.VoucherReference).NotEmpty();
    }
}

internal sealed class WithdrawSharesHandler : IRequestHandler<WithdrawSharesCommand>
{
    private readonly IBorrowerRepository _borrowers;
    private readonly ILoanRepository _loans;
    private readonly IJournalRepository _journal;
    private readonly IBalanceQueries _balances;
    private readonly IAkibaAccounts _accounts;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public WithdrawSharesHandler(
        IBorrowerRepository borrowers,
        ILoanRepository loans,
        IJournalRepository journal,
        IBalanceQueries balances,
        IAkibaAccounts accounts,
        ICurrentUser currentUser,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _borrowers = borrowers;
        _loans = loans;
        _journal = journal;
        _balances = balances;
        _accounts = accounts;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(WithdrawSharesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var member = await _borrowers.FindMemberAsync(command.MemberId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No member with id {command.MemberId}.");

        if (member.SharesOnHold)
        {
            throw new InvalidOperationException(
                $"{member.Name.Full}'s shares are on hold pending exit settlement. " +
                "Release the hold once all outstanding obligations have been resolved, " +
                "then process the withdrawal.");
        }

        var running = await _loans.RunningForBorrowerAsync(command.MemberId, cancellationToken)
            .ConfigureAwait(false);

        if (running.Count > 0)
        {
            throw new InvalidOperationException(
                $"{member.Name.Full} holds {running.Count} running loan(s) " +
                $"({string.Join(", ", running.Select(l => l.LoanNumber))}). " +
                "Share withdrawals are not permitted while a loan is running.");
        }

        var sharesBalance = await _balances
            .NaturalBalanceAsAtAsync(member.SharesAccountId, command.ProcessedOn, cancellationToken)
            .ConfigureAwait(false);

        if (command.Amount > sharesBalance)
        {
            throw new InvalidOperationException(
                $"Cannot withdraw {command.Amount}: the available shareholding as at " +
                $"{command.ProcessedOn:d MMM yyyy} is {sharesBalance}.");
        }

        var bank = await _accounts.BankAsync(cancellationToken).ConfigureAwait(false);

        var entry = AkibaPostings.ShareWithdrawal(
            member.SharesAccountId,
            bank,
            command.Amount,
            command.ProcessedOn,
            member.Name.Full,
            SourceDocument.WithdrawalVoucher(command.VoucherReference),
            _currentUser.Actor,
            _clock.UtcNow);

        await _journal.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Records the monthly share deduction amount a member has instructed.
/// </summary>
/// <remarks>
/// <para>
/// A member writes to the chairman to set or change the amount deducted from their salary
/// each month. The accounts clerk records the new figure here once it is approved.
/// </para>
/// <para>
/// Until this is set the deduction schedule falls back to inferring the amount from the
/// member's last journal entry, which is error-prone when top-ups and contributions arrive
/// separately. Setting it once makes every future schedule reliable.
/// </para>
/// </remarks>
public sealed record SetMemberContributionCommand(BorrowerId MemberId, decimal AmountKes)
    : IRequest;

public sealed class SetMemberContributionValidator : AbstractValidator<SetMemberContributionCommand>
{
    public SetMemberContributionValidator()
    {
        RuleFor(command => command.MemberId)
            .Must(id => id.IsSpecified)
            .WithMessage("A member must be identified.");

        RuleFor(command => command.AmountKes)
            .GreaterThan(0)
            .WithMessage("The monthly contribution must be a positive amount.")
            .LessThanOrEqualTo(500_000)
            .WithMessage("That amount looks too large for a monthly contribution. Please check.");
    }
}

internal sealed class SetMemberContributionHandler : IRequestHandler<SetMemberContributionCommand>
{
    private readonly IBorrowerRepository _borrowers;
    private readonly IUnitOfWork _unitOfWork;

    public SetMemberContributionHandler(IBorrowerRepository borrowers, IUnitOfWork unitOfWork)
    {
        _borrowers = borrowers;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(SetMemberContributionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var member = await _borrowers.FindMemberAsync(command.MemberId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No member with id {command.MemberId}.");

        member.SetMonthlyContribution(new Money(command.AmountKes, Currency.Kes));
        _borrowers.Update(member);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Enables or disables notifications for a member.
/// </summary>
/// <remarks>
/// A member can request to be removed from the SMS/email distribution by writing to the
/// chairman. The clerk records the change here; it takes effect immediately.
/// </remarks>
public sealed record SetMemberNotificationsCommand(BorrowerId MemberId, bool Enabled) : IRequest;

internal sealed class SetMemberNotificationsHandler : IRequestHandler<SetMemberNotificationsCommand>
{
    private readonly IBorrowerRepository _borrowers;
    private readonly IUnitOfWork _unitOfWork;

    public SetMemberNotificationsHandler(IBorrowerRepository borrowers, IUnitOfWork unitOfWork)
    {
        _borrowers = borrowers;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(SetMemberNotificationsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var member = await _borrowers.FindMemberAsync(command.MemberId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No member with id {command.MemberId}.");

        if (command.Enabled)
        {
            member.EnableNotifications();
        }
        else
        {
            member.DisableNotifications();
        }

        _borrowers.Update(member);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Records that a member has left CAL.
/// </summary>
/// <remarks>
/// Consequential rather than administrative. Any loan balance is recovered from final dues
/// with a shortfall passing to the guarantors, and every loan this member <i>guarantees</i> is
/// flagged - a guarantor must be a current CAL employee. The handler returns the exit review
/// so the clerk sees immediately whether the member's own funds may be released.
/// </remarks>
public sealed record RecordMemberExitCommand(BorrowerId MemberId, DateOnly ExitedOn)
    : IRequest<MemberExitOutcome>;

/// <summary>What an exit means for the member and for the loans they guarantee.</summary>
/// <param name="MemberName">Who left.</param>
/// <param name="FundsMayBeReleased">Whether their own shares and final dues can be paid out.</param>
/// <param name="LoansNeedingReplacementGuarantor">
/// Loans whose balance exceeds the borrower's own shares. Those borrowers must find a
/// replacement guarantor.
/// </param>
/// <param name="ClerkTask">What the accounts clerk needs to do, in plain words.</param>
public sealed record MemberExitOutcome(
    string MemberName,
    bool FundsMayBeReleased,
    IReadOnlyList<string> LoansNeedingReplacementGuarantor,
    string ClerkTask);

internal sealed class RecordMemberExitHandler
    : IRequestHandler<RecordMemberExitCommand, MemberExitOutcome>
{
    private readonly IBorrowerRepository _borrowers;
    private readonly ILoanRepository _loans;
    private readonly IBalanceQueries _balances;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public RecordMemberExitHandler(
        IBorrowerRepository borrowers,
        ILoanRepository loans,
        IBalanceQueries balances,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _borrowers = borrowers;
        _loans = loans;
        _balances = balances;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task<MemberExitOutcome> Handle(
        RecordMemberExitCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var member = await _borrowers.FindMemberAsync(command.MemberId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No member with id {command.MemberId}.");

        member.ExitEmployment(command.ExitedOn, _clock.UtcNow);

        var review = await BuildExitReviewAsync(member, command.ExitedOn, cancellationToken)
            .ConfigureAwait(false);

        // Q14: shares are held automatically when the funds cannot yet be released. The clerk
        // releases the hold once the outstanding obligations are resolved.
        if (!review.FundsMayBeReleased)
        {
            member.PlaceSharesOnHold();
        }

        _borrowers.Update(member);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return review;
    }

    private async Task<MemberExitOutcome> BuildExitReviewAsync(
        Member member, DateOnly exitedOn, CancellationToken cancellationToken)
    {
        var guaranteed = await _loans.GuaranteedByAsync(member.Id, cancellationToken)
            .ConfigureAwait(false);

        var exposures = new List<Domain.Guaranteeing.GuaranteedLoanExposure>(guaranteed.Count);

        foreach (var loan in guaranteed)
        {
            var outstanding = await _balances
                .NaturalBalanceAsAtAsync(loan.ReceivableAccountId, exitedOn, cancellationToken)
                .ConfigureAwait(false);

            var borrower = await _borrowers.FindMemberAsync(loan.BorrowerId, cancellationToken)
                .ConfigureAwait(false);

            // A non-member client holds no shares, so nothing of theirs stands behind the loan.
            var borrowerShares = borrower is null
                ? Domain.Financial.Money.ZeroKes
                : await _balances
                    .NaturalBalanceAsAtAsync(borrower.SharesAccountId, exitedOn, cancellationToken)
                    .ConfigureAwait(false);

            var guarantee = loan.Guarantees.First(g => g.GuarantorId == member.Id && !g.IsReleased);

            var liability = new Domain.Guaranteeing.ProRataLiability()
                .Apportion(outstanding, loan.Guarantees)
                .FirstOrDefault(l => l.GuarantorId == member.Id);

            exposures.Add(new Domain.Guaranteeing.GuaranteedLoanExposure(
                loan.Id.Value,
                loan.LoanNumber,
                borrower?.Name.Full ?? "Non-member client",
                guarantee.GuaranteedAmount,
                outstanding,
                borrowerShares,
                liability?.AmountLiable ?? Domain.Financial.Money.ZeroKes,
                IsInArrears: false));
        }

        var exposure = Domain.Guaranteeing.GuarantorExposureReport.For(
            member.Id, member.Name.Full, exposures);

        var review = Domain.Guaranteeing.GuarantorExposureReport.ReviewExit(exposure, exitedOn);

        return new MemberExitOutcome(
            member.Name.Full,
            review.FundsMayBeReleased,
            [.. review.LoansNeedingReplacement.Select(loan => loan.LoanNumber)],
            $"{review.Departure} {review.ClerkTask}");
    }
}

/// <summary>
/// Releases a shares hold placed at exit, once all outstanding obligations are resolved.
/// </summary>
/// <remarks>
/// The hold is placed automatically when a member's exit is recorded and their funds cannot
/// yet be released. Once the outstanding loans, guarantees, and final dues have been settled,
/// the clerk records the release here, which allows the withdrawal to proceed.
/// </remarks>
public sealed record ReleaseSharesHoldCommand(BorrowerId MemberId) : IRequest;

internal sealed class ReleaseSharesHoldHandler : IRequestHandler<ReleaseSharesHoldCommand>
{
    private readonly IBorrowerRepository _borrowers;
    private readonly IUnitOfWork _unitOfWork;

    public ReleaseSharesHoldHandler(IBorrowerRepository borrowers, IUnitOfWork unitOfWork)
    {
        _borrowers = borrowers;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(ReleaseSharesHoldCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var member = await _borrowers.FindMemberAsync(command.MemberId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No member with id {command.MemberId}.");

        if (!member.SharesOnHold)
        {
            return;
        }

        member.ReleaseSharesHold();
        _borrowers.Update(member);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
