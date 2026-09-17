using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;

namespace Akiba.Application.Posting;

/// <summary>
/// Builds the journal entry for each thing that happens to Akiba's money.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place that knows which accounts each business event touches. Put another
/// way: if you want to know what a disbursement does to the books, it is in
/// <see cref="Disbursement"/> and nowhere else.
/// </para>
/// <para>
/// Every method returns a <see cref="JournalEntry"/>, whose constructor refuses to build
/// anything that does not sum to zero. So an error in the account mapping here cannot produce
/// an unbalanced ledger - it produces an exception at the moment of posting.
/// </para>
/// <para>
/// <b>When money is recognised.</b> A receipt posts to the ledger when it <i>clears</i>, not
/// when the office first hears about it. A cheque that has not matured has not brought any
/// money in, and the questionnaire is explicit that Akiba waits for one to mature before
/// taking any action. So the ledger holds money Akiba actually has, and the receipt record
/// holds what is expected - which is what lets a balance say whether it is showing confirmed
/// funds.
/// </para>
/// </remarks>
public static class AkibaPostings
{
    /// <summary>
    /// Money has cleared into the bank, not yet allocated to anything.
    /// </summary>
    /// <remarks>
    /// Debit Bank, credit Unallocated Receipts. It is a liability at this point because the
    /// money has arrived but Akiba has not yet said what it is for - until the clerk allocates
    /// it, it is still the member's.
    /// </remarks>
    public static JournalEntry ReceiptCleared(
        AccountId bank,
        AccountId unallocatedReceipts,
        Money amount,
        DateOnly clearedOn,
        DateOnly receivedOn,
        string narration,
        SourceDocument sourceDocument,
        Actor postedBy,
        DateTimeOffset postedAtUtc) =>
        JournalEntry.Post(
            clearedOn,
            narration,
            sourceDocument,
            postedBy,
            postedAtUtc,
            [
                JournalLine.Debit(bank, amount),
                JournalLine.Credit(unallocatedReceipts, amount),
            ],
            // The value date is when the money was paid in, which can be months before a
            // quarterly statement confirms it.
            valueDate: receivedOn);

    /// <summary>
    /// Part of a receipt has been applied to a member's shares.
    /// </summary>
    /// <remarks>
    /// Debit Unallocated Receipts, credit the member's share account. Both are liabilities:
    /// the money stops being unattributed and becomes something Akiba owes that member.
    /// </remarks>
    public static JournalEntry AllocateToShares(
        AccountId unallocatedReceipts,
        AccountId memberShares,
        Money amount,
        DateOnly entryDate,
        string memberName,
        SourceDocument sourceDocument,
        Actor postedBy,
        DateTimeOffset postedAtUtc) =>
        JournalEntry.Post(
            entryDate,
            $"Share contribution - {memberName}",
            sourceDocument,
            postedBy,
            postedAtUtc,
            [
                JournalLine.Debit(unallocatedReceipts, amount),
                JournalLine.Credit(memberShares, amount),
            ]);

    /// <summary>
    /// Part of a receipt has been applied to a loan instalment.
    /// </summary>
    /// <remarks>
    /// Debit Unallocated Receipts, credit the loan's receivable. The borrower owes less; the
    /// money is no longer unattributed.
    /// </remarks>
    public static JournalEntry AllocateToLoan(
        AccountId unallocatedReceipts,
        AccountId loanReceivable,
        Money amount,
        DateOnly entryDate,
        string loanNumber,
        SourceDocument sourceDocument,
        Actor postedBy,
        DateTimeOffset postedAtUtc) =>
        JournalEntry.Post(
            entryDate,
            $"Instalment - {loanNumber}",
            sourceDocument,
            postedBy,
            postedAtUtc,
            [
                JournalLine.Debit(unallocatedReceipts, amount),
                JournalLine.Credit(loanReceivable, amount),
            ]);

    /// <summary>
    /// An overpayment refunded to the member by cheque.
    /// </summary>
    /// <remarks>
    /// One of the two sanctioned outcomes for an overpayment; the other is
    /// <see cref="AllocateToShares"/>. Debit Unallocated Receipts, credit Bank - the money
    /// leaves.
    /// </remarks>
    public static JournalEntry RefundOverpayment(
        AccountId unallocatedReceipts,
        AccountId bank,
        Money amount,
        DateOnly entryDate,
        string memberName,
        SourceDocument sourceDocument,
        Actor postedBy,
        DateTimeOffset postedAtUtc) =>
        JournalEntry.Post(
            entryDate,
            $"Refund of overpayment - {memberName}",
            sourceDocument,
            postedBy,
            postedAtUtc,
            [
                JournalLine.Debit(unallocatedReceipts, amount),
                JournalLine.Credit(bank, amount),
            ]);

