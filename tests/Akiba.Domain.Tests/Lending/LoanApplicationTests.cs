using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Guaranteeing;
using Akiba.Domain.Ledger;
using Akiba.Domain.Lending;
using Akiba.Domain.Membership;
using Akiba.Domain.Tests.Ledger;

namespace Akiba.Domain.Tests.Lending;

public sealed class ApplicationCutoffTests
{
    private readonly ApplicationCutoff _cutoff = ApplicationCutoff.Version1;

    [Fact]
    public void The_cutoff_is_the_fifteenth_as_printed_on_both_forms()
    {
        _cutoff.DayOfMonth.Should().Be(15);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(15, true)]
    [InlineData(16, false)]
    [InlineData(30, false)]
    public void An_application_makes_the_cycle_if_it_arrives_by_the_fifteenth(int day, bool makesCycle)
    {
        _cutoff.MakesCurrentCycle(new DateOnly(2026, 9, day)).Should().Be(makesCycle);
    }

    [Fact]
    public void A_late_application_is_considered_the_following_month()
    {
        // "LATE APPLICATIONS WILL BE CONSIDERED IN THE SUCCEEDING MONTH."
        _cutoff.ConsiderationMonth(new DateOnly(2026, 9, 20))
            .Should().Be(new DateOnly(2026, 10, 1));
    }

    [Fact]
    public void A_late_December_application_rolls_into_January()
    {
        _cutoff.ConsiderationMonth(new DateOnly(2026, 12, 20))
            .Should().Be(new DateOnly(2027, 1, 1));
    }
}

public sealed class LoanApplicationTests
{
    [Fact]
    public void A_late_application_is_accepted_and_queued_rather_than_rejected()
    {
        // The office explicitly tracks loans approved but awaiting disbursement because they
        // were applied for after the lock period.
        var application = ApplicationFixture.Received(on: new DateOnly(2026, 9, 20));

        application.Status.Should().Be(LoanApplicationStatus.Draft);
        application.MissedTheCutoff.Should().BeTrue();
        application.ConsiderationMonth.Should().Be(new DateOnly(2026, 10, 1));
    }

    [Fact]
    public void An_application_arriving_in_time_is_considered_that_month()
    {
        var application = ApplicationFixture.Received(on: new DateOnly(2026, 9, 10));

        application.MissedTheCutoff.Should().BeFalse();
        application.ConsiderationMonth.Should().Be(new DateOnly(2026, 9, 1));
    }

    [Fact]
    public void An_approved_application_needs_at_least_one_representatives_decision()
    {
        // There is no single official who can approve alone: approval is by the member
        // representatives for the zone and the office.
        var application = ApplicationFixture.Submitted();

        var approve = () => application.Approve(ApplicationFixture.Terms, LedgerFixture.Now);

        approve.Should().Throw<InvalidOperationException>()
            .WithMessage("*representatives*");
    }

    [Fact]
    public void An_application_with_every_decision_in_favour_can_be_approved()
    {
        var application = ApplicationFixture.Submitted();
        application.RecordDecision(ApplicationFixture.ZoneRep, ApprovalDecisionKind.Approve, LedgerFixture.Now);
        application.RecordDecision(ApplicationFixture.OfficeRep, ApprovalDecisionKind.Approve, LedgerFixture.Now, "Shares sufficient");

        application.Approve(ApplicationFixture.Terms, LedgerFixture.Now);

        application.Status.Should().Be(LoanApplicationStatus.Approved);
        application.ApprovedPrincipal.Should().Be(Money.Kes(60_000m));
        application.DomainEvents.OfType<LoanApplicationApproved>().Should().ContainSingle();
    }

    [Fact]
    public void One_representative_rejecting_blocks_approval()
    {
        var application = ApplicationFixture.Submitted();
        application.RecordDecision(ApplicationFixture.ZoneRep, ApprovalDecisionKind.Approve, LedgerFixture.Now);
        application.RecordDecision(ApplicationFixture.OfficeRep, ApprovalDecisionKind.Reject, LedgerFixture.Now, "Shares insufficient");

        var approve = () => application.Approve(ApplicationFixture.Terms, LedgerFixture.Now);

        approve.Should().Throw<InvalidOperationException>().WithMessage("*rejected*");
    }

    [Fact]
    public void A_representative_cannot_decide_twice()
    {
        var application = ApplicationFixture.Submitted();
        application.RecordDecision(ApplicationFixture.ZoneRep, ApprovalDecisionKind.Approve, LedgerFixture.Now);

        var again = () => application.RecordDecision(
            ApplicationFixture.ZoneRep, ApprovalDecisionKind.Reject, LedgerFixture.Now);

        again.Should().Throw<InvalidOperationException>().WithMessage("*already decided*");
    }

