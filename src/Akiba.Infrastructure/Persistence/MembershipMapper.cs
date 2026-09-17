using Akiba.Domain.Ledger;
using Akiba.Domain.Membership;
using Akiba.Infrastructure.Persistence.Rows;

namespace Akiba.Infrastructure.Persistence;

/// <summary>Which kind of borrower a row holds.</summary>
internal static class BorrowerKind
{
    public const int Member = 1;
    public const int Client = 2;
}

/// <summary>
/// Translates between borrower rows and the domain.
/// </summary>
/// <remarks>
/// Members and clients share a table. The member-only columns are nullable, and this mapper
/// refuses to build a member without them rather than defaulting them - a member with no
/// share account could not have a shareholding, and that is not a state worth tolerating.
/// </remarks>
internal static class MembershipMapper
{
    public static BorrowerRow ToRow(Borrower borrower) => borrower switch
    {
        Member member => new BorrowerRow
        {
            Id = member.Id.Value,
            Kind = BorrowerKind.Member,
            GivenName = member.Name.GivenName,
            FamilyName = member.Name.FamilyName,
            OtherNames = member.Name.OtherNames,
            NationalId = member.NationalId.Value,
            Phone = member.Phone.Value,
            Email = member.Email,
            MembershipNumber = member.MembershipNumber.Value,
            PayrollNumber = member.PayrollNumber.Value,
            ZoneId = member.ZoneId.Value,
            SharesAccountId = member.SharesAccountId.Value,
            EmploymentStatus = (int)member.EmploymentStatus,
            ExitedOn = member.ExitedOn,
            IsLandlord = member.IsLandlord,
            Documents = [.. member.Documents.Select(document => ToRow(document, member.Id.Value, null))],
        },
        ClientBorrower client => new BorrowerRow
        {
            Id = client.Id.Value,
            Kind = BorrowerKind.Client,
            GivenName = client.Name.GivenName,
            FamilyName = client.Name.FamilyName,
            OtherNames = client.Name.OtherNames,
            NationalId = client.NationalId.Value,
            Phone = client.Phone.Value,
            Email = client.Email,
            IntroducedBy = client.IntroducedBy,
            Documents = [.. client.Documents.Select(document => ToRow(document, client.Id.Value, null))],
        },
        _ => throw new ArgumentOutOfRangeException(
            nameof(borrower), borrower?.GetType().Name, "Unknown kind of borrower."),
    };

    public static Borrower ToDomain(BorrowerRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var name = new PersonName(row.GivenName, row.FamilyName, row.OtherNames);
        var nationalId = NationalId.Of(row.NationalId);
        var phone = PhoneNumber.Of(row.Phone);

        if (row.Kind == BorrowerKind.Client)
        {
            var client = ClientBorrower.Rehydrate(
                new BorrowerId(row.Id), name, nationalId, phone, row.Email, row.IntroducedBy);

            Attach(client, row);

            return client;
        }

        var member = Member.Rehydrate(
            new BorrowerId(row.Id),
            MembershipNumber.Of(Required(row.MembershipNumber, row.Id, "membership number")),
            PayrollNumber.Of(Required(row.PayrollNumber, row.Id, "payroll number")),
            name,
            nationalId,
            phone,
            row.Email,
            new ZoneId(row.ZoneId ?? throw Missing(row.Id, "zone")),
            new AccountId(row.SharesAccountId ?? throw Missing(row.Id, "share account")),
            (EmploymentStatus)(row.EmploymentStatus ?? (int)EmploymentStatus.Employed),
            row.ExitedOn,
            row.IsLandlord);

        Attach(member, row);

        return member;
    }

    public static Member ToMember(BorrowerRow row) =>
        ToDomain(row) as Member
        ?? throw new InvalidOperationException(
            $"Borrower {row?.Id} is a non-member client and holds no shares.");

    public static AttachedDocumentRow ToRow(
        AttachedDocument document, Guid? borrowerId, Guid? applicationId) => new()
        {
            Id = Guid.NewGuid(),
            BorrowerId = borrowerId,
            LoanApplicationId = applicationId,
            Kind = (int)document.Kind,
            FileName = document.FileName,
            StoragePath = document.StoragePath,
            ReceivedOn = document.ReceivedOn,
            Note = document.Note,
        };

    public static AttachedDocument ToDomain(AttachedDocumentRow row) => new(
        (AttachedDocumentKind)row.Kind, row.FileName, row.StoragePath, row.ReceivedOn, row.Note);

    public static ZoneRow ToRow(Zone zone) => new()
    {
        Id = zone.Id.Value,
        Code = zone.Code,
        Name = zone.Name,
        IsOffice = zone.IsOffice,
        IsActive = zone.IsActive,
        Representatives =
        [
            .. zone.Representatives.Select(representative => new ZoneRepresentativeRow
            {
                ZoneId = zone.Id.Value,
                MemberId = representative.Value,
            }),
        ],
    };

    public static Zone ToDomain(ZoneRow row) => Zone.Rehydrate(
        new ZoneId(row.Id),
        row.Code,
        row.Name,
        row.IsOffice,
        row.IsActive,
        row.Representatives.Select(representative => new BorrowerId(representative.MemberId)));

    private static void Attach(Borrower borrower, BorrowerRow row)
    {
        foreach (var document in row.Documents)
        {
            borrower.Attach(ToDomain(document));
        }
    }

    private static string Required(string? value, Guid id, string what) =>
        string.IsNullOrWhiteSpace(value) ? throw Missing(id, what) : value;

    private static InvalidOperationException Missing(Guid id, string what) =>
        new($"Borrower {id} is stored as a member but has no {what}. The row is inconsistent.");
}
