namespace Akiba.Infrastructure.Persistence.Rows;

/// <summary>
/// The stored shape of a borrower.
/// </summary>
/// <remarks>
/// Members and non-member clients share a table with a discriminator, because a loan is made
/// to a borrower and does not care which kind it is. The member-only columns are nullable and
/// the mapper refuses to build a member without them.
/// </remarks>
internal sealed class BorrowerRow
{
    public Guid Id { get; set; }

    /// <summary>1 for a member, 2 for a non-member client.</summary>
    public int Kind { get; set; }

    public string GivenName { get; set; } = string.Empty;

    public string FamilyName { get; set; } = string.Empty;

    public string? OtherNames { get; set; }

    public string NationalId { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    public string? Email { get; set; }

    // --- Member only -------------------------------------------------
    public string? MembershipNumber { get; set; }

    public string? PayrollNumber { get; set; }

    public Guid? ZoneId { get; set; }

    /// <summary>The member's share account. Their shareholding is its balance as at a date.</summary>
    public Guid? SharesAccountId { get; set; }

    public int? EmploymentStatus { get; set; }

    public DateOnly? ExitedOn { get; set; }

    public bool IsLandlord { get; set; }

    // --- Client only -------------------------------------------------
    public string? IntroducedBy { get; set; }

    public List<AttachedDocumentRow> Documents { get; set; } = [];
}

/// <summary>A scan filed against a borrower or an application.</summary>
internal sealed class AttachedDocumentRow
{
    public Guid Id { get; set; }

    public Guid? BorrowerId { get; set; }

    public Guid? LoanApplicationId { get; set; }

    public int Kind { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string StoragePath { get; set; } = string.Empty;

    public DateOnly ReceivedOn { get; set; }

    public string? Note { get; set; }
}

internal sealed class ZoneRow
{
    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsOffice { get; set; }

    public bool IsActive { get; set; }

    public List<ZoneRepresentativeRow> Representatives { get; set; } = [];
}

/// <summary>A member who approves applications from a zone.</summary>
internal sealed class ZoneRepresentativeRow
{
    public Guid ZoneId { get; set; }

    public Guid MemberId { get; set; }
}