    [Fact]
    public void A_borrower_cannot_guarantee_their_own_loan()
    {
        var application = ApplicationFixture.Received();

        var guarantee = new Guarantee(
            application.BorrowerId,
            "Grace Njeri",
            PayrollNumber.Of("CAL/0042"),
            Money.Kes(50_000m),
            Money.Kes(85_000m),
            new DateOnly(2026, 9, 10));

        var add = () => application.AddGuarantee(guarantee);

        add.Should().Throw<InvalidOperationException>().WithMessage("*own loan*");
    }

    [Fact]
    public void The_same_guarantor_cannot_sign_twice()
    {
        var application = ApplicationFixture.Received();
        var guarantee = ApplicationFixture.Guarantee("Peter Mwangi", 50_000m);

        application.AddGuarantee(guarantee);
        var again = () => application.AddGuarantee(guarantee);

        again.Should().Throw<InvalidOperationException>().WithMessage("*already guaranteed*");
    }

    [Fact]
    public void A_submitted_application_can_no_longer_be_edited()
    {
        // Applications are entered from the signed paper form, and the form does not change
        // after it has been submitted.
        var application = ApplicationFixture.Submitted();

        var edit = () => application.DeclareGrossSalary(Money.Kes(90_000m));

        edit.Should().Throw<InvalidOperationException>().WithMessage("*no longer be edited*");
    }

    [Fact]
    public void Rental_income_details_belong_only_on_a_rental_income_application()
    {
        var application = ApplicationFixture.Received();

        var record = () => application.RecordRentalIncome(ApplicationFixture.RentalIncome);

        record.Should().Throw<InvalidOperationException>().WithMessage("*rental income loan*");
    }

    [Fact]
    public void A_rental_income_application_captures_every_field_the_revised_form_asks_for()
    {
        var application = LoanApplication.Receive(
            BorrowerId.New(),
            ZoneId.New(),
            LoanProduct.RentalIncome,
            Money.Kes(300_000m),
            new DateOnly(2026, 9, 10),
            ApplicationCutoff.Version1);

        application.RecordRentalIncome(ApplicationFixture.RentalIncome);

        application.RentalIncome!.PropertyName.Should().Be("Riverside Court");
        application.RentalIncome.NumberOfUnits.Should().Be(8);
        application.RentalIncome.MonthlyRentalIncome.Should().Be(Money.Kes(120_000m));
        application.RentalIncome.RentStatementsAttached.Should().BeTrue();
        application.Security.Should().ContainSingle()
            .Which.Kind.Should().Be(LoanSecurityKind.RentalIncome);
    }

    [Fact]
    public void A_rental_income_application_without_rent_statements_records_that_it_lacks_them()
    {
        // The form says to attach proof of rental income, so an application without it is
        // incomplete rather than invalid - the clerk chases the paper.
        var rentalIncome = new RentalIncomeSecurity(
            "Riverside Court", "Kileleshwa, Nairobi", Money.Kes(120_000m), 8,
            rentStatementsAttached: false);

        rentalIncome.RentStatementsAttached.Should().BeFalse();
    }
}

public sealed class LoanDisbursementTests
{
    [Fact]
    public void Disbursing_an_approved_application_creates_the_loan_and_raises_the_posting_event()
    {
        var application = ApplicationFixture.Approved();

        var loan = Loan.Disburse(
            application,
            "AKB-2026-0007",
            AccountId.New(),
            new DateOnly(2026, 9, 20),
            ApplicationFixture.Cheque(60_000m),
            LedgerFixture.Now);

        loan.Status.Should().Be(LoanStatus.Running);
        loan.LoanNumber.Should().Be("AKB-2026-0007");
        application.Status.Should().Be(LoanApplicationStatus.Disbursed);
        loan.DomainEvents.OfType<LoanDisbursed>().Should().ContainSingle();
    }

    [Fact]
    public void A_disbursed_loan_carries_its_schedule_with_the_grace_month()
    {
        var application = ApplicationFixture.Approved();

        var loan = Loan.Disburse(
            application, "AKB-2026-0007", AccountId.New(), new DateOnly(2026, 9, 20),
            ApplicationFixture.Cheque(60_000m), LedgerFixture.Now);

        loan.Schedule.FirstDueDate.Should().Be(new DateOnly(2026, 11, 30));
        loan.Schedule.TotalScheduled.Should().Be(Money.Kes(66_000m));
    }

    [Fact]
    public void A_cheque_needs_two_signatories()
    {
        var single = () => new ChequeDetails(
            "000431", "PV-2026-0112", Money.Kes(60_000m), new DateOnly(2026, 9, 20), ["Mr. Mutinda"]);

        single.Should().Throw<ArgumentException>().WithMessage("*two signatories*");
    }

