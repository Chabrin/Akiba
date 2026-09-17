using Akiba.Application.Abstractions;
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
        RuleFor(command => command.PayrollNumber).NotEmpty();
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

        var payrollNumber = PayrollNumber.Of(command.PayrollNumber);

        var existing = await _borrowers
            .FindMemberByPayrollNumberAsync(payrollNumber, cancellationToken)
            .ConfigureAwait(false);

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
        _borrowers.Update(member);

        var review = await BuildExitReviewAsync(member, command.ExitedOn, cancellationToken)
            .ConfigureAwait(false);

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
            review.ClerkTask);
    }
}
