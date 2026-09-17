using Akiba.Domain.Ledger;

namespace Akiba.Domain.Tests.Ledger;

public sealed class AccountingPeriodTests
{
    private static readonly DateOnly MidSeptember = new(2026, 9, 17);

    [Fact]
    public void A_monthly_period_runs_from_the_first_to_the_last_day()
    {
        var september = AccountingPeriod.ForMonth(MidSeptember);

        september.Start.Should().Be(new DateOnly(2026, 9, 1));
        september.End.Should().Be(new DateOnly(2026, 9, 30));
        september.Contains(new DateOnly(2026, 9, 30)).Should().BeTrue();
        september.Contains(new DateOnly(2026, 10, 1)).Should().BeFalse();
    }

    [Fact]
    public void February_in_a_leap_year_ends_on_the_twenty_ninth()
    {
        AccountingPeriod.ForMonth(new DateOnly(2028, 2, 10)).End
            .Should().Be(new DateOnly(2028, 2, 29));
    }

    [Fact]
    public void Financial_years_follow_the_calendar_year()
    {
        var year = AccountingPeriod.ForYear(MidSeptember);

        year.Start.Should().Be(new DateOnly(2026, 1, 1));
        year.End.Should().Be(new DateOnly(2026, 12, 31));
    }

    [Fact]
    public void The_treasurer_closes_a_period_and_it_records_who_and_when()
    {
        var september = AccountingPeriod.ForMonth(MidSeptember);

        september.Close(LedgerFixture.Treasurer, LedgerFixture.Now);

        september.IsClosed.Should().BeTrue();
        september.ClosedBy.Should().Be(LedgerFixture.Treasurer);
        september.ClosedAtUtc.Should().Be(LedgerFixture.Now);
        september.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<AccountingPeriodClosed>();
    }

    [Fact]
    public void A_period_cannot_be_closed_twice()
    {
        var september = AccountingPeriod.ForMonth(MidSeptember);
        september.Close(LedgerFixture.Treasurer, LedgerFixture.Now);

        var closeAgain = () => september.Close(LedgerFixture.Treasurer, LedgerFixture.Now);

        closeAgain.Should().Throw<InvalidOperationException>()
            .WithMessage("*already closed*");
    }

    [Fact]
    public void Closing_a_period_records_an_actor()
    {
        var september = AccountingPeriod.ForMonth(MidSeptember);

        var close = () => september.Close(default, LedgerFixture.Now);

        close.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reopening_requires_a_reason_and_records_it_permanently()
    {
        // Reopening is the chairman's decision and should be rare. Recording the reason is
        // what keeps it rare.
        var september = AccountingPeriod.ForMonth(MidSeptember);
        september.Close(LedgerFixture.Treasurer, LedgerFixture.Now);

        september.Reopen(
            LedgerFixture.Chairman,
            "September payroll schedule was posted twice",
            LedgerFixture.Now);

        september.IsOpen.Should().BeTrue();
        september.ReopenedBy.Should().Be(LedgerFixture.Chairman);
        september.ReopenedReason.Should().Be("September payroll schedule was posted twice");
        september.DomainEvents.OfType<AccountingPeriodReopened>().Should().ContainSingle();
    }

    [Fact]
    public void Reopening_without_a_reason_is_refused()
    {
        var september = AccountingPeriod.ForMonth(MidSeptember);
        september.Close(LedgerFixture.Treasurer, LedgerFixture.Now);

        var reopen = () => september.Reopen(LedgerFixture.Chairman, "  ", LedgerFixture.Now);

        reopen.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_open_period_cannot_be_reopened()
    {
        var september = AccountingPeriod.ForMonth(MidSeptember);

        var reopen = () => september.Reopen(LedgerFixture.Chairman, "No reason", LedgerFixture.Now);

        reopen.Should().Throw<InvalidOperationException>().WithMessage("*already open*");
    }
}

public sealed class PeriodCalendarTests
{
    [Fact]
    public void A_date_in_a_closed_month_cannot_be_posted_into()
    {
        var september = AccountingPeriod.ForMonth(new DateOnly(2026, 9, 1));
        september.Close(LedgerFixture.Treasurer, LedgerFixture.Now);

        var calendar = new PeriodCalendar([september]);

        var check = () => calendar.EnsureOpenForPosting(new DateOnly(2026, 9, 30));

        check.Should().Throw<ClosedPeriodException>()
            .WithMessage("*Post the correction into the open period instead*");
    }

    [Fact]
    public void A_date_in_an_open_month_posts_freely()
    {
        var september = AccountingPeriod.ForMonth(new DateOnly(2026, 9, 1));
        september.Close(LedgerFixture.Treasurer, LedgerFixture.Now);

        var calendar = new PeriodCalendar([september]);

        calendar.IsOpenForPosting(new DateOnly(2026, 10, 1)).Should().BeTrue();
        calendar.EnsureOpenForPosting(new DateOnly(2026, 10, 1));
    }

    [Fact]
    public void A_date_with_no_period_at_all_is_postable()
    {
        // Periods are created as the ledger reaches them. The absence of one means nobody has
        // closed that month yet, not that posting is forbidden.
        var calendar = new PeriodCalendar([]);

        calendar.IsOpenForPosting(new DateOnly(2026, 9, 17)).Should().BeTrue();
    }

    [Fact]
    public void A_closed_year_closes_every_month_inside_it()
    {
        var year = AccountingPeriod.ForYear(new DateOnly(2025, 1, 1));
        year.Close(LedgerFixture.Treasurer, LedgerFixture.Now);

        var calendar = new PeriodCalendar([year]);

        calendar.IsOpenForPosting(new DateOnly(2025, 7, 14)).Should().BeFalse();
    }

    [Fact]
    public void The_error_names_the_narrowest_closed_period_containing_the_date()
    {
        // "September is closed" is more use to an official than "2026 is closed", even when
        // both are true.
        var year = AccountingPeriod.ForYear(new DateOnly(2026, 1, 1));
        var september = AccountingPeriod.ForMonth(new DateOnly(2026, 9, 1));
        year.Close(LedgerFixture.Treasurer, LedgerFixture.Now);
        september.Close(LedgerFixture.Treasurer, LedgerFixture.Now);

        var calendar = new PeriodCalendar([year, september]);

        var check = () => calendar.EnsureOpenForPosting(new DateOnly(2026, 9, 17));

        check.Should().Throw<ClosedPeriodException>()
            .WithMessage("*2026-09*");
    }

    [Fact]
    public void A_reopened_period_accepts_postings_again()
    {
        var september = AccountingPeriod.ForMonth(new DateOnly(2026, 9, 1));
        september.Close(LedgerFixture.Treasurer, LedgerFixture.Now);
        september.Reopen(LedgerFixture.Chairman, "Duplicated payroll schedule", LedgerFixture.Now);

        var calendar = new PeriodCalendar([september]);

        calendar.IsOpenForPosting(new DateOnly(2026, 9, 17)).Should().BeTrue();
    }
}