    [Fact]
    public void A_cheque_cannot_exceed_the_approved_principal()
    {
        var application = ApplicationFixture.Approved();

        var disburse = () => Loan.Disburse(
            application, "AKB-2026-0007", AccountId.New(), new DateOnly(2026, 9, 20),
            ApplicationFixture.Cheque(70_000m), LedgerFixture.Now);

        disburse.Should().Throw<InvalidOperationException>().WithMessage("*exceeds the approved principal*");
    }

    [Fact]
    public void An_unapproved_application_cannot_be_disbursed()
    {
        var application = ApplicationFixture.Submitted();

        var disburse = () => Loan.Disburse(
            application, "AKB-2026-0007", AccountId.New(), new DateOnly(2026, 9, 20),
            ApplicationFixture.Cheque(60_000m), LedgerFixture.Now);

        disburse.Should().Throw<InvalidOperationException>().WithMessage("*approved on terms*");
    }

    [Fact]
    public void A_loan_carries_the_guarantees_from_its_application()
    {
        var application = ApplicationFixture.Approved(withGuarantor: true);

        var loan = Loan.Disburse(
            application, "AKB-2026-0007", AccountId.New(), new DateOnly(2026, 9, 20),
            ApplicationFixture.Cheque(60_000m), LedgerFixture.Now);

        loan.Guarantees.Should().ContainSingle().Which.GuarantorName.Should().Be("Peter Mwangi");
    }

    [Fact]
    public void A_loan_can_only_be_restructured_once()
    {
        var loan = ApplicationFixture.RunningLoan();
        loan.CloseIntoRestructure(new DateOnly(2027, 3, 31));

        var again = () => loan.CloseIntoRestructure(new DateOnly(2027, 6, 30));

        again.Should().Throw<InvalidOperationException>().WithMessage("*Restructured*");
    }

    [Fact]
    public void Writing_off_a_loan_records_who_authorised_it_and_why()
    {
        var loan = ApplicationFixture.RunningLoan();

        loan.WriteOff(LedgerFixture.Chairman, "Borrower deceased, no recoverable estate", new DateOnly(2027, 4, 30));

        loan.Status.Should().Be(LoanStatus.WrittenOff);
        loan.DomainEvents.OfType<LoanWrittenOff>().Should().ContainSingle()
            .Which.AuthorisedBy.Should().Be(LedgerFixture.Chairman);
    }
}

internal static class ApplicationFixture
{
    public static Actor ZoneRep { get; } = new(Guid.Parse("0000B22C-0000-0000-0000-000000000001"), "Mary Otieno");

    public static Actor OfficeRep { get; } = new(Guid.Parse("0000B22C-0000-0000-0000-000000000002"), "Samuel Kiptoo");

    public static LoanTerms Terms { get; } = new LoanPricing().Price(LoanProduct.Normal, Money.Kes(60_000m));

    public static RentalIncomeSecurity RentalIncome { get; } = new(
        "Riverside Court", "Kileleshwa, Nairobi", Money.Kes(120_000m), 8, rentStatementsAttached: true);

    public static LoanApplication Received(DateOnly? on = null) =>
        LoanApplication.Receive(
            BorrowerId.New(),
            ZoneId.New(),
            LoanProduct.Normal,
            Money.Kes(60_000m),
            on ?? new DateOnly(2026, 9, 20),
            ApplicationCutoff.Version1);

    public static LoanApplication Submitted()
    {
        var application = Received(new DateOnly(2026, 9, 10));
        application.Submit();
        return application;
    }

    public static LoanApplication Approved(bool withGuarantor = false)
    {
        var application = Received(new DateOnly(2026, 9, 10));

        if (withGuarantor)
        {
            application.AddGuarantee(Guarantee("Peter Mwangi", 50_000m));
        }

        application.Submit();
        application.RecordDecision(ZoneRep, ApprovalDecisionKind.Approve, LedgerFixture.Now);
        application.RecordDecision(OfficeRep, ApprovalDecisionKind.Approve, LedgerFixture.Now);
        application.Approve(Terms, LedgerFixture.Now);

        return application;
    }

    public static Guarantee Guarantee(string name, decimal guaranteed) =>
        new(
            BorrowerId.New(),
            name,
            PayrollNumber.Of("CAL/0099"),
            Money.Kes(guaranteed),
            Money.Kes(guaranteed * 2m),
            new DateOnly(2026, 9, 10));

    public static ChequeDetails Cheque(decimal amount) =>
        new(
            "000431",
            "PV-2026-0112",
            Money.Kes(amount),
            new DateOnly(2026, 9, 20),
            ["Mr. Mutinda", "Mr. Kimathi"]);

    public static Loan RunningLoan() =>
        Loan.Disburse(
            Approved(),
            "AKB-2026-0007",
            AccountId.New(),
            new DateOnly(2026, 9, 20),
            Cheque(60_000m),
            LedgerFixture.Now);
}
