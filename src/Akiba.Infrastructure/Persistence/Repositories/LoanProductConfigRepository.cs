using Akiba.Application.Abstractions;
using Akiba.Domain.Financial;
using Akiba.Domain.Lending;
using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Akiba.Infrastructure.Persistence.Repositories;

internal sealed class LoanProductConfigRepository : ILoanProductConfigRepository
{
    private readonly AkibaDbContext _context;

    public LoanProductConfigRepository(AkibaDbContext context) => _context = context;

    public async Task<IReadOnlyList<LoanProductDefinitionWithDate>> AllDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.Set<LoanProductConfigRow>()
            .AsNoTracking()
            .OrderBy(r => r.Product)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToDefinitionWithDate).ToList();
    }

    public async Task<GraduatedTermScale> CurrentScaleAsync(
        CancellationToken cancellationToken = default)
    {
        var maxVersion = await _context.Set<TermScaleBandRow>()
            .AsNoTracking()
            .MaxAsync(b => (int?)b.ScaleVersion, cancellationToken)
            .ConfigureAwait(false);

        if (maxVersion is null)
        {
            return GraduatedTermScale.Version1;
        }

        var bands = await _context.Set<TermScaleBandRow>()
            .AsNoTracking()
            .Where(b => b.ScaleVersion == maxVersion)
            .OrderBy(b => b.MinPrincipalKes)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (bands.Count == 0)
        {
            return GraduatedTermScale.Version1;
        }

        var effectiveFrom = bands[0].EffectiveFrom;

        var termBands = bands.Select(b => new TermBand(
            Money.Kes(b.MinPrincipalKes),
            b.MaxPrincipalKes.HasValue ? Money.Kes(b.MaxPrincipalKes.Value) : null,
            b.MaxTermMonths)).ToList();

        return GraduatedTermScale.NewVersion(maxVersion.Value, effectiveFrom, termBands);
    }

    public async Task UpsertProductAsync(
        LoanProduct product,
        decimal interestRate,
        bool rateIsMonthly,
        decimal? maxPrincipalKes,
        int? fixedTermMonths,
        int? maximumTermMonths,
        DateOnly effectiveFrom,
        CancellationToken cancellationToken = default)
    {
        var existing = await _context.Set<LoanProductConfigRow>()
            .FirstOrDefaultAsync(r => r.Product == (int)product, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _context.Set<LoanProductConfigRow>().Add(new LoanProductConfigRow
            {
                Product = (int)product,
                InterestRate = interestRate,
                RateIsMonthly = rateIsMonthly,
                MaxPrincipalKes = maxPrincipalKes,
                FixedTermMonths = fixedTermMonths,
                MaximumTermMonths = maximumTermMonths,
                UsesGraduatedScale = product == LoanProduct.Normal,
                IsConfigured = true,
                EffectiveFrom = effectiveFrom,
            });
        }
        else
        {
            existing.InterestRate = interestRate;
            existing.RateIsMonthly = rateIsMonthly;
            existing.MaxPrincipalKes = maxPrincipalKes;
            existing.FixedTermMonths = fixedTermMonths;
            existing.MaximumTermMonths = maximumTermMonths;
            existing.IsConfigured = true;
            existing.EffectiveFrom = effectiveFrom;
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static LoanProductDefinitionWithDate ToDefinitionWithDate(LoanProductConfigRow row)
    {
        var product = (LoanProduct)row.Product;

        IInterestStrategy strategy = row.IsConfigured
            ? (row.RateIsMonthly
                ? new MonthlyRateInterestStrategy(row.InterestRate)
                : new FlatRateInterestStrategy(row.InterestRate))
            : new UnconfiguredInterestStrategy(product, "loan_product_configs.is_configured = false");

        var definition = new LoanProductDefinition(
            product,
            strategy,
            row.MaxPrincipalKes.HasValue ? Money.Kes(row.MaxPrincipalKes.Value) : null,
            row.FixedTermMonths,
            row.MaximumTermMonths,
            row.UsesGraduatedScale);

        return new LoanProductDefinitionWithDate(definition, row.EffectiveFrom);
    }
}
