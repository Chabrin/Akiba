using Akiba.Application.Abstractions;
using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Infrastructure.Persistence;
using Akiba.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Akiba.Infrastructure.Tests.Persistence;

/// <summary>
/// The ledger against a real PostgreSQL instance.
/// </summary>
/// <remarks>
/// These tests are what turn the domain's guarantees into guarantees about the system. The
/// domain tests prove an unbalanced entry cannot be constructed; these prove that what is
/// written to PostgreSQL comes back as the same figures, that a closed period refuses a
/// posting, and that a balance derived for June is the same one derived in December.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class LedgerPersistenceTests : IAsyncLifetime
{
    private static readonly Actor Clerk = new(Guid.Parse("0000A11B-0000-0000-0000-000000000001"), "Oliver Kamau");
    private static readonly Actor Treasurer = new(Guid.Parse("0000A11B-0000-0000-0000-000000000002"), "Wilfred Wamai");
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 9, 30, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public LedgerPersistenceTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync() => _postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task An_entry_comes_back_from_PostgreSQL_as_the_same_figures_that_went_in()
    {
        // The whole point of numeric(19,4). An amount with cents must survive the round trip
        // exactly - a float column would return something very close and quietly wrong.
        await using var context = _postgres.CreateContext();
        var (accounts, journal) = Repositories(context);

        var bank = Account.OpenSocietyAccount(AccountCode.Of("1000"), "Bank", AccountType.Asset);
        var shares = Account.OpenMemberSharesAccount(AccountCode.Of("2100-0042"), "Grace Njeri", Guid.NewGuid());
        accounts.Add(bank);
        accounts.Add(shares);

        var entry = JournalEntry.Post(
            new DateOnly(2026, 9, 30),
            "Share contribution - Grace Njeri",
            SourceDocument.PayrollSchedule(2026, 9),
            Clerk,
            Now,
            [
                JournalLine.Debit(bank.Id, Money.Kes(5_500.55m)),
                JournalLine.Credit(shares.Id, Money.Kes(5_500.55m)),
            ]);

        await journal.AddAsync(entry);
        await context.SaveChangesAsync();

        var reloaded = await journal.FindByIdAsync(entry.Id);

        reloaded.Should().NotBeNull();
        reloaded!.Total.Should().Be(Money.Kes(5_500.55m));
        reloaded.Narration.Should().Be("Share contribution - Grace Njeri");
        reloaded.PostedBy.DisplayName.Should().Be("Oliver Kamau");
        reloaded.SourceDocument.Reference.Should().Be("2026-09");
        reloaded.Lines.Should().HaveCount(2);
    }

    [Fact]
    public async Task Money_columns_are_numeric_and_not_floating_point()
    {
        // Asserted against the live schema rather than against the C# model, because it is the
        // column type that decides whether 0.10 survives.
        await using var context = _postgres.CreateContext();

        var columnType = await context.Database
            .SqlQuery<string>($"""
                SELECT data_type AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'akiba'
                  AND table_name = 'journal_lines'
                  AND column_name = 'SignedAmount'
                """)
            .SingleAsync();

        columnType.Should().Be("numeric");
    }

    [Fact]
    public async Task A_members_shareholding_is_derived_from_the_entries_not_read_from_a_column()
    {
        await using var context = _postgres.CreateContext();
        var (accounts, journal) = Repositories(context);

        var bank = Account.OpenSocietyAccount(AccountCode.Of("1000"), "Bank", AccountType.Asset);
        var shares = Account.OpenMemberSharesAccount(AccountCode.Of("2100-0042"), "Grace Njeri", Guid.NewGuid());
        accounts.Add(bank);
        accounts.Add(shares);

        foreach (var month in Enumerable.Range(1, 6))
        {
            await journal.AddAsync(Contribution(bank, shares, 5_500m, month));
        }

        await context.SaveChangesAsync();

        var balances = new BalanceQueries(context, journal);

        var shareholding = await balances.NaturalBalanceAsAtAsync(shares.Id, new DateOnly(2026, 6, 30));

        shareholding.Should().Be(Money.Kes(33_000m));
    }

    [Fact]
    public async Task A_June_balance_computed_in_December_equals_the_one_computed_in_June()
    {
        // This is the proof the append-only design works, run against the real database. A
        // member asks in December what their shareholding was in June, and gets the figure
        // they would have been given in June - because nothing that made it up was overwritten.
        await using var context = _postgres.CreateContext();
        var (accounts, journal) = Repositories(context);

        var bank = Account.OpenSocietyAccount(AccountCode.Of("1000"), "Bank", AccountType.Asset);
        var shares = Account.OpenMemberSharesAccount(AccountCode.Of("2100-0042"), "Grace Njeri", Guid.NewGuid());
        accounts.Add(bank);
        accounts.Add(shares);

        foreach (var month in Enumerable.Range(1, 6))
        {
            await journal.AddAsync(Contribution(bank, shares, 5_500m, month));
        }

        await context.SaveChangesAsync();

        var balances = new BalanceQueries(context, journal);
        var june = new DateOnly(2026, 6, 30);
        var asAtJuneComputedInJune = await balances.NaturalBalanceAsAtAsync(shares.Id, june);

        // Six more months of ledger activity happen.
        foreach (var month in Enumerable.Range(7, 6))
        {
            await journal.AddAsync(Contribution(bank, shares, 7_250m, month));
        }

        await context.SaveChangesAsync();

        var asAtJuneComputedInDecember = await balances.NaturalBalanceAsAtAsync(shares.Id, june);

        asAtJuneComputedInDecember.Should().Be(asAtJuneComputedInJune);
        asAtJuneComputedInDecember.Should().Be(Money.Kes(33_000m));

        // And December's figure has genuinely moved on, so the test is not passing by accident.
        var asAtDecember = await balances.NaturalBalanceAsAtAsync(shares.Id, new DateOnly(2026, 12, 31));
        asAtDecember.Should().Be(Money.Kes(76_500m));
    }

    [Fact]
    public async Task The_repository_refuses_to_post_into_a_closed_period()
    {
        // Enforced at the repository, not in the UI. A closed period that any code path can
        // write to by forgetting to check is not closed.
        await using var context = _postgres.CreateContext();
        var (accounts, journal) = Repositories(context);
        var periods = new AccountingPeriodRepository(context);

        var bank = Account.OpenSocietyAccount(AccountCode.Of("1000"), "Bank", AccountType.Asset);
        var shares = Account.OpenMemberSharesAccount(AccountCode.Of("2100-0042"), "Grace Njeri", Guid.NewGuid());
        accounts.Add(bank);
        accounts.Add(shares);

        var september = AccountingPeriod.ForMonth(new DateOnly(2026, 9, 1));
        september.Close(Treasurer, Now);
        periods.Add(september);
        await context.SaveChangesAsync();

        var post = async () => await journal.AddAsync(Contribution(bank, shares, 5_500m, 9));

        await post.Should().ThrowAsync<ClosedPeriodException>()
            .WithMessage("*Post the correction into the open period instead*");
    }

    [Fact]
    public async Task A_correction_for_a_closed_month_posts_into_the_open_one()
    {
        // The sanctioned route. September's figures do not change because October found a
        // mistake; the reversal lands in October and September still reads as it did when the
        // treasurer signed it off.
        await using var context = _postgres.CreateContext();
        var (accounts, journal) = Repositories(context);
        var periods = new AccountingPeriodRepository(context);

        var bank = Account.OpenSocietyAccount(AccountCode.Of("1000"), "Bank", AccountType.Asset);
        var shares = Account.OpenMemberSharesAccount(AccountCode.Of("2100-0042"), "Grace Njeri", Guid.NewGuid());
        accounts.Add(bank);
        accounts.Add(shares);

        var original = Contribution(bank, shares, 5_500m, 9);
        await journal.AddAsync(original);
        await context.SaveChangesAsync();

        var september = AccountingPeriod.ForMonth(new DateOnly(2026, 9, 1));
        september.Close(Treasurer, Now);
        periods.Add(september);
        await context.SaveChangesAsync();

        var reversal = original.Reverse(
            new DateOnly(2026, 10, 3), "Posted against the wrong member", Clerk, Now);

        await journal.AddAsync(reversal);
        await context.SaveChangesAsync();

        var balances = new BalanceQueries(context, journal);

        (await balances.NaturalBalanceAsAtAsync(shares.Id, new DateOnly(2026, 9, 30)))
            .Should().Be(Money.Kes(5_500m));
        (await balances.NaturalBalanceAsAtAsync(shares.Id, new DateOnly(2026, 10, 31)))
            .Should().Be(Money.ZeroKes);
    }

    [Fact]
    public async Task A_reopened_period_accepts_postings_again()
    {
        await using var context = _postgres.CreateContext();
        var (accounts, journal) = Repositories(context);
        var periods = new AccountingPeriodRepository(context);

        var bank = Account.OpenSocietyAccount(AccountCode.Of("1000"), "Bank", AccountType.Asset);
        var shares = Account.OpenMemberSharesAccount(AccountCode.Of("2100-0042"), "Grace Njeri", Guid.NewGuid());
        accounts.Add(bank);
        accounts.Add(shares);

        var september = AccountingPeriod.ForMonth(new DateOnly(2026, 9, 1));
        september.Close(Treasurer, Now);
        periods.Add(september);
        await context.SaveChangesAsync();

        var reloaded = (await periods.FindMonthAsync(2026, 9))!;
        reloaded.Reopen(Treasurer, "September payroll schedule was posted twice", Now);
        periods.Update(reloaded);
        await context.SaveChangesAsync();

        await journal.AddAsync(Contribution(bank, shares, 5_500m, 9));
        await context.SaveChangesAsync();

        var balances = new BalanceQueries(context, journal);
        (await balances.NaturalBalanceAsAtAsync(shares.Id, new DateOnly(2026, 9, 30)))
            .Should().Be(Money.Kes(5_500m));
    }

    [Fact]
    public async Task A_closed_period_keeps_who_closed_it_and_when()
    {
        await using var context = _postgres.CreateContext();
        var periods = new AccountingPeriodRepository(context);

        var september = AccountingPeriod.ForMonth(new DateOnly(2026, 9, 1));
        september.Close(Treasurer, Now);
        periods.Add(september);
        await context.SaveChangesAsync();

        var reloaded = await periods.FindMonthAsync(2026, 9);

        reloaded!.IsClosed.Should().BeTrue();
        reloaded.ClosedBy!.Value.DisplayName.Should().Be("Wilfred Wamai");
        reloaded.ClosedAtUtc.Should().Be(Now);
    }

    [Fact]
    public async Task The_trial_balance_is_zero_across_everything_that_was_stored()
    {
        await using var context = _postgres.CreateContext();
        var (accounts, journal) = Repositories(context);

        var bank = Account.OpenSocietyAccount(AccountCode.Of("1000"), "Bank", AccountType.Asset);
        var shares = Account.OpenMemberSharesAccount(AccountCode.Of("2100-0042"), "Grace Njeri", Guid.NewGuid());
        var receivable = Account.OpenLoanReceivableAccount(AccountCode.Of("1200-AKB-2026-0007"), "AKB-2026-0007", Guid.NewGuid());
        var interest = Account.OpenSocietyAccount(AccountCode.Of("4000"), "Loan Interest Income", AccountType.Income);
        accounts.Add(bank);
        accounts.Add(shares);
        accounts.Add(receivable);
        accounts.Add(interest);

        await journal.AddAsync(Contribution(bank, shares, 5_500m, 9));
        await journal.AddAsync(JournalEntry.Post(
            new DateOnly(2026, 9, 20),
            "Disbursement - AKB-2026-0007",
            SourceDocument.Cheque("000431"),
            Clerk,
            Now,
            [
                JournalLine.Debit(receivable.Id, Money.Kes(27_500m)),
                JournalLine.Credit(bank.Id, Money.Kes(25_000m)),
                JournalLine.Credit(interest.Id, Money.Kes(2_500m)),
            ]));

        await context.SaveChangesAsync();

        var balances = new BalanceQueries(context, journal);

        (await balances.TrialBalanceDifferenceAsAtAsync(new DateOnly(2026, 12, 31)))
            .Should().Be(Money.ZeroKes);
    }

    [Fact]
    public async Task An_account_code_cannot_be_used_twice()
    {
        // Officials refer to accounts by code on paper, so a duplicate is a real ambiguity.
        await using var context = _postgres.CreateContext();
        var accounts = new AccountRepository(context);

        accounts.Add(Account.OpenSocietyAccount(AccountCode.Of("1000"), "Bank", AccountType.Asset));
        await context.SaveChangesAsync();

        accounts.Add(Account.OpenSocietyAccount(AccountCode.Of("1000"), "Bank again", AccountType.Asset));

        var save = async () => await context.SaveChangesAsync();

        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task A_members_share_account_can_be_found_from_their_member_id()
    {
        await using var context = _postgres.CreateContext();
        var accounts = new AccountRepository(context);
        var memberId = Guid.NewGuid();

        accounts.Add(Account.OpenMemberSharesAccount(AccountCode.Of("2100-0042"), "Grace Njeri", memberId));
        await context.SaveChangesAsync();

        var found = await accounts.ForOwnerAsync(AccountOwner.Member(memberId));

        found.Should().ContainSingle().Which.Name.Should().Be("Member Shares - Grace Njeri");
    }

    private static (IAccountRepository Accounts, IJournalRepository Journal) Repositories(
        AkibaDbContext context) =>
        (new AccountRepository(context), new JournalRepository(context));

    private static JournalEntry Contribution(Account bank, Account shares, decimal amount, int month) =>
        JournalEntry.Post(
            new DateOnly(2026, month, DateTime.DaysInMonth(2026, month)),
            $"Share contribution - {shares.Name}",
            SourceDocument.PayrollSchedule(2026, month),
            Clerk,
            Now,
            [
                JournalLine.Debit(bank.Id, Money.Kes(amount)),
                JournalLine.Credit(shares.Id, Money.Kes(amount)),
            ]);
}