    /// <summary>
    /// A loan has been disbursed.
    /// </summary>
    /// <remarks>
    /// Three lines, because the interest is charged once at disbursement rather than accruing:
    /// the borrower owes principal plus interest, the bank pays out the principal, and the
    /// difference is recognised as income immediately. That is what the flat 10% means in
    /// accounting terms, and it is why a loan's receivable is one balance rather than a
    /// principal and an interest component tracked separately.
    /// </remarks>
    public static JournalEntry Disbursement(
        AccountId loanReceivable,
        AccountId bank,
        AccountId interestIncome,
        Money principal,
        Money interest,
        DateOnly disbursedOn,
        string loanNumber,
        SourceDocument sourceDocument,
        Actor postedBy,
        DateTimeOffset postedAtUtc)
    {
        JournalLine[] lines = interest.IsZero
            // A restructured balance carries no fresh interest, so there is no income line.
            ? [
                JournalLine.Debit(loanReceivable, principal),
                JournalLine.Credit(bank, principal),
            ]
            : [
                JournalLine.Debit(loanReceivable, principal + interest, "Principal and interest"),
                JournalLine.Credit(bank, principal, "Cheque drawn"),
                JournalLine.Credit(interestIncome, interest, "Flat interest charged at disbursement"),
            ];

        return JournalEntry.Post(
            disbursedOn,
            $"Disbursement - {loanNumber}",
            sourceDocument,
            postedBy,
            postedAtUtc,
            lines);
    }

    /// <summary>
    /// The 5% restructuring fee, deducted upfront.
    /// </summary>
    /// <remarks>
    /// Debit Unallocated Receipts, credit Restructuring Fees. The fee is paid before the
    /// restructured instalments start and is not rolled into the balance.
    /// </remarks>
    public static JournalEntry RestructuringFee(
        AccountId unallocatedReceipts,
        AccountId restructuringFees,
        Money fee,
        DateOnly entryDate,
        string loanNumber,
        SourceDocument sourceDocument,
        Actor postedBy,
        DateTimeOffset postedAtUtc) =>
        JournalEntry.Post(
            entryDate,
            $"Restructuring fee - {loanNumber}",
            sourceDocument,
            postedBy,
            postedAtUtc,
            [
                JournalLine.Debit(unallocatedReceipts, fee),
                JournalLine.Credit(restructuringFees, fee),
            ]);

    /// <summary>
    /// Closes a loan's balance into the loan that replaces it.
    /// </summary>
    /// <remarks>
    /// <b>The original loan is never mutated.</b> Its receivable is credited down to zero and
    /// the new loan's receivable is debited by the same amount, so both loans' histories stay
    /// readable and a member can see exactly what happened to what they owed.
    /// </remarks>
    public static JournalEntry CarryBalanceIntoRestructure(
        AccountId originalReceivable,
        AccountId replacementReceivable,
        Money balance,
        DateOnly entryDate,
        string originalLoanNumber,
        string replacementLoanNumber,
        Actor postedBy,
        DateTimeOffset postedAtUtc) =>
        JournalEntry.Post(
            entryDate,
            $"Restructure - {originalLoanNumber} carried into {replacementLoanNumber}",
            SourceDocument.Of(SourceDocumentKind.Correction, originalLoanNumber),
            postedBy,
            postedAtUtc,
            [
                JournalLine.Debit(replacementReceivable, balance, $"Balance from {originalLoanNumber}"),
                JournalLine.Credit(originalReceivable, balance, $"Carried into {replacementLoanNumber}"),
            ]);

    /// <summary>
    /// A bank charge.
    /// </summary>
    /// <remarks>
    /// Debit Bank Charges, credit Bank. These matter beyond bookkeeping: the account earns no
    /// interest but incurs charges, and the charges are deducted when working out the interest
    /// earned that the dividend is calculated from.
    /// </remarks>
    public static JournalEntry BankCharge(
        AccountId bankCharges,
        AccountId bank,
        Money amount,
        DateOnly entryDate,
        string narration,
        SourceDocument sourceDocument,
        Actor postedBy,
        DateTimeOffset postedAtUtc) =>
        JournalEntry.Post(
            entryDate,
            narration,
            sourceDocument,
            postedBy,
            postedAtUtc,
            [
                JournalLine.Debit(bankCharges, amount),
                JournalLine.Credit(bank, amount),
            ]);

    /// <summary>
    /// An opening balance at go-live.
    /// </summary>
    /// <remarks>
    /// Every opening balance is posted against Opening Balance Equity, and the whole set must
    /// net to zero - which is what proves the migration was complete rather than partial. The
    /// migration tooling refuses to commit if it does not.
    /// </remarks>
    public static JournalEntry OpeningBalance(
        AccountId account,
        AccountId openingBalanceEquity,
        Money amount,
        BalanceSide side,
        DateOnly goLiveDate,
        string narration,
        Actor postedBy,
        DateTimeOffset postedAtUtc)
    {
        JournalLine[] lines = side == BalanceSide.Debit
            ? [
                JournalLine.Debit(account, amount),
                JournalLine.Credit(openingBalanceEquity, amount),
            ]
            : [
                JournalLine.Debit(openingBalanceEquity, amount),
                JournalLine.Credit(account, amount),
            ];

        return JournalEntry.Post(
            goLiveDate,
            narration,
            SourceDocument.Of(SourceDocumentKind.OpeningBalance, goLiveDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            postedBy,
            postedAtUtc,
            lines);
    }
}
