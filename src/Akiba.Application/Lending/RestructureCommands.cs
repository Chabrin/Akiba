using Akiba.Application.Abstractions;
using Akiba.Application.Posting;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Domain.Lending;
using FluentValidation;
using MediatR;

namespace Akiba.Application.Lending;

/// <summary>What a restructure would look like, before anybody commits to it.</summary>
/// <param name="LoanId">The loan.</param>
/// <param name="LoanNumber">Its number, as officials quote it.</param>
/// <param name="BorrowerName">Whose loan.</param>
/// <param name="Assessment">Whether it may be restructured, and on what terms.</param>
/// <param name="ProposedTerms">The replacement's terms, where it is permitted.</param>
public sealed record RestructureProposal(
    LoanId LoanId,
    string LoanNumber,
    string BorrowerName,
    RestructureAssessment Assessment,
    LoanTerms? ProposedTerms)
{
    public bool IsPermitted => Assessment.IsPermitted;

    /// <summary>The monthly instalment on the replacement, where there would be one.</summary>
    public Money? Instalment => ProposedTerms is null || ProposedTerms.TermMonths == 0
        ? null
        : ProposedTerms.TotalRepayable.Allocate(ProposedTerms.TermMonths)[0];
}

/// <summary>
/// Works out whether a loan may be restructured, and what it would cost.
/// </summary>
/// <remarks>
/// Read before acting, and safe to read as often as anybody likes: it posts nothing and changes
/// nothing. The outstanding balance comes from the loan's receivable account, because the loan
/// aggregate holds no balance.
/// </remarks>
public sealed record AssessRestructureQuery(LoanId LoanId) : IRequest<RestructureProposal>;

