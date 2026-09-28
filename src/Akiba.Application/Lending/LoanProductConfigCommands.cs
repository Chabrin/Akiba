using Akiba.Application.Abstractions;
using Akiba.Domain.Financial;
using Akiba.Domain.Lending;
using FluentValidation;
using MediatR;

namespace Akiba.Application.Lending;

/// <summary>A loan product's configuration as shown on the settings page.</summary>
public sealed record LoanProductConfig(
    LoanProduct Product,
    string ProductName,
    decimal InterestRate,
    bool RateIsMonthly,
    Money? MaxPrincipal,
    int? FixedTermMonths,
    int? MaximumTermMonths,
    bool UsesGraduatedScale,
    bool IsConfigured,
    DateOnly EffectiveFrom);

/// <summary>One band of the graduated term scale, for display.</summary>
public sealed record TermScaleBandSetting(
    Money From,
    Money? To,
    int MaxTermMonths);

/// <summary>Returns every product's configuration and the current graduated scale.</summary>
public sealed record ListLoanProductConfigsQuery
    : IRequest<(IReadOnlyList<LoanProductConfig> Products, IReadOnlyList<TermScaleBandSetting> Scale)>;

internal sealed class ListLoanProductConfigsHandler
    : IRequestHandler<ListLoanProductConfigsQuery,
        (IReadOnlyList<LoanProductConfig> Products, IReadOnlyList<TermScaleBandSetting> Scale)>
{
    private readonly ILoanProductConfigRepository _configs;

    public ListLoanProductConfigsHandler(ILoanProductConfigRepository configs) =>
        _configs = configs;

    public async Task<(IReadOnlyList<LoanProductConfig> Products, IReadOnlyList<TermScaleBandSetting> Scale)>
        Handle(ListLoanProductConfigsQuery query, CancellationToken cancellationToken)
    {
        var rows = await _configs.AllDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        var scale = await _configs.CurrentScaleAsync(cancellationToken).ConfigureAwait(false);

        var products = rows
            .OrderBy(r => (int)r.Definition.Product)
            .Select(r => new LoanProductConfig(
                r.Definition.Product,
                r.Definition.Product.DisplayName(),
                RateOf(r.Definition),
                IsMonthly(r.Definition),
                r.Definition.MaximumPrincipal,
                r.Definition.FixedTermMonths,
                r.Definition.MaximumTermMonths,
                r.Definition.UsesGraduatedScale,
                r.Definition.InterestStrategy is not UnconfiguredInterestStrategy,
                r.EffectiveFrom))
            .ToList();

        var bands = scale.Bands
            .Select(b => new TermScaleBandSetting(b.From, b.To, b.Months))
            .ToList();

        return (products, bands);
    }

    private static decimal RateOf(LoanProductDefinition d) => d.InterestStrategy switch
    {
        FlatRateInterestStrategy flat => flat.Rate,
        MonthlyRateInterestStrategy monthly => monthly.MonthlyRate,
        _ => 0m,
    };

    private static bool IsMonthly(LoanProductDefinition d) =>
        d.InterestStrategy is MonthlyRateInterestStrategy;
}

/// <summary>Updates a loan product's interest rate and limits.</summary>
/// <remarks>
/// The committee minute date is recorded as <see cref="EffectiveFrom"/> so changes can be
/// traced back to the decision that authorised them.
/// </remarks>
public sealed record UpdateLoanProductConfigCommand(
    LoanProduct Product,
    decimal InterestRate,
    bool RateIsMonthly,
    decimal? MaxPrincipalKes,
    int? FixedTermMonths,
    int? MaximumTermMonths,
    DateOnly EffectiveFrom) : IRequest;

internal sealed class UpdateLoanProductConfigValidator
    : AbstractValidator<UpdateLoanProductConfigCommand>
{
    public UpdateLoanProductConfigValidator()
    {
        RuleFor(c => c.InterestRate)
            .GreaterThan(0).WithMessage("Interest rate must be greater than zero.")
            .LessThanOrEqualTo(1m).WithMessage("Interest rate must not exceed 100%.");

        RuleFor(c => c.MaxPrincipalKes)
            .GreaterThan(0).When(c => c.MaxPrincipalKes.HasValue)
            .WithMessage("Maximum principal must be positive.");

        RuleFor(c => c.FixedTermMonths)
            .InclusiveBetween(1, 360).When(c => c.FixedTermMonths.HasValue)
            .WithMessage("Fixed term must be between 1 and 360 months.");

        RuleFor(c => c.MaximumTermMonths)
            .InclusiveBetween(1, 360).When(c => c.MaximumTermMonths.HasValue)
            .WithMessage("Maximum term must be between 1 and 360 months.");

        RuleFor(c => c.EffectiveFrom)
            .LessThanOrEqualTo(DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("Effective date cannot be in the future.");
    }
}

internal sealed class UpdateLoanProductConfigHandler
    : IRequestHandler<UpdateLoanProductConfigCommand>
{
    private readonly ILoanProductConfigRepository _configs;

    public UpdateLoanProductConfigHandler(ILoanProductConfigRepository configs) =>
        _configs = configs;

    public Task Handle(UpdateLoanProductConfigCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _configs.UpsertProductAsync(
            command.Product,
            command.InterestRate,
            command.RateIsMonthly,
            command.MaxPrincipalKes,
            command.FixedTermMonths,
            command.MaximumTermMonths,
            command.EffectiveFrom,
            cancellationToken);
    }
}
