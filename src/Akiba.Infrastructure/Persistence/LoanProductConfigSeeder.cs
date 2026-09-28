using Akiba.Domain.Lending;
using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Akiba.Infrastructure.Persistence;

/// <summary>
/// Seeds the loan product configuration tables on first run, using the values the committee
/// approved on 6 September 2026.
/// </summary>
/// <remarks>
/// Idempotent: skips any product whose row already exists. A subsequent committee decision is
/// recorded through the <see cref="Application.Abstractions.ILoanProductConfigRepository"/>
/// upsert, not by re-running this seeder.
/// </remarks>
public sealed class LoanProductConfigSeeder
{
    private static readonly DateOnly CommitteeDate = new(2026, 9, 6);

    private readonly AkibaDbContext _context;
    private readonly ILogger<LoanProductConfigSeeder> _logger;

    public LoanProductConfigSeeder(
        AkibaDbContext context,
        ILogger<LoanProductConfigSeeder> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<int> SeedAsync(CancellationToken cancellationToken = default)
    {
        var seeded = 0;
        seeded += await SeedProductsAsync(cancellationToken).ConfigureAwait(false);
        seeded += await SeedScaleBandsAsync(cancellationToken).ConfigureAwait(false);
        return seeded;
    }

    private async Task<int> SeedProductsAsync(CancellationToken cancellationToken)
    {
        var existingProducts = await _context.Set<LoanProductConfigRow>()
            .Select(r => r.Product)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var seeds = ProductSeeds().Where(s => !existingProducts.Contains(s.Product)).ToList();

        if (seeds.Count == 0)
        {
            return 0;
        }

        foreach (var seed in seeds)
        {
            _context.Set<LoanProductConfigRow>().Add(seed);
            _logger.LogInformation(
                "Seeded loan product config for {Product}", (LoanProduct)seed.Product);
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return seeds.Count;
    }

    private async Task<int> SeedScaleBandsAsync(CancellationToken cancellationToken)
    {
        var alreadySeeded = await _context.Set<TermScaleBandRow>()
            .AnyAsync(b => b.ScaleVersion == 1, cancellationToken)
            .ConfigureAwait(false);

        if (alreadySeeded)
        {
            return 0;
        }

        var bands = TermScaleSeeds().ToList();

        foreach (var band in bands)
        {
            _context.Set<TermScaleBandRow>().Add(band);
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Seeded {Count} graduated term scale bands (version 1)", bands.Count);

        return bands.Count;
    }

    private static IEnumerable<LoanProductConfigRow> ProductSeeds() =>
    [
        new LoanProductConfigRow
        {
            Product = (int)LoanProduct.Normal,
            InterestRate = 0.10m,
            RateIsMonthly = false,
            MaxPrincipalKes = null,
            FixedTermMonths = null,
            MaximumTermMonths = null,
            UsesGraduatedScale = true,
            IsConfigured = true,
            EffectiveFrom = CommitteeDate,
        },
        new LoanProductConfigRow
        {
            Product = (int)LoanProduct.Emergency,
            InterestRate = 0.10m,
            RateIsMonthly = false,
            MaxPrincipalKes = 25_000m,
            FixedTermMonths = 5,
            MaximumTermMonths = 5,
            UsesGraduatedScale = false,
            IsConfigured = true,
            EffectiveFrom = CommitteeDate,
        },
        new LoanProductConfigRow
        {
            Product = (int)LoanProduct.Client,
            InterestRate = 0.03m,
            RateIsMonthly = true,
            MaxPrincipalKes = null,
            FixedTermMonths = null,
            MaximumTermMonths = 5,
            UsesGraduatedScale = false,
            IsConfigured = true,
            EffectiveFrom = CommitteeDate,
        },
        new LoanProductConfigRow
        {
            Product = (int)LoanProduct.RentalIncome,
            InterestRate = 0.03m,
            RateIsMonthly = true,
            MaxPrincipalKes = null,
            FixedTermMonths = null,
            MaximumTermMonths = 5,
            UsesGraduatedScale = false,
            IsConfigured = true,
            EffectiveFrom = CommitteeDate,
        },
    ];

    private static IEnumerable<TermScaleBandRow> TermScaleSeeds() =>
    [
        new TermScaleBandRow { ScaleVersion = 1, EffectiveFrom = CommitteeDate, MinPrincipalKes = 30_000m,       MaxPrincipalKes = 50_000m,    MaxTermMonths = 8 },
        new TermScaleBandRow { ScaleVersion = 1, EffectiveFrom = CommitteeDate, MinPrincipalKes = 50_000.01m,    MaxPrincipalKes = 100_000m,   MaxTermMonths = 12 },
        new TermScaleBandRow { ScaleVersion = 1, EffectiveFrom = CommitteeDate, MinPrincipalKes = 100_000.01m,   MaxPrincipalKes = 150_000m,   MaxTermMonths = 16 },
        new TermScaleBandRow { ScaleVersion = 1, EffectiveFrom = CommitteeDate, MinPrincipalKes = 150_000.01m,   MaxPrincipalKes = 200_000m,   MaxTermMonths = 20 },
        new TermScaleBandRow { ScaleVersion = 1, EffectiveFrom = CommitteeDate, MinPrincipalKes = 200_000.01m,   MaxPrincipalKes = 400_000m,   MaxTermMonths = 24 },
        new TermScaleBandRow { ScaleVersion = 1, EffectiveFrom = CommitteeDate, MinPrincipalKes = 400_000.01m,   MaxPrincipalKes = null,       MaxTermMonths = 30 },
    ];
}
