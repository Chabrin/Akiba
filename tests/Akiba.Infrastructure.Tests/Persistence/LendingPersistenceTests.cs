using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Domain.Lending;
using Akiba.Domain.Membership;
using Akiba.Domain.Receipting;
using Akiba.Infrastructure.Persistence;
using Akiba.Infrastructure.Persistence.Repositories;

namespace Akiba.Infrastructure.Tests.Persistence;

/// <summary>
/// Members, loans, applications and receipts against a real PostgreSQL instance.
/// </summary>
/// <remarks>
/// The point of each of these is the round trip. An aggregate is rebuilt through its
/// <c>Rehydrate</c> factory, so anything the database gives back has passed the same
/// invariant checks it passed on the way in.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class LendingPersistenceTests : IAsyncLifetime
{
    private static readonly Actor Clerk = new(Guid.Parse("0000A11B-0000-0000-0000-000000000001"), "Oliver Kamau");
    private static readonly Actor ZoneRep = new(Guid.Parse("0000B22C-0000-0000-0000-000000000001"), "Mary Otieno");
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 9, 30, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public LendingPersistenceTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync() => _postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_member_comes_back_with_their_share_account_and_zone()
    {
        await using var context = _postgres.CreateContext();
        var (accounts, borrowers, zones) = MembershipRepositories(context);

        var zone = Zone.Create("KLE", "Kileleshwa");
        zones.Add(zone);

        var shares = Account.OpenMemberSharesAccount(
            AccountCode.Of("2100-0042"), "Grace Njeri", Guid.NewGuid());
        accounts.Add(shares);

        var member = Member.Join(
            MembershipNumber.Of("0042"),
            PayrollNumber.Of("CAL/0042"),
            new PersonName("Grace", "Njeri", "Wambui"),
            NationalId.Of("28765432"),
            PhoneNumber.Of("0712345678"),
            "grace.njeri@example.com",
            zone.Id,
            shares.Id);

        borrowers.Add(member);
        await context.SaveChangesAsync();

        var reloaded = await borrowers.FindMemberAsync(member.Id);

        reloaded.Should().NotBeNull();
        reloaded!.Name.Full.Should().Be("Grace Wambui Njeri");
        reloaded.PayrollNumber.Value.Should().Be("CAL/0042");
        reloaded.Phone.Value.Should().Be("+254712345678");
        reloaded.SharesAccountId.Should().Be(shares.Id);
        reloaded.ZoneId.Should().Be(zone.Id);
        reloaded.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task A_members_shareholding_is_derived_from_their_share_account()
    {
        // The end-to-end version of the rule: nothing on the member row says what they hold.
        await using var context = _postgres.CreateContext();
        var (accounts, borrowers, zones) = MembershipRepositories(context);
        var journal = new JournalRepository(context);

        var bank = Account.OpenSocietyAccount(AccountCode.Of("1000"), "Bank", AccountType.Asset);
        var zone = Zone.Create("KLE", "Kileleshwa");
        var shares = Account.OpenMemberSharesAccount(AccountCode.Of("2100-0042"), "Grace Njeri", Guid.NewGuid());
        accounts.Add(bank);
        accounts.Add(shares);
        zones.Add(zone);

        var member = MemberJoining(zone, shares);
        borrowers.Add(member);

        foreach (var month in Enumerable.Range(1, 4))
        {
            await journal.AddAsync(Contribution(bank, shares, 5_500m, month));
        }

        await context.SaveChangesAsync();

        var reloaded = (await borrowers.FindMemberAsync(member.Id))!;
        var entries = await journal.ForAccountAsOfAsync(
            reloaded.SharesAccountId, new DateOnly(2026, 4, 30));

        var shareholding = Shareholding.AsAt(entries, shares, new DateOnly(2026, 4, 30));

        shareholding.Should().Be(Money.Kes(22_000m));
        Shareholding.BorrowingLimit(shareholding).Should().Be(Money.Kes(44_000m));
        Shareholding.MembershipStartDate(entries, shares.Id).Should().Be(new DateOnly(2026, 1, 31));
    }

    [Fact]
    public async Task A_member_who_leaves_CAL_is_recorded_as_exited()
    {
        await using var context = _postgres.CreateContext();
        var (accounts, borrowers, zones) = MembershipRepositories(context);

        var zone = Zone.Create("KLE", "Kileleshwa");
        var shares = Account.OpenMemberSharesAccount(AccountCode.Of("2100-0042"), "Grace Njeri", Guid.NewGuid());
        accounts.Add(shares);
        zones.Add(zone);

        var member = MemberJoining(zone, shares);
        borrowers.Add(member);
        await context.SaveChangesAsync();

        var loaded = (await borrowers.FindMemberAsync(member.Id))!;
        loaded.ExitEmployment(new DateOnly(2026, 9, 30), Now);
        borrowers.Update(loaded);
        await context.SaveChangesAsync();

        var reloaded = await borrowers.FindMemberAsync(member.Id);

        reloaded!.IsActive.Should().BeFalse();
        reloaded.ExitedOn.Should().Be(new DateOnly(2026, 9, 30));
    }

    [Fact]
    public async Task Two_members_cannot_share_a_payroll_number()
    {
        // HR matches the deduction schedule on it, so a duplicate is a real-world collision.
        await using var context = _postgres.CreateContext();
        var (accounts, borrowers, zones) = MembershipRepositories(context);

        var zone = Zone.Create("KLE", "Kileleshwa");
        zones.Add(zone);

        foreach (var suffix in new[] { "0042", "0043" })
        {
            var shares = Account.OpenMemberSharesAccount(
                AccountCode.Of($"2100-{suffix}"), $"Member {suffix}", Guid.NewGuid());
            accounts.Add(shares);

            borrowers.Add(Member.Join(
                MembershipNumber.Of(suffix),
                PayrollNumber.Of("CAL/0042"),
                new PersonName("Member", suffix),
                NationalId.Of("28765432"),
                PhoneNumber.Of("0712345678"),
                null,
                zone.Id,
                shares.Id));
        }

        var save = async () => await context.SaveChangesAsync();

        await save.Should().ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateException>();
    }

    [Fact]
    public async Task An_application_survives_the_round_trip_with_its_decisions_and_guarantors()
    {
        await using var context = _postgres.CreateContext();
        var applications = new LoanApplicationRepository(context);

        var application = LoanApplication.Receive(
            BorrowerId.New(),
            ZoneId.New(),
            LoanProduct.Normal,
            Money.Kes(60_000m),
            new DateOnly(2026, 9, 20),
            ApplicationCutoff.Version1);

        application.DeclareGrossSalary(Money.Kes(90_000m));
        application.AddGuarantee(new Domain.Guaranteeing.Guarantee(
            BorrowerId.New(),
            "Peter Mwangi",
            PayrollNumber.Of("CAL/0099"),
            Money.Kes(50_000m),
            Money.Kes(85_000m),
            new DateOnly(2026, 9, 18)));
        application.OfferSecurity(LoanSecurity.Guarantors());
        application.Submit();
        application.RecordDecision(ZoneRep, ApprovalDecisionKind.Approve, Now, "Shares sufficient");

        applications.Add(application);
        await context.SaveChangesAsync();

        var reloaded = await applications.FindByIdAsync(application.Id);

        reloaded.Should().NotBeNull();
        reloaded!.MissedTheCutoff.Should().BeTrue(because: "it arrived on the 20th");
        reloaded.ConsiderationMonth.Should().Be(new DateOnly(2026, 10, 1));
        reloaded.DeclaredGrossSalary.Should().Be(Money.Kes(90_000m));
        reloaded.Guarantees.Should().ContainSingle()
            .Which.GuaranteedAmount.Should().Be(Money.Kes(50_000m));
        reloaded.Decisions.Should().ContainSingle()
            .Which.Comment.Should().Be("Shares sufficient");
    }

    [Fact]
    public async Task A_rental_income_application_keeps_every_field_from_the_revised_form()
    {
        await using var context = _postgres.CreateContext();
        var applications = new LoanApplicationRepository(context);

        var application = LoanApplication.Receive(
            BorrowerId.New(), ZoneId.New(), LoanProduct.RentalIncome,
            Money.Kes(300_000m), new DateOnly(2026, 9, 10), ApplicationCutoff.Version1);

        application.RecordRentalIncome(new RentalIncomeSecurity(
            "Riverside Court", "Kileleshwa, Nairobi", Money.Kes(120_000m), 8, true));

        applications.Add(application);
        await context.SaveChangesAsync();

        var reloaded = await applications.FindByIdAsync(application.Id);

        reloaded!.RentalIncome!.PropertyName.Should().Be("Riverside Court");
        reloaded.RentalIncome.PropertyLocation.Should().Be("Kileleshwa, Nairobi");
        reloaded.RentalIncome.MonthlyRentalIncome.Should().Be(Money.Kes(120_000m));
        reloaded.RentalIncome.NumberOfUnits.Should().Be(8);
        reloaded.RentalIncome.RentStatementsAttached.Should().BeTrue();
    }

    [Fact]
    public async Task A_loan_comes_back_with_its_cheque_terms_and_schedule()
    {
        await using var context = _postgres.CreateContext();
        var (accounts, _, _) = MembershipRepositories(context);
        var applications = new LoanApplicationRepository(context);
        var loans = new LoanRepository(context);

        var receivable = Account.OpenLoanReceivableAccount(
            AccountCode.Of("1200-AKB-2026-0007"), "AKB-2026-0007", Guid.NewGuid());
        accounts.Add(receivable);

        var application = ApprovedApplication();
        applications.Add(application);

        var loan = Loan.Disburse(
            application,
            "AKB-2026-0007",
            receivable.Id,
            new DateOnly(2026, 9, 20),
            new ChequeDetails("000431", "PV-2026-0112", Money.Kes(60_000m),
                new DateOnly(2026, 9, 20), ["Mr. Mutinda", "Mr. Kimathi"]),
            Now);

        loans.Add(loan);
        applications.Update(application);
        await context.SaveChangesAsync();

        var reloaded = await loans.FindByNumberAsync("AKB-2026-0007");

        reloaded.Should().NotBeNull();
        reloaded!.Terms.TotalRepayable.Should().Be(Money.Kes(66_000m));
        reloaded.Cheque.Signatories.Should().Equal("Mr. Mutinda", "Mr. Kimathi");
        reloaded.Cheque.ChequeNumber.Should().Be("000431");

        // The schedule is derived from the stored terms, so the grace month survives too.
        reloaded.Schedule.FirstDueDate.Should().Be(new DateOnly(2026, 11, 30));
        reloaded.Schedule.Instalments.Should().HaveCount(12);
    }

    [Fact]
    public async Task A_loan_can_be_found_by_the_member_who_guarantees_it()
    {
        // What the guarantor exposure report and the exit review both read by.
        await using var context = _postgres.CreateContext();
        var (accounts, _, _) = MembershipRepositories(context);
        var applications = new LoanApplicationRepository(context);
        var loans = new LoanRepository(context);

        var guarantorId = BorrowerId.New();
        var receivable = Account.OpenLoanReceivableAccount(
            AccountCode.Of("1200-AKB-2026-0007"), "AKB-2026-0007", Guid.NewGuid());
        accounts.Add(receivable);

        var application = ApprovedApplication(guarantorId);
        applications.Add(application);

        loans.Add(Loan.Disburse(
            application, "AKB-2026-0007", receivable.Id, new DateOnly(2026, 9, 20),
            new ChequeDetails("000431", "PV-2026-0112", Money.Kes(60_000m),
                new DateOnly(2026, 9, 20), ["Mr. Mutinda", "Mr. Kimathi"]),
            Now));

        await context.SaveChangesAsync();

        var guaranteed = await loans.GuaranteedByAsync(guarantorId);

        guaranteed.Should().ContainSingle().Which.LoanNumber.Should().Be("AKB-2026-0007");
    }

    [Fact]
    public async Task A_receipt_keeps_its_clearance_state_and_its_allocations()
    {
        await using var context = _postgres.CreateContext();
        var receipts = new ReceiptRepository(context);

        var receipt = Receipt.FromDirectDeposit(
            Money.Kes(11_000m), ReceiptMethod.Cheque, new DateOnly(2026, 9, 12),
            "000512", "G. NJERI", new DateOnly(2026, 9, 19));

        receipts.Add(receipt);
        await context.SaveChangesAsync();

        var loaded = (await receipts.FindByIdAsync(receipt.Id))!;
        loaded.PayerNameOnSlip.Should().Be("G. NJERI", because: "the slip is kept as written");
        loaded.IsConfirmed.Should().BeFalse();

        loaded.Clear(new DateOnly(2026, 9, 19));
        loaded.Allocate(AllocationTarget.Shares, Guid.NewGuid(), Money.Kes(5_500m), Clerk, Now);
        loaded.Allocate(AllocationTarget.LoanInstalment, Guid.NewGuid(), Money.Kes(5_500m), Clerk, Now);
        receipts.Update(loaded);
        await context.SaveChangesAsync();

        var reloaded = (await receipts.FindByIdAsync(receipt.Id))!;

        reloaded.IsConfirmed.Should().BeTrue();
        reloaded.IsFullyAllocated.Should().BeTrue();
        reloaded.LiveAllocations.Should().HaveCount(2);
        reloaded.Allocations[0].AllocatedBy.DisplayName.Should().Be("Oliver Kamau");
    }

    [Fact]
    public async Task A_reversed_allocation_stays_on_the_record_after_a_round_trip()
    {
        await using var context = _postgres.CreateContext();
        var receipts = new ReceiptRepository(context);

        var receipt = Receipt.FromPayroll(Money.Kes(5_500m), new DateOnly(2026, 9, 30), "000512");
        receipt.Clear(new DateOnly(2026, 9, 30));
        receipt.Allocate(AllocationTarget.Shares, Guid.NewGuid(), Money.Kes(5_500m), Clerk, Now);
        receipt.ReverseAllocation(0, "Allocated against the wrong member");

        receipts.Add(receipt);
        await context.SaveChangesAsync();

        var reloaded = (await receipts.FindByIdAsync(receipt.Id))!;

        reloaded.UnallocatedAmount.Should().Be(Money.Kes(5_500m));
        reloaded.Allocations.Should().ContainSingle()
            .Which.ReversedReason.Should().Be("Allocated against the wrong member");
    }

    [Fact]
    public async Task The_clerks_queues_show_what_has_not_cleared_and_what_is_unallocated()
    {
        await using var context = _postgres.CreateContext();
        var receipts = new ReceiptRepository(context);

        var uncleared = Receipt.FromDirectDeposit(
            Money.Kes(20_000m), ReceiptMethod.Cheque, new DateOnly(2026, 9, 12),
            "000512", "Grace Njeri", new DateOnly(2026, 9, 19));

        var clearedButUnallocated = Receipt.FromPayroll(
            Money.Kes(120_000m), new DateOnly(2026, 9, 30), "000513");
        clearedButUnallocated.Clear(new DateOnly(2026, 9, 30));

        var done = Receipt.FromPayroll(Money.Kes(5_500m), new DateOnly(2026, 9, 30), "000514");
        done.Clear(new DateOnly(2026, 9, 30));
        done.Allocate(AllocationTarget.Shares, Guid.NewGuid(), Money.Kes(5_500m), Clerk, Now);

        receipts.Add(uncleared);
        receipts.Add(clearedButUnallocated);
        receipts.Add(done);
        await context.SaveChangesAsync();

        (await receipts.AwaitingClearanceAsync()).Should().ContainSingle()
            .Which.Reference.Should().Be("000512");

        (await receipts.AwaitingAllocationAsync()).Should().ContainSingle()
            .Which.Reference.Should().Be("000513");
    }

    [Fact]
    public async Task The_approved_but_not_disbursed_queue_is_what_the_office_watches()
    {
        await using var context = _postgres.CreateContext();
        var applications = new LoanApplicationRepository(context);

        applications.Add(ApprovedApplication());
        await context.SaveChangesAsync();

        var queue = await applications.AwaitingDisbursementAsync();

        queue.Should().ContainSingle()
            .Which.Status.Should().Be(LoanApplicationStatus.Approved);
    }

    private static (AccountRepository Accounts, BorrowerRepository Borrowers, ZoneRepository Zones)
        MembershipRepositories(AkibaDbContext context) =>
        (new AccountRepository(context), new BorrowerRepository(context), new ZoneRepository(context));

    private static Member MemberJoining(Zone zone, Account shares) => Member.Join(
        MembershipNumber.Of("0042"),
        PayrollNumber.Of("CAL/0042"),
        new PersonName("Grace", "Njeri"),
        NationalId.Of("28765432"),
        PhoneNumber.Of("0712345678"),
        "grace.njeri@example.com",
        zone.Id,
        shares.Id);

    private static LoanApplication ApprovedApplication(BorrowerId? guarantorId = null)
    {
        var application = LoanApplication.Receive(
            BorrowerId.New(), ZoneId.New(), LoanProduct.Normal,
            Money.Kes(60_000m), new DateOnly(2026, 9, 10), ApplicationCutoff.Version1);

        if (guarantorId is { } guarantor)
        {
            application.AddGuarantee(new Domain.Guaranteeing.Guarantee(
                guarantor, "Peter Mwangi", PayrollNumber.Of("CAL/0099"),
                Money.Kes(50_000m), Money.Kes(85_000m), new DateOnly(2026, 9, 9)));
        }

        application.Submit();
        application.RecordDecision(ZoneRep, ApprovalDecisionKind.Approve, Now);
        application.Approve(new LoanPricing().Price(LoanProduct.Normal, Money.Kes(60_000m)), Now);

        return application;
    }

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
