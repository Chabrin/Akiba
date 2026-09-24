namespace Akiba.Domain.Membership;

/// <summary>
/// How the society collects from somebody, which is the difference that matters about them.
/// </summary>
/// <remarks>
/// <para>
/// Akiba lends to three kinds of person and they are not variations on a theme — the money
/// reaches the society by three different routes, lands in two different bank accounts, and
/// reconciles against two different statements.
/// </para>
/// <para>
/// An <b>employee</b> is deducted from a CAL payslip. HR is sent one schedule, CAL writes one
/// cheque covering everybody on it, and that cheque is drawn on the business account.
/// </para>
/// <para>
/// A <b>landlord</b> is a member whose deduction is claimed against the rent CAL pays them.
/// They appear on a separate schedule, the cheque is drawn on the main account, and the two
/// reconcile independently — which is why a screen that mixes them makes an office check a
/// figure against the wrong statement.
/// </para>
/// <para>
/// A <b>client</b> is not a member at all. They hold no shares, they borrow on different terms,
/// and they pay in directly rather than through any schedule.
/// </para>
/// </remarks>
public enum BorrowerCategory
{
    /// <summary>A member deducted from a CAL payslip.</summary>
    Employee = 1,

    /// <summary>A member whose deduction is claimed against the rent CAL pays them.</summary>
    Landlord = 2,

    /// <summary>A non-member who borrows from the society and holds no shares.</summary>
    Client = 3,
}

/// <summary>Works out which category somebody falls into.</summary>
public static class BorrowerCategories
{
    /// <summary>
    /// The category this borrower belongs to.
    /// </summary>
    /// <remarks>
    /// One place, so the answer cannot drift between the members list, the loan book and the
    /// applications queue. A borrower who is not a member is a client by definition — there is
    /// no third kind of member, and a new one would have to be added here before any screen
    /// could quietly guess at it.
    /// </remarks>
    public static BorrowerCategory Of(Borrower? borrower) => borrower switch
    {
        Member { IsLandlord: true } => BorrowerCategory.Landlord,
        Member => BorrowerCategory.Employee,
        _ => BorrowerCategory.Client,
    };

    /// <summary>What an official would call this category on screen.</summary>
    public static string DisplayName(this BorrowerCategory category) => category switch
    {
        BorrowerCategory.Employee => "CAL employee",
        BorrowerCategory.Landlord => "Landlord",
        _ => "Client",
    };
}
