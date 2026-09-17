namespace Akiba.Domain.Membership;

/// <summary>
/// A borrower who is not a CAL employee and holds no shares.
/// </summary>
/// <remarks>
/// <para>
/// Clients borrow at 3% per month on the principal for at most five months - a different
/// product from the members' flat 10%.
/// </para>
/// <para>
/// The important difference is not the rate but the recovery. A member repays by deduction at
/// source and cannot miss a payment; a client pays by direct deposit and can. Arrears
/// handling for clients is one of the questions the committee has not answered - no penalty
/// rule exists and no definition of a missed payment was given.
/// </para>
/// </remarks>
public sealed class ClientBorrower : Borrower
{
    private ClientBorrower(
        BorrowerId id,
        PersonName name,
        NationalId nationalId,
        PhoneNumber phone,
        string? email,
        string? introducedBy)
        : base(id, name, nationalId, phone, email) =>
        IntroducedBy = string.IsNullOrWhiteSpace(introducedBy) ? null : introducedBy.Trim();

    /// <summary>
    /// Who brought the client to Akiba. Recorded because the group is informal and lending
    /// outside the membership rests on somebody vouching for the borrower.
    /// </summary>
    public string? IntroducedBy { get; private set; }

    public override bool HoldsShares => false;

    public static ClientBorrower Register(
        PersonName name,
        NationalId nationalId,
        PhoneNumber phone,
        string? email = null,
        string? introducedBy = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        return new ClientBorrower(BorrowerId.New(), name, nationalId, phone, email, introducedBy);
    }

    /// <summary>Rebuilds a client from storage. For the persistence layer only.</summary>
    public static ClientBorrower Rehydrate(
        BorrowerId id,
        PersonName name,
        NationalId nationalId,
        PhoneNumber phone,
        string? email,
        string? introducedBy) =>
        new(id, name, nationalId, phone, email, introducedBy);
}
