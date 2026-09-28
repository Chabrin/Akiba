using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akiba.Infrastructure.Persistence.Configurations;

internal sealed class LoanProductConfigConfiguration : IEntityTypeConfiguration<LoanProductConfigRow>
{
    public void Configure(EntityTypeBuilder<LoanProductConfigRow> builder)
    {
        builder.ToTable("loan_product_configs");
        builder.HasKey(r => r.Product);

        builder.Property(r => r.InterestRate).HasColumnType("numeric(6,4)");
        builder.Property(r => r.MaxPrincipalKes).HasColumnType(AkibaDbContext.MoneyColumnType);
    }
}

internal sealed class TermScaleBandConfiguration : IEntityTypeConfiguration<TermScaleBandRow>
{
    public void Configure(EntityTypeBuilder<TermScaleBandRow> builder)
    {
        builder.ToTable("term_scale_bands");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id).UseIdentityColumn();
        builder.Property(r => r.MinPrincipalKes).HasColumnType(AkibaDbContext.MoneyColumnType);
        builder.Property(r => r.MaxPrincipalKes).HasColumnType(AkibaDbContext.MoneyColumnType);

        builder.HasIndex(r => new { r.ScaleVersion, r.EffectiveFrom });
    }
}
