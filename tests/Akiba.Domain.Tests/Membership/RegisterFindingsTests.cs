using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Domain.Membership;
using Akiba.Domain.Tests.Ledger;

namespace Akiba.Domain.Tests.Membership;

/// <summary>
/// Rules taken from the society's own deduction register (December 2025 to June 2026).
/// </summary>
/// <remarks>
/// These are not hypotheticals. Each one is something the real file does that Akiba either
/// got wrong or had never been told about, and each would have shown up as a failed import
/// rather than as a bug report.
/// </remarks>
public sealed class RegisterFindingsTests
{
    [Fact]
    public void A_shareholder_need_not_be_on_the_CAL_payroll()
    {
        // Two shareholders in the register share staff number 0, which is the office's way of
        // writing "not on the payroll". Requiring a payroll number would have refused to load
        // the society's own records.
        var member = Member.Join(
            MembershipNumber.Of("0001"),
            PayrollNumber.None,
            new PersonName("Francis", "Chabari"),
            NationalId.Of("23456781"),
            PhoneNumber.Of("0712345001"),
            null,
            ZoneId.New(),
            AccountId.New());

        member.IsOnPayroll.Should().BeFalse();
        member.PayrollNumber.IsSpecified.Should().BeFalse();
        member.PayrollNumber.ToString().Should().Be("(not on payroll)");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-")]
    [InlineData("N/A")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void The_registers_stand_ins_for_no_payroll_number_read_as_absent(string? written)
    {
        // Taken literally, "0" against two people is a duplicate key. Taken as the office means
        // it, it is a blank.
        PayrollNumber.FromRegister(written).IsSpecified.Should().BeFalse();
    }

    [Fact]
    public void A_real_payroll_number_still_reads_as_present()
    {
        var payroll = PayrollNumber.FromRegister(" cal/0042 ");

        payroll.IsSpecified.Should().BeTrue();
        payroll.Value.Should().Be("CAL/0042");
    }

    [Fact]
    public void Members_choose_their_own_monthly_contribution()
    {
        // Open question 10, answered by the register: the amounts in use are 1,000 through
        // 25,000 and steady per member, not one society-wide figure. Nothing in Akiba assumes
        // a fixed amount, and this test is here so nothing starts to.
        decimal[] amountsInUse = [1_000m, 1_500m, 2_000m, 2_500m, 3_000m, 4_000m, 5_000m, 10_000m, 25_000m];

        var shares = LedgerFixture.SharesOf("Lucy Wanjiru Karanja", Guid.NewGuid());

        var ledger = amountsInUse
            .Select((amount, index) => LedgerFixture.ShareContribution(
                shares, amount, new DateOnly(2026, index + 1, 28)))
            .ToArray();

        Shareholding.AsAt(ledger, shares, new DateOnly(2026, 12, 31))
            .Should().Be(Money.Kes(amountsInUse.Sum()));
    }

    [Fact]
    public void The_largest_shareholdings_in_the_register_sit_above_the_scales_top_band()
    {
        // The biggest holding in the register is 2,355,000, which is a borrowing limit of
        // 4,710,000. The 400,001+ band is not an edge case for this society - it is where its
        // largest members are.
        var shareholding = Money.Kes(2_355_000m);

        Shareholding.BorrowingLimit(shareholding).Should().Be(Money.Kes(4_710_000m));
        Shareholding.IsWithinBorrowingLimit(shareholding, Money.Kes(4_710_000m)).Should().BeTrue();
    }

    [Fact]
    public void Summing_the_register_in_decimal_gives_a_whole_number()
    {
        // Every shareholding in the register is a whole number of shillings, yet every column
        // total in the spreadsheet ends .734195583 - a fractional tail that cannot come from
        // the data.
        decimal[] holdings = [1_979_000m, 2_355_000m, 1_266_000m, 170_224m, 451_000m, 388_540m, 512_003m];

        var exact = holdings.Select(Money.Kes).Sum(Currency.Kes);

        exact.Amount.Should().Be(7_121_767m);
        decimal.Truncate(exact.Amount).Should().Be(
            exact.Amount, because: "a sum of whole shillings has no fractional part");
    }

    [Fact]
    public void Binary_floating_point_cannot_hold_a_shilling_and_a_decimal_can()
    {
        // Why the register's totals drift, in two lines. This is the canonical demonstration
        // rather than a reconstruction of their exact figure - the point is the type, not the
        // arithmetic that happened to expose it.
        var tenTenthsAsDouble = Enumerable.Repeat(0.1d, 10).Sum();
        var tenTenthsAsDecimal = Enumerable.Repeat(0.1m, 10).Sum();

        tenTenthsAsDouble.Should().NotBe(1.0d, because: "0.1 has no exact binary representation");
        tenTenthsAsDecimal.Should().Be(1.0m, because: "decimal is base-10 and holds it exactly");

        // And the same in Money, which is what Akiba actually posts with.
        Enumerable.Repeat(Money.Kes(0.10m), 10).Sum(Currency.Kes).Should().Be(Money.Kes(1.00m));
    }
}
