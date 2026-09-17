using Akiba.Application.Abstractions;
using Akiba.Domain.Lending;
using Akiba.Domain.Membership;
using Akiba.Domain.Receipting;
using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Akiba.Infrastructure.Persistence.Repositories;

/// <summary>
/// The repositories for everything that is not the ledger.
/// </summary>
/// <remarks>
/// <para>
/// Each one loads rows, hands them to a mapper, and gets back an aggregate built through its
/// <c>Rehydrate</c> factory - so the invariants are checked on the way out of the database as
/// well as on the way in.
/// </para>
/// <para>
/// <c>Update</c> replaces the row's children wholesale rather than diffing them. These are
/// small collections - a handful of guarantors, a couple of approval decisions - and a clerk
/// edits them one at a time on a LAN. Diffing would be more code for no gain, and the ledger,
/// where wholesale replacement would be unthinkable, has no Update at all.
/// </para>
/// <para>
/// Each <c>Update</c> looks in the change tracker before it queries. A single command can add
/// an aggregate and then update it - disbursing a loan creates the loan and marks its
/// application disbursed - and a straight query would not find a row that has not been saved
/// yet, failing with a bare "sequence contains no elements".
/// </para>
/// </remarks>
internal sealed class BorrowerRepository : IBorrowerRepository
{
    private readonly AkibaDbContext _context;

    public BorrowerRepository(AkibaDbContext context) => _context = context;

    public async Task<Borrower?> FindByIdAsync(
        BorrowerId id, CancellationToken cancellationToken = default)
    {
        var row = await _context.Borrowers
            .AsNoTracking()
            .FirstOrDefaultAsync(borrower => borrower.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : MembershipMapper.ToDomain(row);
    }

    public async Task<Member?> FindMemberAsync(
        BorrowerId id, CancellationToken cancellationToken = default)
    {
        var row = await _context.Borrowers
            .AsNoTracking()
            .FirstOrDefaultAsync(
                borrower => borrower.Id == id.Value && borrower.Kind == BorrowerKind.Member,
                cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : MembershipMapper.ToMember(row);
    }

    public async Task<Member?> FindMemberByPayrollNumberAsync(
        PayrollNumber payrollNumber, CancellationToken cancellationToken = default)
    {
        var row = await _context.Borrowers
            .AsNoTracking()
            .FirstOrDefaultAsync(
                borrower => borrower.PayrollNumber == payrollNumber.Value, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : MembershipMapper.ToMember(row);
    }

    public async Task<IReadOnlyList<Member>> AllMembersAsync(
        bool includeExited = false, CancellationToken cancellationToken = default)
    {
        var query = _context.Borrowers
            .AsNoTracking()
            .Where(borrower => borrower.Kind == BorrowerKind.Member);

        if (!includeExited)
        {
            query = query.Where(borrower =>
                borrower.EmploymentStatus == (int)EmploymentStatus.Employed);
        }

        var rows = await query
            .OrderBy(borrower => borrower.FamilyName)
            .ThenBy(borrower => borrower.GivenName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(MembershipMapper.ToMember)];
    }

    public async Task<IReadOnlyList<Member>> LandlordMembersAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.Borrowers
            .AsNoTracking()
            .Where(borrower => borrower.Kind == BorrowerKind.Member && borrower.IsLandlord)
            .OrderBy(borrower => borrower.FamilyName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(MembershipMapper.ToMember)];
    }

    public void Add(Borrower borrower) => _context.Borrowers.Add(MembershipMapper.ToRow(borrower));

    public void Update(Borrower borrower)
    {
        ArgumentNullException.ThrowIfNull(borrower);

        var existing =
            _context.Borrowers.Local.FirstOrDefault(row => row.Id == borrower.Id.Value)
            ?? _context.Borrowers
                .Include(row => row.Documents)
                .First(row => row.Id == borrower.Id.Value);

        var updated = MembershipMapper.ToRow(borrower);

        _context.Entry(existing).CurrentValues.SetValues(updated);
        ChildRows.Replace(_context.AttachedDocuments, existing.Documents, updated.Documents);
    }
}

internal sealed class ZoneRepository : IZoneRepository
{
    private readonly AkibaDbContext _context;

    public ZoneRepository(AkibaDbContext context) => _context = context;

    public async Task<Zone?> FindByIdAsync(ZoneId id, CancellationToken cancellationToken = default)
    {
        var row = await _context.Zones
            .AsNoTracking()
            .FirstOrDefaultAsync(zone => zone.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : MembershipMapper.ToDomain(row);
    }

    public async Task<IReadOnlyList<Zone>> AllAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _context.Zones
            .AsNoTracking()
            .OrderBy(zone => zone.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(MembershipMapper.ToDomain)];
    }

    public void Add(Zone zone) => _context.Zones.Add(MembershipMapper.ToRow(zone));

    public void Update(Zone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var existing =
            _context.Zones.Local.FirstOrDefault(row => row.Id == zone.Id.Value)
            ?? _context.Zones
                .Include(row => row.Representatives)
                .First(row => row.Id == zone.Id.Value);

        var updated = MembershipMapper.ToRow(zone);

        _context.Entry(existing).CurrentValues.SetValues(updated);
        ChildRows.Replace(_context.ZoneRepresentatives, existing.Representatives, updated.Representatives);
    }
}

internal sealed class LoanApplicationRepository : ILoanApplicationRepository
{
    private readonly AkibaDbContext _context;

    public LoanApplicationRepository(AkibaDbContext context) => _context = context;

    public async Task<LoanApplication?> FindByIdAsync(
        LoanApplicationId id, CancellationToken cancellationToken = default)
    {
        var row = await _context.LoanApplications
            .AsNoTracking()
            .FirstOrDefaultAsync(application => application.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : LendingMapper.ToDomain(row);
    }

    public async Task<IReadOnlyList<LoanApplication>> ByStatusAsync(
        LoanApplicationStatus status, CancellationToken cancellationToken = default)
    {
        var rows = await _context.LoanApplications
            .AsNoTracking()
            .Where(application => application.Status == (int)status)
            .OrderBy(application => application.ReceivedOn)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(LendingMapper.ToDomain)];
    }

    public Task<IReadOnlyList<LoanApplication>> AwaitingDisbursementAsync(
        CancellationToken cancellationToken = default) =>
        ByStatusAsync(LoanApplicationStatus.Approved, cancellationToken);

    public async Task<IReadOnlyList<LoanApplication>> ForBorrowerAsync(
        BorrowerId borrowerId, CancellationToken cancellationToken = default)
    {
        var rows = await _context.LoanApplications
            .AsNoTracking()
            .Where(application => application.BorrowerId == borrowerId.Value)
            .OrderByDescending(application => application.ReceivedOn)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(LendingMapper.ToDomain)];
    }

    public void Add(LoanApplication application) =>
        _context.LoanApplications.Add(LendingMapper.ToRow(application));

    public void Update(LoanApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        var existing =
            _context.LoanApplications.Local.FirstOrDefault(row => row.Id == application.Id.Value)
            ?? _context.LoanApplications
                .Include(row => row.Decisions)
                .Include(row => row.Guarantees)
                .Include(row => row.Security)
                .Include(row => row.Documents)
                .First(row => row.Id == application.Id.Value);

        var updated = LendingMapper.ToRow(application);

        _context.Entry(existing).CurrentValues.SetValues(updated);

        ChildRows.Replace(_context.ApprovalDecisions, existing.Decisions, updated.Decisions);
        ChildRows.Replace(_context.Guarantees, existing.Guarantees, updated.Guarantees);
        ChildRows.Replace(_context.LoanSecurity, existing.Security, updated.Security);
        ChildRows.Replace(_context.AttachedDocuments, existing.Documents, updated.Documents);
    }
}

internal sealed class LoanRepository : ILoanRepository
{
    private readonly AkibaDbContext _context;

