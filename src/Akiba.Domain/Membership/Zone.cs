using Akiba.Domain.Common;

namespace Akiba.Domain.Membership;

/// <summary>
/// A zone or the office. Loan applications are approved by the member representatives for
/// the applicant's zone.
/// </summary>
/// <remarks>
/// <b>The list of zones is one of the open questions.</b> Approval routes through zone
/// representatives, but no source document lists the zones. This type exists so the approval
/// workflow has something real to route through; the zones themselves are seeded empty and
/// entered by the accounts clerk once the committee supplies them.
/// See docs/open-questions.md, item 9.
/// </remarks>
public sealed class Zone : AggregateRoot<ZoneId>
{
    private readonly List<BorrowerId> _representatives = [];

    private Zone(ZoneId id, string code, string name, bool isOffice)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Code = code.Trim().ToUpperInvariant();
        Name = name.Trim();
        IsOffice = isOffice;
    }

    public string Code { get; private set; }

    public string Name { get; private set; }

    /// <summary>True for the office, which approves alongside the zones.</summary>
    public bool IsOffice { get; private set; }

    public bool IsActive { get; private set; } = true;

    /// <summary>
    /// The members who approve applications from this zone. A loan needs a decision from
    /// these people; there is no single official who can approve alone.
    /// </summary>
    public IReadOnlyList<BorrowerId> Representatives => _representatives;

    public static Zone Create(string code, string name) => new(ZoneId.New(), code, name, isOffice: false);

    public static Zone CreateOffice(string code, string name) => new(ZoneId.New(), code, name, isOffice: true);

    /// <summary>Rebuilds a zone from storage. For the persistence layer only.</summary>
    public static Zone Rehydrate(
        ZoneId id,
        string code,
        string name,
        bool isOffice,
        bool isActive,
        IEnumerable<BorrowerId> representatives)
    {
        var zone = new Zone(id, code, name, isOffice) { IsActive = isActive };
        zone._representatives.AddRange(representatives);
        return zone;
    }

    public void AppointRepresentative(BorrowerId memberId)
    {
        if (!memberId.IsSpecified)
        {
            throw new ArgumentException("A representative must be a member.", nameof(memberId));
        }

        if (_representatives.Contains(memberId))
        {
            return;
        }

        _representatives.Add(memberId);
    }

    public void RemoveRepresentative(BorrowerId memberId) => _representatives.Remove(memberId);

    public void Deactivate() => IsActive = false;

    public override string ToString() => $"{Code} {Name}";
}
