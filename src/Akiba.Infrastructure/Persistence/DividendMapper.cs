using Akiba.Domain.Common;
using Akiba.Domain.Dividends;
using Akiba.Domain.Financial;
using Akiba.Infrastructure.Persistence.Rows;

namespace Akiba.Infrastructure.Persistence;

/// <summary>
/// Translates between the stored dividend rows and the aggregate.
/// </summary>
/// <remarks>
/// The lines are read back in the order they were computed in. That order is the allocation
/// order, and the allocation hands its leftover cents out by it - so a run rehydrated in a
/// different order would be a different run by a few cents.
/// </remarks>
internal static class DividendMapper
{
    public static DividendRunRow ToRow(DividendRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var row = new DividendRunRow
        {
            Id = run.Id.Value,
            Year = run.Year,
            InterestEarned = run.InterestEarned.Amount,
            BankCharges = run.BankCharges.Amount,
            CurrencyCode = run.InterestEarned.Currency.Code,
            BasisName = run.BasisName,
            BasisExplanation = run.BasisExplanation,
            Status = (int)run.Status,
            ComputedByUserId = run.ComputedBy?.UserId,
            ComputedByName = run.ComputedBy?.DisplayName,
            ComputedAtUtc = run.ComputedAtUtc,
            ReviewedByUserId = run.ReviewedBy?.UserId,
            ReviewedByName = run.ReviewedBy?.DisplayName,
            ReviewedAtUtc = run.ReviewedAtUtc,
            ApprovedByUserId = run.ApprovedBy?.UserId,
            ApprovedByName = run.ApprovedBy?.DisplayName,
            ApprovedAtUtc = run.ApprovedAtUtc,
            PostedOn = run.PostedOn,
            PostedAtUtc = run.PostedAtUtc,
            WithdrawnReason = run.WithdrawnReason,
        };

        row.Lines =
        [
            .. run.Lines.Select((line, index) => new DividendLineRow
            {
                Id = Guid.NewGuid(),
                DividendRunId = run.Id.Value,
                MemberId = line.MemberId,
                MembershipNumber = line.MembershipNumber,
                FullName = line.FullName,
                SharesAccountId = line.SharesAccountId,
                BasisAmount = line.BasisAmount.Amount,
                Amount = line.Amount.Amount,
                CurrencyCode = line.Amount.Currency.Code,
                Sequence = index,
            }),
        ];

        return row;
    }

    public static DividendRun ToDomain(DividendRunRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var currency = Currency.Of(row.CurrencyCode);

        return DividendRun.Rehydrate(
            new DividendRunId(row.Id),
            row.Year,
            new Money(row.InterestEarned, currency),
            new Money(row.BankCharges, currency),
            row.BasisName,
            row.BasisExplanation,
            (DividendRunStatus)row.Status,
            ToActor(row.ComputedByUserId, row.ComputedByName),
            row.ComputedAtUtc,
            ToActor(row.ReviewedByUserId, row.ReviewedByName),
            row.ReviewedAtUtc,
            ToActor(row.ApprovedByUserId, row.ApprovedByName),
            row.ApprovedAtUtc,
            row.PostedOn,
            row.PostedAtUtc,
            row.WithdrawnReason,
            row.Lines
                .OrderBy(line => line.Sequence)
                .Select(line => new DividendLine(
                    line.MemberId,
                    line.MembershipNumber,
                    line.FullName,
                    line.SharesAccountId,
                    new Money(line.BasisAmount, Currency.Of(line.CurrencyCode)),
                    new Money(line.Amount, Currency.Of(line.CurrencyCode)))));
    }

    private static Actor? ToActor(Guid? userId, string? name) =>
        userId is { } id && !string.IsNullOrWhiteSpace(name) ? new Actor(id, name) : null;
}