    public LoanRepository(AkibaDbContext context) => _context = context;

    public async Task<Loan?> FindByIdAsync(LoanId id, CancellationToken cancellationToken = default)
    {
        var row = await _context.Loans
            .AsNoTracking()
            .FirstOrDefaultAsync(loan => loan.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : LendingMapper.ToDomain(row);
    }

    public async Task<Loan?> FindByNumberAsync(
        string loanNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loanNumber);

        var normalised = loanNumber.Trim().ToUpperInvariant();

        var row = await _context.Loans
            .AsNoTracking()
            .FirstOrDefaultAsync(loan => loan.LoanNumber == normalised, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : LendingMapper.ToDomain(row);
    }

    public async Task<IReadOnlyList<Loan>> RunningForBorrowerAsync(
        BorrowerId borrowerId, CancellationToken cancellationToken = default)
    {
        var rows = await _context.Loans
            .AsNoTracking()
            .Where(loan => loan.BorrowerId == borrowerId.Value
                && loan.Status == (int)LoanStatus.Running)
            .OrderBy(loan => loan.DisbursedOn)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(LendingMapper.ToDomain)];
    }

    public async Task<IReadOnlyList<Loan>> AllForBorrowerAsync(
        BorrowerId borrowerId, CancellationToken cancellationToken = default)
    {
        var rows = await _context.Loans
            .AsNoTracking()
            .Where(loan => loan.BorrowerId == borrowerId.Value)
            .OrderBy(loan => loan.DisbursedOn)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(LendingMapper.ToDomain)];
    }

