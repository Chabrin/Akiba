using Akiba.Domain.Lending;
using Akiba.Domain.Ledger;
using Akiba.Infrastructure.Persistence.Rows;

namespace Akiba.Infrastructure.Persistence;

internal static class ChequeBookMapper
{
    public static ChequeBookRow ToRow(ChequeBook book) => new()
    {
        Id = book.Id.Value,
        BookReference = book.BookReference,
        BankAccountId = book.BankAccountId.Value,
        ReceivedOn = book.ReceivedOn,
        IsClosed = book.IsClosed,
        Revision = book.Revision,
        Leaves = [.. book.Leaves.Select(leaf => new ChequeLeafRow
        {
            Id = leaf.Id.Value,
            ChequeBookId = book.Id.Value,
            BankAccountId = book.BankAccountId.Value,
            Number = leaf.Number,
            Status = (int)leaf.Status,
            ReservedForApplicationId = leaf.ReservedForApplication?.Value,
            IssuedForLoanId = leaf.IssuedForLoan?.Value,
            VoidReason = leaf.VoidReason,
        })],
    };

    public static ChequeBook ToDomain(ChequeBookRow row) => ChequeBook.Rehydrate(
        new ChequeBookId(row.Id),
        row.BookReference,
        new AccountId(row.BankAccountId),
        row.ReceivedOn,
        row.Leaves.Select(leaf => ChequeLeaf.Rehydrate(
            new ChequeLeafId(leaf.Id),
            leaf.Number,
            (ChequeLeafStatus)leaf.Status,
            leaf.ReservedForApplicationId is { } applicationId
                ? new LoanApplicationId(applicationId)
                : null,
            leaf.IssuedForLoanId is { } loanId ? new LoanId(loanId) : null,
            leaf.VoidReason)),
        row.IsClosed,
        row.Revision);
}
