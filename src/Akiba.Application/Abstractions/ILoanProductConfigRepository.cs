using Akiba.Domain.Lending;

namespace Akiba.Application.Abstractions;

/// <summary>A definition paired with the committee date it became effective.</summary>
public sealed record LoanProductDefinitionWithDate(
    LoanProductDefinition Definition,
    DateOnly EffectiveFrom);

/// <summary>Reads and writes loan product configuration from the database.</summary>
public interface ILoanProductConfigRepository
{
    /// <summary>
    /// Every product definition, built from the stored configuration, paired with its
    /// committee-approved effective date. Used to construct a <see cref="LoanPricing"/> that
    /// prices from the database rather than from hardcoded constants.
    /// </summary>
    Task<IReadOnlyList<LoanProductDefinitionWithDate>> AllDefinitionsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The graduated term scale currently in force: the highest-versioned set of bands.
    /// </summary>
    Task<GraduatedTermScale> CurrentScaleAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a product's rate and limits. Inserts if new; updates if it already exists.
    /// </summary>
    Task UpsertProductAsync(
        LoanProduct product,
        decimal interestRate,
        bool rateIsMonthly,
        decimal? maxPrincipalKes,
        int? fixedTermMonths,
        int? maximumTermMonths,
        DateOnly effectiveFrom,
        CancellationToken cancellationToken = default);
}