    public async Task<IReadOnlyList<Loan>> AllRunningAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _context.Loans
            .AsNoTracking()
            .Where(loan => loan.Status == (int)LoanStatus.Running)
            .OrderBy(loan => loan.LoanNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(LendingMapper.ToDomain)];
    }

    public async Task<IReadOnlyList<Loan>> GuaranteedByAsync(
        BorrowerId guarantorId, CancellationToken cancellationToken = default)
    {
        var rows = await _context.Loans
            .AsNoTracking()
            .Where(loan => loan.Status == (int)LoanStatus.Running
                && loan.Guarantees.Any(guarantee =>
                    guarantee.GuarantorId == guarantorId.Value && !guarantee.IsReleased))
            .OrderBy(loan => loan.LoanNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(LendingMapper.ToDomain)];
    }

    public void Add(Loan loan) => _context.Loans.Add(LendingMapper.ToRow(loan));

    public void Update(Loan loan)
    {
        ArgumentNullException.ThrowIfNull(loan);

        var existing =
            _context.Loans.Local.FirstOrDefault(row => row.Id == loan.Id.Value)
            ?? _context.Loans
                .Include(row => row.Guarantees)
                .First(row => row.Id == loan.Id.Value);

        var updated = LendingMapper.ToRow(loan);

        _context.Entry(existing).CurrentValues.SetValues(updated);
        ChildRows.Replace(_context.Guarantees, existing.Guarantees, updated.Guarantees);
    }
}

internal sealed class ReceiptRepository : IReceiptRepository
{
    private readonly AkibaDbContext _context;

    public ReceiptRepository(AkibaDbContext context) => _context = context;

    public async Task<Receipt?> FindByIdAsync(
        ReceiptId id, CancellationToken cancellationToken = default)
    {
        var row = await _context.Receipts
            .AsNoTracking()
            .FirstOrDefaultAsync(receipt => receipt.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : LendingMapper.ToDomain(row);
    }

    public async Task<IReadOnlyList<Receipt>> AwaitingClearanceAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.Receipts
            .AsNoTracking()
            .Where(receipt => receipt.Status == (int)ReceiptStatus.Recorded)
            .OrderBy(receipt => receipt.ExpectedClearanceOn ?? receipt.ReceivedOn)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(LendingMapper.ToDomain)];
    }

    public async Task<IReadOnlyList<Receipt>> AwaitingAllocationAsync(
        CancellationToken cancellationToken = default)
    {
        // Cleared money that has not been fully applied to something. The unallocated part is
        // worked out in the domain, so the query fetches cleared receipts and the filter runs
        // on the aggregate - correct rather than clever, on a table of this size.
        var rows = await _context.Receipts
            .AsNoTracking()
            .Where(receipt => receipt.Status != (int)ReceiptStatus.Recorded)
            .OrderBy(receipt => receipt.ReceivedOn)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(LendingMapper.ToDomain).Where(receipt => !receipt.IsFullyAllocated),
        ];
    }

    public async Task<IReadOnlyList<Receipt>> BetweenAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var rows = await _context.Receipts
            .AsNoTracking()
            .Where(receipt => receipt.ReceivedOn >= from && receipt.ReceivedOn <= to)
            .OrderBy(receipt => receipt.ReceivedOn)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(LendingMapper.ToDomain)];
    }

    public void Add(Receipt receipt) => _context.Receipts.Add(LendingMapper.ToRow(receipt));

    public void Update(Receipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var existing =
            _context.Receipts.Local.FirstOrDefault(row => row.Id == receipt.Id.Value)
            ?? _context.Receipts
                .Include(row => row.Allocations)
                .First(row => row.Id == receipt.Id.Value);

        var updated = LendingMapper.ToRow(receipt);

        _context.Entry(existing).CurrentValues.SetValues(updated);
        ChildRows.Replace(_context.ReceiptAllocations, existing.Allocations, updated.Allocations);
    }
}

/// <summary>
/// Replaces an aggregate's child rows.
/// </summary>
/// <remarks>
/// Old rows are deleted through their DbSet and new ones inserted through it, and the parent's
/// navigation property is left alone. Reassigning the navigation instead looks tidier but
/// leaves the change tracker holding a collection it no longer owns, and the resulting failure
/// surfaces as an unrelated concurrency exception somewhere else in the save.
///
/// Wholesale replacement is fine for these: a handful of guarantors, a couple of approval
/// decisions, edited one at a time by one clerk on a LAN. The ledger, where it would be
/// unthinkable, has no Update at all.
/// </remarks>
internal static class ChildRows
{
    public static void Replace<TRow>(
        DbSet<TRow> set,
        IEnumerable<TRow> existing,
        IEnumerable<TRow> replacement)
        where TRow : class
    {
        foreach (var row in existing.ToList())
        {
            set.Remove(row);
        }

        foreach (var row in replacement)
        {
            set.Add(row);
        }
    }
}