internal sealed class AssessRestructureHandler
    : IRequestHandler<AssessRestructureQuery, RestructureProposal>
{
    private readonly ILoanRepository _loans;
    private readonly IBorrowerRepository _borrowers;
    private readonly IBalanceQueries _balances;
    private readonly IClock _clock;

    public AssessRestructureHandler(
        ILoanRepository loans,
        IBorrowerRepository borrowers,
        IBalanceQueries balances,
        IClock clock)
    {
        _loans = loans;
        _borrowers = borrowers;
        _balances = balances;
        _clock = clock;
    }

    public async Task<RestructureProposal> Handle(
        AssessRestructureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var loan = await _loans.FindByIdAsync(query.LoanId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No loan with id {query.LoanId}.");

        var outstanding = await _balances
            .NaturalBalanceAsAtAsync(loan.ReceivableAccountId, _clock.TodayInNairobi, cancellationToken)
            .ConfigureAwait(false);

        var assessment = Restructuring.Assess(loan, outstanding);

        var borrower = await _borrowers.FindByIdAsync(loan.BorrowerId, cancellationToken)
            .ConfigureAwait(false);

        return new RestructureProposal(
            loan.Id,
            loan.LoanNumber,
            borrower?.Name.Full ?? "Non-member client",
            assessment,
            assessment.IsPermitted ? Restructuring.TermsFor(assessment) : null);
    }
}

/// <summary>
/// Restructures a loan: closes it and opens a replacement carrying its balance.
/// </summary>
/// <param name="LoanId">The loan being restructured.</param>
/// <param name="ReplacementLoanNumber">The new number, which officials will quote.</param>
/// <param name="RestructuredOn">The date the committee agreed it.</param>
/// <param name="FeeReceiptReference">
/// How the 5% fee was paid. It is deducted upfront rather than rolled into the balance, so
/// there is a receipt for it.
/// </param>
/// <remarks>
/// Two journal entries and no cheque. The fee moves out of Unallocated Receipts into
/// Restructuring Fees; the balance moves from the old receivable to the new one. No money
/// leaves the bank, which is the whole point - a restructure rearranges a debt rather than
/// lending again.
/// </remarks>
public sealed record RestructureLoanCommand(
    LoanId LoanId,
    string ReplacementLoanNumber,
    DateOnly RestructuredOn,
    string FeeReceiptReference) : IRequest<LoanId>;

public sealed class RestructureLoanValidator : AbstractValidator<RestructureLoanCommand>
{
    public RestructureLoanValidator()
    {
        RuleFor(command => command.ReplacementLoanNumber)
            .NotEmpty()
            .WithMessage("The replacement needs its own number; officials quote these.");

        RuleFor(command => command.FeeReceiptReference)
            .NotEmpty()
            .WithMessage(
                "The restructuring fee is paid upfront, so say how. It is not rolled into the " +
                "balance, and a fee nobody can trace to a receipt is a fee nobody can prove was " +
                "paid.");
    }
}

internal sealed class RestructureLoanHandler : IRequestHandler<RestructureLoanCommand, LoanId>
{
    private readonly ISender _sender;
    private readonly ILoanRepository _loans;
    private readonly IJournalRepository _journal;
    private readonly IAkibaAccounts _accounts;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public RestructureLoanHandler(
        ISender sender,
        ILoanRepository loans,
        IJournalRepository journal,
        IAkibaAccounts accounts,
        ICurrentUser currentUser,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _sender = sender;
        _loans = loans;
        _journal = journal;
        _accounts = accounts;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task<LoanId> Handle(
        RestructureLoanCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var proposal = await _sender
            .Send(new AssessRestructureQuery(command.LoanId), cancellationToken)
            .ConfigureAwait(false);

        if (!proposal.IsPermitted)
        {
            throw new InvalidOperationException(proposal.Assessment.Explanation);
        }

        var existing = await _loans
            .FindByNumberAsync(command.ReplacementLoanNumber, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"Loan number {command.ReplacementLoanNumber} is already used by a loan " +
                $"disbursed on {existing.DisbursedOn:yyyy-MM-dd}. Officials quote these, so " +
                "they cannot repeat.");
        }

        var original = await _loans.FindByIdAsync(command.LoanId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No loan with id {command.LoanId}.");

        var receivable = await _accounts
            .OpenLoanReceivableAccountAsync(
                command.ReplacementLoanNumber, Guid.NewGuid(), cancellationToken)
            .ConfigureAwait(false);

        var replacement = Loan.FromRestructure(
            original,
            command.ReplacementLoanNumber,
            receivable.Id,
            proposal.ProposedTerms!,
            command.RestructuredOn,
            _clock.UtcNow);

        _loans.Update(original);
        _loans.Add(replacement);

        // The fee first: it is deducted upfront, and the restructured instalments start after
        // it. Rolling it into the balance would mean charging the member interest-free credit
        // on their own fee and would make the carried balance disagree with the assessment.
        var fee = AkibaPostings.RestructuringFee(
            await _accounts.UnallocatedReceiptsAsync(cancellationToken).ConfigureAwait(false),
            await _accounts.RestructuringFeesAsync(cancellationToken).ConfigureAwait(false),
            proposal.Assessment.Fee,
            command.RestructuredOn,
            original.LoanNumber,
            SourceDocument.Of(SourceDocumentKind.PaymentVoucher, command.FeeReceiptReference),
            _currentUser.Actor,
            _clock.UtcNow);

        await _journal.AddAsync(fee, cancellationToken).ConfigureAwait(false);

        // Then the balance itself: out of the old receivable, into the new one. Nothing touches
        // Bank, because no money moves.
        var carry = AkibaPostings.CarryBalanceIntoRestructure(
            original.ReceivableAccountId,
            receivable.Id,
            proposal.Assessment.RestructuredBalance,
            command.RestructuredOn,
            original.LoanNumber,
            replacement.LoanNumber,
            _currentUser.Actor,
            _clock.UtcNow);

        await _journal.AddAsync(carry, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return replacement.Id;
    }
}
