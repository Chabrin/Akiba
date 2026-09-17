using Akiba.Application.Abstractions;
using Akiba.Application.Posting;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Domain.Membership;
using Akiba.Domain.Receipting;
using FluentValidation;
using MediatR;

namespace Akiba.Application.Receipting;

/// <summary>Records a cheque from HR covering a month's payroll deductions.</summary>
public sealed record RecordPayrollReceiptCommand(
    Money Amount, DateOnly ReceivedOn, string ChequeNumber) : IRequest<ReceiptId>;

/// <summary>Records a cheque from the payments team covering landlord rent offsets.</summary>
/// <remarks>
/// Kept apart from the payroll receipt because the two are drawn on different accounts - the
/// business account and the main account - and reconcile against different statements.
/// </remarks>
public sealed record RecordLandlordReceiptCommand(
    Money Amount, DateOnly ReceivedOn, string ChequeNumber) : IRequest<ReceiptId>;

/// <summary>Records money paid in directly by a member or a client.</summary>
/// <param name="Amount">How much.</param>
/// <param name="Method">Cash, bank deposit, M-Pesa or cheque.</param>
/// <param name="ReceivedOn">When the office learned of it.</param>
/// <param name="Reference">The M-Pesa code, deposit slip number or cheque number.</param>
/// <param name="PayerNameOnSlip">The name written on the slip, as written.</param>
/// <param name="ExpectedClearanceOn">When a cheque is expected to mature. Required for a cheque.</param>
public sealed record RecordDirectDepositCommand(
    Money Amount,
    ReceiptMethod Method,
    DateOnly ReceivedOn,
    string Reference,
    string PayerNameOnSlip,
    DateOnly? ExpectedClearanceOn) : IRequest<ReceiptId>;

public sealed class RecordDirectDepositValidator : AbstractValidator<RecordDirectDepositCommand>
{
    public RecordDirectDepositValidator()
    {
        RuleFor(command => command.Amount.Amount).GreaterThan(0m);
        RuleFor(command => command.Reference).NotEmpty();

        RuleFor(command => command.PayerNameOnSlip)
            .NotEmpty()
            .WithMessage(
                "A direct deposit is matched to a member by the name written on the slip, so " +
                "there must be one.");

        RuleFor(command => command.ExpectedClearanceOn)
            .NotNull()
            .When(command => command.Method == ReceiptMethod.Cheque)
            .WithMessage("Akiba does not act on a cheque until it has matured, so state when it will.");
    }
}

internal sealed class RecordReceiptHandler
    : IRequestHandler<RecordPayrollReceiptCommand, ReceiptId>,
      IRequestHandler<RecordLandlordReceiptCommand, ReceiptId>,
      IRequestHandler<RecordDirectDepositCommand, ReceiptId>
{
    private readonly IReceiptRepository _receipts;
    private readonly IUnitOfWork _unitOfWork;

    public RecordReceiptHandler(IReceiptRepository receipts, IUnitOfWork unitOfWork)
    {
        _receipts = receipts;
        _unitOfWork = unitOfWork;
    }

    public Task<ReceiptId> Handle(RecordPayrollReceiptCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return SaveAsync(
            Receipt.FromPayroll(command.Amount, command.ReceivedOn, command.ChequeNumber),
            cancellationToken);
    }

    public Task<ReceiptId> Handle(RecordLandlordReceiptCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return SaveAsync(
            Receipt.FromLandlordSchedule(command.Amount, command.ReceivedOn, command.ChequeNumber),
            cancellationToken);
    }

    public Task<ReceiptId> Handle(RecordDirectDepositCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return SaveAsync(
            Receipt.FromDirectDeposit(
                command.Amount,
                command.Method,
                command.ReceivedOn,
                command.Reference,
                command.PayerNameOnSlip,
                command.ExpectedClearanceOn),
            cancellationToken);
    }

    private async Task<ReceiptId> SaveAsync(Receipt receipt, CancellationToken cancellationToken)
    {
        _receipts.Add(receipt);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return receipt.Id;
    }
}

