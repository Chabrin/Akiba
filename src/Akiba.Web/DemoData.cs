using Akiba.Application.Abstractions;
using Akiba.Application.Lending;
using Akiba.Application.Members;
using Akiba.Application.Receipting;
using Akiba.Domain.Financial;
using Akiba.Domain.Guaranteeing;
using Akiba.Domain.Lending;
using Akiba.Domain.Membership;
using Akiba.Domain.Receipting;
using MediatR;

namespace Akiba.Web;

/// <summary>
/// Fills a development database with plausible activity, so the panel has something to show.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never runs outside Development.</b> The endpoint that calls it is only mapped outside
/// Production, and this refuses anyway.
/// </para>
/// <para>
/// The names are realistic Kenyan names and the amounts are plausible, but none of it is real
/// member data and none of it ever will be. Real opening balances arrive through the migration
/// tooling, which is a reviewed and signed-off process - not a side effect of a seed script.
/// </para>
/// <para>
/// It drives the same commands the panel does, so what appears on screen got there the way an
/// official would have put it there. That also makes it a smoke test: if seeding works, the
/// whole stack from command to ledger works.
/// </para>
/// </remarks>
public static class DemoData
{
    private sealed record Person(
        string Membership,
        string Payroll,
        string Given,
        string Family,
        string? Other,
        string NationalId,
        string Phone,
        decimal MonthlyShare,
        bool IsLandlord);

    private static readonly Person[] People =
    [
        new("0001", "CAL/0001", "Grace", "Njeri", "Wambui", "23456781", "0712345001", 6_000m, false),
        new("0002", "CAL/0002", "Peter", "Mwangi", null, "23456782", "0712345002", 5_500m, false),
        new("0003", "CAL/0003", "Alice", "Wanjiru", "Muthoni", "23456783", "0712345003", 8_000m, true),
        new("0004", "CAL/0004", "Samuel", "Kiptoo", null, "23456784", "0712345004", 4_500m, false),
        new("0005", "CAL/0005", "Mary", "Otieno", "Akinyi", "23456785", "0712345005", 7_500m, false),
        new("0006", "CAL/0006", "Joseph", "Kamau", null, "23456786", "0712345006", 3_000m, false),
        new("0007", "CAL/0007", "Esther", "Chebet", null, "23456787", "0712345007", 9_000m, true),
        new("0008", "CAL/0008", "Daniel", "Omondi", "Ochieng", "23456788", "0712345008", 5_000m, false),
    ];

    /// <summary>Creates zones, members, contributions, loans and repayments.</summary>
    public static async Task<object> SeedAsync(
        IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var zones = await CreateZonesAsync(services, cancellationToken).ConfigureAwait(false);
        var members = await EnrolAsync(services, zones, cancellationToken).ConfigureAwait(false);

        // Eighteen months of monthly payroll deductions, so the "as at" control has history to
        // move through rather than a single point.
        var start = new DateOnly(2025, 4, 1);
        var months = 18;

        await ContributeAsync(services, members, start, months, cancellationToken)
            .ConfigureAwait(false);

        var loans = await LendAsync(services, members, cancellationToken).ConfigureAwait(false);

        await RepayAsync(services, loans, cancellationToken).ConfigureAwait(false);
        await LeaveOneUnclearedAndOneUnallocatedAsync(services, cancellationToken)
            .ConfigureAwait(false);

        return new
        {
            Zones = zones.Count,
            Members = members.Count,
            MonthsOfContributions = months,
            Loans = loans.Count,
            Note = "Development data. Realistic names, invented people.",
        };
    }

    private static async Task<IReadOnlyList<ZoneId>> CreateZonesAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        var repository = scope.ServiceProvider.GetRequiredService<IZoneRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // The real zone list is open question 9. These are placeholders so approval has
        // something to route through.
        var zones = new List<Zone>
        {
            Zone.CreateOffice("OFF", "Head office"),
            Zone.Create("KLE", "Kileleshwa"),
            Zone.Create("SWA", "South B and South C"),
        };

        foreach (var zone in zones)
        {
            repository.Add(zone);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return [.. zones.Select(zone => zone.Id)];
    }

    private static async Task<IReadOnlyList<(BorrowerId Id, Person Person)>> EnrolAsync(
        IServiceProvider services, IReadOnlyList<ZoneId> zones, CancellationToken cancellationToken)
    {
        var enrolled = new List<(BorrowerId, Person)>();

        for (var index = 0; index < People.Length; index++)
        {
            var person = People[index];

            var id = await SendAsync(
                services,
                new EnrolMemberCommand(
                    person.Membership,
                    person.Payroll,
                    person.Given,
                    person.Family,
                    person.Other,
                    person.NationalId,
                    person.Phone,
                    $"{person.Given.ToLowerInvariant()}.{person.Family.ToLowerInvariant()}@example.com",
                    zones[index % zones.Count],
                    person.IsLandlord),
                cancellationToken).ConfigureAwait(false);

            enrolled.Add((id, person));
        }

        return enrolled;
    }

