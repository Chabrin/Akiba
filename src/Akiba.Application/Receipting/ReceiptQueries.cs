using Akiba.Application.Abstractions;
using Akiba.Domain.Financial;
using Akiba.Domain.Lending;
using Akiba.Domain.Receipting;
using MediatR;

namespace Akiba.Application.Receipting;

/// <summary>A receipt as the receipts screen shows it.</summary>
public sealed record ReceiptSummary(
    ReceiptId ReceiptId,
    ReceiptChannel Channel,
    ReceiptMethod Method,
    Money Amount,
    Money Allocated,
    Money Unallocated,
    DateOnly ReceivedOn,
    DateOnly? ExpectedClearanceOn,
    DateOnly? ClearedOn,
    string Reference,
    string? PayerNameOnSlip,
    ReceiptStatus Status)
{
    public bool IsConfirmed => Status is ReceiptStatus.Cleared or ReceiptStatus.Reconciled;

    public bool IsFullyAllocated => Unallocated.IsZero;

    /// <summary>
    /// Whether a cheque is past the date it was expected to mature. Bank statements arrive
    /// quarterly, so one can sit here a while - but an old one is worth a phone call.
    /// </summary>
    public bool ClearanceOverdue(DateOnly today) =>
        Status == ReceiptStatus.Recorded && ExpectedClearanceOn is { } expected && expected < today;
}

/// <summary>Which receipts to show.</summary>
public enum ReceiptWorkList
{
    /// <summary>Recorded but not cleared. A cheque that has not matured.</summary>
    AwaitingClearance = 1,

    /// <summary>Cleared, with money still unapplied. The clerk's work queue.</summary>
    AwaitingAllocation = 2,

    /// <summary>Everything received in a date range.</summary>
    All = 3,
}

/// <summary>
/// The receipts screen.
/// </summary>
/// <remarks>
/// The two queues are the ones the accounts clerk actually works from: what has not cleared,
/// and what has cleared but has not been applied to anything. The second is worked by hand
/// because allocation is never inferred.
/// </remarks>
public sealed record ListReceiptsQuery(ReceiptWorkList WorkList, DateOnly From, DateOnly To)
    : IRequest<IReadOnlyList<ReceiptSummary>>;

internal sealed class ListReceiptsHandler
    : IRequestHandler<ListReceiptsQuery, IReadOnlyList<ReceiptSummary>>
{
    private readonly IReceiptRepository _receipts;

    public ListReceiptsHandler(IReceiptRepository receipts) => _receipts = receipts;

    public async Task<IReadOnlyList<ReceiptSummary>> Handle(
        ListReceiptsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var receipts = query.WorkList switch
        {
            ReceiptWorkList.AwaitingClearance =>
                await _receipts.AwaitingClearanceAsync(cancellationToken).ConfigureAwait(false),
            ReceiptWorkList.AwaitingAllocation =>
                await _receipts.AwaitingAllocationAsync(cancellationToken).ConfigureAwait(false),
            _ =>
                await _receipts.BetweenAsync(query.From, query.To, cancellationToken)
                    .ConfigureAwait(false),
        };

        return
        [
            .. receipts
                .Select(receipt => new ReceiptSummary(
                    receipt.Id,
                    receipt.Channel,
                    receipt.Method,
                    receipt.Amount,
                    receipt.AllocatedAmount,
                    receipt.UnallocatedAmount,
                    receipt.ReceivedOn,
                    receipt.ExpectedClearanceOn,
                    receipt.ClearedOn,
                    receipt.Reference,
                    receipt.PayerNameOnSlip,
                    receipt.Status))
                .OrderBy(receipt => receipt.ReceivedOn),
        ];
    }
}

/// <summary>
/// What can be allocated to, for the allocation form.
/// </summary>
/// <param name="TargetId">The member or the loan.</param>
/// <param name="Description">What the clerk reads when choosing.</param>
/// <param name="Target">Shares or a loan instalment.</param>
/// <param name="SuggestedAmount">
/// The instalment due, where the target is a loan. A prompt, not a decision - the clerk still
/// types the figure, because allocation is never inferred.
/// </param>
public sealed record AllocationTargetOption(
    Guid TargetId,
    string Description,
    AllocationTarget Target,
    Money? SuggestedAmount);

/// <summary>
/// The things a member's money could be applied to: their shares, and each of their loans.
/// </summary>
public sealed record GetAllocationTargetsQuery(Domain.Membership.BorrowerId BorrowerId, DateOnly AsAt)
    : IRequest<IReadOnlyList<AllocationTargetOption>>;

internal sealed class GetAllocationTargetsHandler
    : IRequestHandler<GetAllocationTargetsQuery, IReadOnlyList<AllocationTargetOption>>
{
    private readonly IBorrowerRepository _borrowers;
    private readonly ILoanRepository _loans;

    public GetAllocationTargetsHandler(IBorrowerRepository borrowers, ILoanRepository loans)
    {
        _borrowers = borrowers;
        _loans = loans;
    }

    public async Task<IReadOnlyList<AllocationTargetOption>> Handle(
        GetAllocationTargetsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var options = new List<AllocationTargetOption>();

        var member = await _borrowers
            .FindMemberAsync(query.BorrowerId, cancellationToken)
            .ConfigureAwait(false);

        if (member is not null)
        {
            options.Add(new AllocationTargetOption(
                member.Id.Value,
                $"Shares - {member.Name.Full}",
                AllocationTarget.Shares,
                SuggestedAmount: null));
        }

        var loans = await _loans
            .RunningForBorrowerAsync(query.BorrowerId, cancellationToken)
            .ConfigureAwait(false);

        foreach (var loan in loans)
        {
            var instalment = loan.Schedule
                .InstalmentDueIn(query.AsAt.Year, query.AsAt.Month)?.Amount;

            options.Add(new AllocationTargetOption(
                loan.Id.Value,
                $"{loan.LoanNumber} - {loan.Terms.Product.DisplayName()}",
                AllocationTarget.LoanInstalment,
                instalment));
        }

        return options;
    }
}