/// <summary>
/// Marks a receipt as cleared, and posts it to the ledger.
/// </summary>
/// <remarks>
/// <b>This is the moment money enters the books.</b> A recorded receipt is only an
/// expectation; a cheque that has not matured has not brought anything in. So the entry -
/// debit Bank, credit Unallocated Receipts - is posted here rather than when the office first
/// heard about the money, and the ledger holds funds Akiba actually has.
/// </remarks>
public sealed record ClearReceiptCommand(ReceiptId ReceiptId, DateOnly ClearedOn)
    : IRequest<JournalEntryId>;

internal sealed class ClearReceiptHandler : IRequestHandler<ClearReceiptCommand, JournalEntryId>
{
    private readonly IReceiptRepository _receipts;
    private readonly IJournalRepository _journal;
    private readonly IAkibaAccounts _accounts;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public ClearReceiptHandler(
        IReceiptRepository receipts,
        IJournalRepository journal,
        IAkibaAccounts accounts,
        ICurrentUser currentUser,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _receipts = receipts;
        _journal = journal;
        _accounts = accounts;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task<JournalEntryId> Handle(
        ClearReceiptCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var receipt = await _receipts.FindByIdAsync(command.ReceiptId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No receipt with id {command.ReceiptId}.");

        receipt.Clear(command.ClearedOn);
        _receipts.Update(receipt);

        var entry = AkibaPostings.ReceiptCleared(
            await _accounts.BankAsync(cancellationToken).ConfigureAwait(false),
            await _accounts.UnallocatedReceiptsAsync(cancellationToken).ConfigureAwait(false),
            receipt.Amount,
            command.ClearedOn,
            receipt.ReceivedOn,
            NarrationFor(receipt),
            SourceDocumentFor(receipt),
            _currentUser.Actor,
            _clock.UtcNow);

        await _journal.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return entry.Id;
    }

    private static string NarrationFor(Receipt receipt) => receipt.Channel switch
    {
        ReceiptChannel.PayrollDeduction => $"Payroll deductions received - cheque {receipt.Reference}",
        ReceiptChannel.LandlordRentOffset => $"Landlord rent offsets received - cheque {receipt.Reference}",
        _ => $"Direct deposit received - {receipt.PayerNameOnSlip}",
    };

    private static SourceDocument SourceDocumentFor(Receipt receipt) => receipt.Method switch
    {
        ReceiptMethod.Cheque => SourceDocument.Cheque(receipt.Reference),
        ReceiptMethod.Mpesa => SourceDocument.Of(SourceDocumentKind.MpesaReceipt, receipt.Reference),
        _ => SourceDocument.Of(SourceDocumentKind.BankDeposit, receipt.Reference),
    };
}

/// <summary>
/// Applies part of a cleared receipt to a member's shares, a loan instalment, or an
/// overpayment.
/// </summary>
/// <remarks>
/// <para>
/// Allocation is <b>never inferred</b>. The clerk decides, and the command records who decided
/// - which is why <see cref="ICurrentUser"/> is a dependency rather than a convenience. A
/// wrong guess in a system of record is worse than no guess, because it looks like a decision
/// somebody made.
/// </para>
/// <para>
/// The money moves out of Unallocated Receipts and into whatever it was applied to, in the
/// same transaction as the allocation itself - so the receipt and the ledger cannot disagree.
/// </para>
/// </remarks>
/// <param name="ReceiptId">The receipt.</param>
/// <param name="Target">Shares, a loan instalment, or one of the two overpayment outcomes.</param>
/// <param name="TargetId">The member or the loan.</param>
/// <param name="Amount">How much of the receipt to apply.</param>
/// <param name="EntryDate">The date the entry belongs to.</param>
public sealed record AllocateReceiptCommand(
    ReceiptId ReceiptId,
    AllocationTarget Target,
    Guid TargetId,
    Money Amount,
    DateOnly EntryDate) : IRequest<JournalEntryId>;

internal sealed class AllocateReceiptHandler
    : IRequestHandler<AllocateReceiptCommand, JournalEntryId>
{
    private readonly IReceiptRepository _receipts;
    private readonly IBorrowerRepository _borrowers;
    private readonly ILoanRepository _loans;
    private readonly IJournalRepository _journal;
    private readonly IAkibaAccounts _accounts;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public AllocateReceiptHandler(
        IReceiptRepository receipts,
        IBorrowerRepository borrowers,
        ILoanRepository loans,
        IJournalRepository journal,
        IAkibaAccounts accounts,
        ICurrentUser currentUser,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _receipts = receipts;
        _borrowers = borrowers;
        _loans = loans;
        _journal = journal;
        _accounts = accounts;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task<JournalEntryId> Handle(
        AllocateReceiptCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var receipt = await _receipts.FindByIdAsync(command.ReceiptId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No receipt with id {command.ReceiptId}.");

        if (!receipt.IsConfirmed)
        {
            throw new InvalidOperationException(
                $"Receipt {receipt.Reference} has not cleared, so there is nothing to allocate " +
                "yet. Clear it once the cheque has matured or the deposit is confirmed.");
        }

        var clerk = _currentUser.Actor;

        receipt.Allocate(command.Target, command.TargetId, command.Amount, clerk, _clock.UtcNow);
        _receipts.Update(receipt);

        var unallocated = await _accounts.UnallocatedReceiptsAsync(cancellationToken)
            .ConfigureAwait(false);

        var entry = command.Target switch
        {
            AllocationTarget.Shares or AllocationTarget.OverpaymentToShares =>
                await ShareEntryAsync(command, receipt, unallocated, clerk, cancellationToken)
                    .ConfigureAwait(false),

            AllocationTarget.LoanInstalment =>
                await LoanEntryAsync(command, receipt, unallocated, clerk, cancellationToken)
                    .ConfigureAwait(false),

            AllocationTarget.OverpaymentRefund =>
                await RefundEntryAsync(command, receipt, unallocated, clerk, cancellationToken)
                    .ConfigureAwait(false),

            _ => throw new ArgumentOutOfRangeException(
                nameof(command), command.Target, "Unknown allocation target."),
        };

        await _journal.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return entry.Id;
    }

    private async Task<JournalEntry> ShareEntryAsync(
        AllocateReceiptCommand command,
        Receipt receipt,
        AccountId unallocated,
        Domain.Common.Actor clerk,
        CancellationToken cancellationToken)
    {
        var member = await _borrowers
            .FindMemberAsync(new BorrowerId(command.TargetId), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No member with id {command.TargetId}. Shares can only be allocated to a member.");

        return AkibaPostings.AllocateToShares(
            unallocated,
            member.SharesAccountId,
            command.Amount,
            command.EntryDate,
            member.Name.Full,
            SourceDocument.Of(SourceDocumentKind.PayrollSchedule, receipt.Reference),
            clerk,
            _clock.UtcNow);
    }

    private async Task<JournalEntry> LoanEntryAsync(
        AllocateReceiptCommand command,
        Receipt receipt,
        AccountId unallocated,
        Domain.Common.Actor clerk,
        CancellationToken cancellationToken)
    {
        var loan = await _loans
            .FindByIdAsync(new Domain.Lending.LoanId(command.TargetId), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No loan with id {command.TargetId}.");

        return AkibaPostings.AllocateToLoan(
            unallocated,
            loan.ReceivableAccountId,
            command.Amount,
            command.EntryDate,
            loan.LoanNumber,
            SourceDocument.Of(SourceDocumentKind.PayrollSchedule, receipt.Reference),
            clerk,
            _clock.UtcNow);
    }

    private async Task<JournalEntry> RefundEntryAsync(
        AllocateReceiptCommand command,
        Receipt receipt,
        AccountId unallocated,
        Domain.Common.Actor clerk,
        CancellationToken cancellationToken)
    {
        var member = await _borrowers
            .FindMemberAsync(new BorrowerId(command.TargetId), cancellationToken)
            .ConfigureAwait(false);

        return AkibaPostings.RefundOverpayment(
            unallocated,
            await _accounts.BankAsync(cancellationToken).ConfigureAwait(false),
            command.Amount,
            command.EntryDate,
            member?.Name.Full ?? receipt.PayerNameOnSlip ?? "member",
            SourceDocument.Cheque(receipt.Reference),
            clerk,
            _clock.UtcNow);
    }
}
