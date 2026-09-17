using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Infrastructure.Persistence.Rows;

namespace Akiba.Infrastructure.Persistence;

/// <summary>
/// Translates between the stored rows and the domain aggregates.
/// </summary>
/// <remarks>
/// Rehydration goes through each aggregate's <c>Rehydrate</c> factory, which runs the same
/// invariant checks as construction. That is the point of doing it this way: a stored journal
/// entry whose lines no longer sum to zero throws when it is read, rather than silently
/// producing a wrong trial balance months later.
/// </remarks>
internal static class LedgerMapper
{
    public static AccountRow ToRow(Account account) => new()
    {
        Id = account.Id.Value,
        Code = account.Code.Value,
        Name = account.Name,
        Type = (int)account.Type,
        OwnerKind = (int)account.Owner.Kind,
        OwnerId = account.Owner.OwnerId,
        IsOpen = account.IsOpen,
        ClosedOn = account.ClosedOn,
    };

    public static Account ToDomain(AccountRow row) => Account.Rehydrate(
        new AccountId(row.Id),
        AccountCode.Of(row.Code),
        row.Name,
        (AccountType)row.Type,
        new AccountOwner((AccountOwnerKind)row.OwnerKind, row.OwnerId),
        row.IsOpen,
        row.ClosedOn);

    public static JournalEntryRow ToRow(JournalEntry entry)
    {
        var row = new JournalEntryRow
        {
            Id = entry.Id.Value,
            EntryDate = entry.EntryDate,
            ValueDate = entry.ValueDate,
            Narration = entry.Narration,
            SourceDocumentKind = (int)entry.SourceDocument.Kind,
            SourceDocumentReference = entry.SourceDocument.Reference,
            PostedByUserId = entry.PostedBy.UserId,
            PostedByName = entry.PostedBy.DisplayName,
            PostedAtUtc = entry.PostedAtUtc,
            ReversesEntryId = entry.Reverses?.Value,
        };

        row.Lines =
        [
            .. entry.Lines.Select((line, index) => new JournalLineRow
            {
                Id = Guid.NewGuid(),
                JournalEntryId = entry.Id.Value,
                AccountId = line.AccountId.Value,
                SignedAmount = line.SignedAmount.Amount,
                CurrencyCode = line.SignedAmount.Currency.Code,
                Narration = line.Narration,
                Sequence = index,
            }),
        ];

        return row;
    }

    public static JournalEntry ToDomain(JournalEntryRow row)
    {
        var lines = row.Lines
            .OrderBy(line => line.Sequence)
            .Select(line => JournalLine.Rehydrate(
                new AccountId(line.AccountId),
                new Money(line.SignedAmount, Currency.Of(line.CurrencyCode)),
                line.Narration))
            .ToList();

        return JournalEntry.Rehydrate(
            new JournalEntryId(row.Id),
            row.EntryDate,
            row.ValueDate,
            row.Narration,
            SourceDocument.Of((SourceDocumentKind)row.SourceDocumentKind, row.SourceDocumentReference),
            new Actor(row.PostedByUserId, row.PostedByName),
            row.PostedAtUtc,
            lines,
            row.ReversesEntryId is { } reverses ? new JournalEntryId(reverses) : null);
    }

    public static AccountingPeriodRow ToRow(AccountingPeriod period) => new()
    {
        Id = period.Id.Value,
        Kind = (int)period.Kind,
        Start = period.Start,
        End = period.End,
        Status = (int)period.Status,
        ClosedByUserId = period.ClosedBy?.UserId,
        ClosedByName = period.ClosedBy?.DisplayName,
        ClosedAtUtc = period.ClosedAtUtc,
        ReopenedByUserId = period.ReopenedBy?.UserId,
        ReopenedByName = period.ReopenedBy?.DisplayName,
        ReopenedReason = period.ReopenedReason,
        ReopenedAtUtc = period.ReopenedAtUtc,
    };

    public static void CopyInto(AccountingPeriod period, AccountingPeriodRow row)
    {
        row.Status = (int)period.Status;
        row.ClosedByUserId = period.ClosedBy?.UserId;
        row.ClosedByName = period.ClosedBy?.DisplayName;
        row.ClosedAtUtc = period.ClosedAtUtc;
        row.ReopenedByUserId = period.ReopenedBy?.UserId;
        row.ReopenedByName = period.ReopenedBy?.DisplayName;
        row.ReopenedReason = period.ReopenedReason;
        row.ReopenedAtUtc = period.ReopenedAtUtc;
    }

    public static AccountingPeriod ToDomain(AccountingPeriodRow row) => AccountingPeriod.Rehydrate(
        new AccountingPeriodId(row.Id),
        (AccountingPeriodKind)row.Kind,
        row.Start,
        row.End,
        (AccountingPeriodStatus)row.Status,
        ToActor(row.ClosedByUserId, row.ClosedByName),
        row.ClosedAtUtc,
        ToActor(row.ReopenedByUserId, row.ReopenedByName),
        row.ReopenedReason,
        row.ReopenedAtUtc);

    private static Actor? ToActor(Guid? userId, string? name) =>
        userId is { } id && !string.IsNullOrWhiteSpace(name) ? new Actor(id, name) : null;
}