    private static async Task ContributeAsync(
        IServiceProvider services,
        IReadOnlyList<(BorrowerId Id, Person Person)> members,
        DateOnly start,
        int months,
        CancellationToken cancellationToken)
    {
        // Employees and landlords are deducted on separate schedules, drawn on different
        // accounts - so they arrive as two cheques a month, not one.
        for (var offset = 0; offset < months; offset++)
        {
            var month = start.AddMonths(offset);
            var lastDay = new DateOnly(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));

            foreach (var landlord in new[] { false, true })
            {
                var group = members.Where(member => member.Person.IsLandlord == landlord).ToList();

                if (group.Count == 0)
                {
                    continue;
                }

                var total = group.Sum(member => member.Person.MonthlyShare);
                var reference = $"{(landlord ? "L" : "P")}{month:yyyyMM}";

                var receiptId = landlord
                    ? await SendAsync(
                        services,
                        new RecordLandlordReceiptCommand(Money.Kes(total), lastDay, reference),
                        cancellationToken).ConfigureAwait(false)
                    : await SendAsync(
                        services,
                        new RecordPayrollReceiptCommand(Money.Kes(total), lastDay, reference),
                        cancellationToken).ConfigureAwait(false);

                await SendAsync(services, new ClearReceiptCommand(receiptId, lastDay), cancellationToken)
                    .ConfigureAwait(false);

                foreach (var member in group)
                {
                    await SendAsync(
                        services,
                        new AllocateReceiptCommand(
                            receiptId,
                            AllocationTarget.Shares,
                            member.Id.Value,
                            Money.Kes(member.Person.MonthlyShare),
                            lastDay),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<IReadOnlyList<(LoanId Id, DateOnly DisbursedOn)>> LendAsync(
        IServiceProvider services,
        IReadOnlyList<(BorrowerId Id, Person Person)> members,
        CancellationToken cancellationToken)
    {
        var loans = new List<(LoanId, DateOnly)>();

        // A spread of products and sizes, including one that will fall into arrears so the
        // ageing report has something in it.
        (int Borrower, int Guarantor, LoanProduct Product, decimal Principal, DateOnly On, string Number)[] plan =
        [
            (0, 1, LoanProduct.Normal, 60_000m, new DateOnly(2026, 2, 12), "AKB-2026-0001"),
            (1, 0, LoanProduct.Emergency, 25_000m, new DateOnly(2026, 5, 8), "AKB-2026-0002"),
            (2, 4, LoanProduct.Normal, 150_000m, new DateOnly(2026, 3, 10), "AKB-2026-0003"),
            (4, 2, LoanProduct.Normal, 90_000m, new DateOnly(2025, 11, 11), "AKB-2025-0014"),
            (7, 3, LoanProduct.Emergency, 20_000m, new DateOnly(2026, 6, 9), "AKB-2026-0004"),
        ];

        foreach (var item in plan)
        {
            var borrower = members[item.Borrower];
            var guarantor = members[item.Guarantor];

            var applicationId = await SendAsync(
                services,
                new ReceiveLoanApplicationCommand(
                    borrower.Id,
                    item.Product,
                    Money.Kes(item.Principal),
                    item.On.AddDays(-4),
                    Money.Kes(borrower.Person.MonthlyShare * 14m),
                    null),
                cancellationToken).ConfigureAwait(false);

            await AddGuarantorAsync(
                services, applicationId, guarantor, item.Principal * 1.2m, item.On.AddDays(-3),
                cancellationToken).ConfigureAwait(false);

            await AdvanceApplicationAsync(services, applicationId, cancellationToken)
                .ConfigureAwait(false);

            await SendAsync(
                services,
                new ApproveLoanApplicationCommand(applicationId, null),
                cancellationToken).ConfigureAwait(false);

            var loanId = await SendAsync(
                services,
                new DisburseLoanCommand(
                    applicationId,
                    item.Number,
                    item.On,
                    $"00{loans.Count + 431}",
                    $"PV-{item.On:yyyy}-{loans.Count + 112}",
                    ["Mr. Mutinda", "Mr. Kimathi"]),
                cancellationToken).ConfigureAwait(false);

            loans.Add((loanId, item.On));
        }

        return loans;
    }

    private static async Task RepayAsync(
        IServiceProvider services,
        IReadOnlyList<(LoanId Id, DateOnly DisbursedOn)> loans,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ILoanRepository>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var today = clock.TodayInNairobi;

        for (var index = 0; index < loans.Count; index++)
        {
            var loan = await repository.FindByIdAsync(loans[index].Id, cancellationToken)
                .ConfigureAwait(false);

            if (loan is null)
            {
                continue;
            }

            var due = loan.Schedule.Instalments.Where(i => i.DueDate <= today).ToList();

            // The third loan stops paying part way through, so the arrears ageing has
            // something real in it rather than being permanently empty.
            var toPay = index == 2 ? due.Take(Math.Max(1, due.Count - 4)).ToList() : due;

            foreach (var instalment in toPay)
            {
                var receiptId = await SendAsync(
                    services,
                    new RecordPayrollReceiptCommand(
                        instalment.Amount,
                        instalment.DueDate,
                        $"R{loan.LoanNumber}-{instalment.Number:D2}"),
                    cancellationToken).ConfigureAwait(false);

                await SendAsync(
                    services,
                    new ClearReceiptCommand(receiptId, instalment.DueDate),
                    cancellationToken).ConfigureAwait(false);

                await SendAsync(
                    services,
                    new AllocateReceiptCommand(
                        receiptId,
                        AllocationTarget.LoanInstalment,
                        loan.Id.Value,
                        instalment.Amount,
                        instalment.DueDate),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Leaves one cheque unmatured and one cleared receipt unallocated, so the clerk's two
    /// work queues are not empty.
    /// </summary>
    private static async Task LeaveOneUnclearedAndOneUnallocatedAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var today = clock.TodayInNairobi;

        await SendAsync(
            services,
            new RecordDirectDepositCommand(
                Money.Kes(12_000m),
                ReceiptMethod.Cheque,
                today.AddDays(-3),
                "000914",
                "E. CHEBET",
                today.AddDays(4)),
            cancellationToken).ConfigureAwait(false);

        var unallocated = await SendAsync(
            services,
            new RecordDirectDepositCommand(
                Money.Kes(7_500m),
                ReceiptMethod.Mpesa,
                today.AddDays(-1),
                "SJ45KL9P0Q",
                "J KAMAU",
                null),
            cancellationToken).ConfigureAwait(false);

        await SendAsync(services, new ClearReceiptCommand(unallocated, today), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task AddGuarantorAsync(
        IServiceProvider services,
        LoanApplicationId applicationId,
        (BorrowerId Id, Person Person) guarantor,
        decimal guaranteed,
        DateOnly signedOn,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        var applications = scope.ServiceProvider.GetRequiredService<ILoanApplicationRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var application = await applications.FindByIdAsync(applicationId, cancellationToken)
            .ConfigureAwait(false);

        if (application is null)
        {
            return;
        }

        application.AddGuarantee(new Guarantee(
            guarantor.Id,
            $"{guarantor.Person.Given} {guarantor.Person.Family}",
            PayrollNumber.Of(guarantor.Person.Payroll),
            Money.Kes(guaranteed),
            Money.Kes(guarantor.Person.MonthlyShare * 12m),
            signedOn));

        applications.Update(application);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Submits the application and records the representatives' decisions.
    /// </summary>
    /// <remarks>
    /// Done through the repository because the panel has no screen for it yet, and because a
    /// representative cannot decide twice - the seeded clerk would otherwise be both of them.
    /// </remarks>
    private static async Task AdvanceApplicationAsync(
        IServiceProvider services, LoanApplicationId applicationId, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        var applications = scope.ServiceProvider.GetRequiredService<ILoanApplicationRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var application = await applications.FindByIdAsync(applicationId, cancellationToken)
            .ConfigureAwait(false);

        if (application is null)
        {
            return;
        }

        application.Submit();

        application.RecordDecision(
            new Domain.Common.Actor(Guid.Parse("0000B22C-0000-0000-0000-000000000001"), "Mary Otieno"),
            ApprovalDecisionKind.Approve,
            clock.UtcNow,
            "Shares sufficient");

        application.RecordDecision(
            new Domain.Common.Actor(Guid.Parse("0000B22C-0000-0000-0000-000000000002"), "Samuel Kiptoo"),
            ApprovalDecisionKind.Approve,
            clock.UtcNow);

        applications.Update(application);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TResponse> SendAsync<TResponse>(
        IServiceProvider services, IRequest<TResponse> request, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(request, cancellationToken)
            .ConfigureAwait(false);
    }
}
