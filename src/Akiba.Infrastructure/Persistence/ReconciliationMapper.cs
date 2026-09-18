using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Domain.Reconciliation;
using Akiba.Infrastructure.Persistence.Rows;

namespace Akiba.Infrastructure.Persistence;

/// <summary>
/// Translates between the stored reconciliation rows and the aggregate.
/// </summary>
/// <remarks>
/// A statement line's amount is stored positive with its direction beside it, the way a bank
/// prints one. The signed figure the arithmetic uses is derived from the pair on read, so
/// there is no stored sign to drift out of step with the direction.
/// </remarks>
internal static class ReconciliationMapper
{
    public static BankReconciliationRow ToRow(BankReconciliation reconciliation)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);

        var row = new BankReconciliationRow
        {
            Id = reconciliation.Id.Value,
            BankAccountId = reconciliation.BankAccountId.Value,
            AccountLabel = reconciliation.AccountLabel,
            From = reconciliation.From,
            To = reconciliation.To,
            OpeningBalance = reconciliation.OpeningBalance.Amount,
            ClosingBalance = reconciliation.ClosingBalance.Amount,
            CurrencyCode = reconciliation.ClosingBalance.Currency.Code,
            Status = (int)reconciliation.Status,
            SignedOffByUserId = reconciliation.SignedOffBy?.UserId,
            SignedOffByName = reconciliation.SignedOffBy?.DisplayName,
            SignedOffAtUtc = reconciliation.SignedOffAtUtc,
        };

        row.Lines =
        [
            .. reconciliation.Lines.Select(line => new BankStatementLineRow
            {
                Id = line.Id,
                BankReconciliationId = reconciliation.Id.Value,
                LineNumber = line.LineNumber,
                ValueDate = line.ValueDate,
                Description = line.Description,
                Amount = line.Amount.Amount,
                CurrencyCode = line.Amount.Currency.Code,
                Direction = (int)line.Direction,
                BankReference = line.BankReference,
                State = (int)line.State,
                MatchedToId = line.MatchedToId,
                MatchedToDescription = line.MatchedToDescription,
                NotOursReason = line.NotOursReason,
                DecidedByUserId = line.DecidedBy?.UserId,
                DecidedByName = line.DecidedBy?.DisplayName,
            }),
        ];

        return row;
    }

    public static BankReconciliation ToDomain(BankReconciliationRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var currency = Currency.Of(row.CurrencyCode);

        return BankReconciliation.Rehydrate(
            new BankStatementId(row.Id),
            new AccountId(row.BankAccountId),
            row.AccountLabel,
            row.From,
            row.To,
            new Money(row.OpeningBalance, currency),
            new Money(row.ClosingBalance, currency),
            (ReconciliationStatus)row.Status,
            ToActor(row.SignedOffByUserId, row.SignedOffByName),
            row.SignedOffAtUtc,
            row.Lines
                .OrderBy(line => line.LineNumber)
                .Select(line => BankStatementLine.Rehydrate(
                    line.Id,
                    line.LineNumber,
                    line.ValueDate,
                    line.Description,
                    new Money(line.Amount, Currency.Of(line.CurrencyCode)),
                    (StatementDirection)line.Direction,
                    line.BankReference,
                    (MatchState)line.State,
                    line.MatchedToId,
                    line.MatchedToDescription,
                    line.NotOursReason,
                    ToActor(line.DecidedByUserId, line.DecidedByName))));
    }

    private static Actor? ToActor(Guid? userId, string? name) =>
        userId is { } id && !string.IsNullOrWhiteSpace(name) ? new Actor(id, name) : null;
}
