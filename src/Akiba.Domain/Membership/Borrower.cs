using Akiba.Domain.Common;

namespace Akiba.Domain.Membership;

/// <summary>
/// The party that borrows.
/// </summary>
/// <remarks>
/// <para>
/// Akiba lends to two kinds of party. <see cref="Member"/> is a CAL employee who holds
/// shares, borrows against them at a flat 10%, and repays by salary deduction. A
/// <see cref="ClientBorrower"/> is not a CAL employee, holds no shares, and borrows at 3%
/// per month for at most five months.
/// </para>
/// <para>
/// Clients are modelled here rather than forced into the member table, because a member
/// record whose shareholding is permanently zero and whose payroll number is blank is a
/// record that lies about what it is - and every rule keyed on shares would then need a
/// special case.
/// </para>
/// </remarks>
public abstract class Borrower : AggregateRoot<BorrowerId>
{
    private readonly List<AttachedDocument> _documents = [];

    protected Borrower(
        BorrowerId id,
        PersonName name,
        NationalId nationalId,
        PhoneNumber phone,
        string? email)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(name);

        Name = name;
        NationalId = nationalId;
        Phone = phone;
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
    }

    public PersonName Name { get; private set; }

    public NationalId NationalId { get; private set; }

    public PhoneNumber Phone { get; private set; }

    /// <summary>
    /// Optional. Statements are emailed where an address exists - the committee settled on
    /// email as the member-facing channel, since there is no member portal.
    /// </summary>
    public string? Email { get; private set; }

    /// <summary>Scanned identity documents, application forms and vouchers.</summary>
    public IReadOnlyList<AttachedDocument> Documents => _documents;

    /// <summary>True for a <see cref="Member"/>, false for a non-member client.</summary>
    public abstract bool HoldsShares { get; }

    public void UpdateContactDetails(PhoneNumber phone, string? email)
    {
        Phone = phone;
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
    }

    public void CorrectName(PersonName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    public void Attach(AttachedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _documents.Add(document);
    }

    public override string ToString() => Name.Full;
}

/// <summary>What a scanned document is.</summary>
public enum AttachedDocumentKind
{
    NationalIdCopy = 1,
    PassportPhoto = 2,
    MembershipApplicationLetter = 3,
    LoanApplicationForm = 4,
    PaymentVoucher = 5,
    RentStatement = 6,
    Other = 99,
}

/// <summary>
/// A scan filed against a borrower or a loan.
/// </summary>
/// <remarks>
/// Akiba runs on paper that members sign. The system records the figures; the scan is the
/// evidence, and an official must be able to get from one to the other.
/// </remarks>
public sealed record AttachedDocument
{
    public AttachedDocument(
        AttachedDocumentKind kind,
        string fileName,
        string storagePath,
        DateOnly receivedOn,
        string? note = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        Kind = kind;
        FileName = fileName.Trim();
        StoragePath = storagePath.Trim();
        ReceivedOn = receivedOn;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public AttachedDocumentKind Kind { get; }

    public string FileName { get; }

    /// <summary>Where the file sits in the file store, not on anyone's desktop.</summary>
    public string StoragePath { get; }

    public DateOnly ReceivedOn { get; }

    public string? Note { get; }
}
