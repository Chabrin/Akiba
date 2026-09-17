namespace Akiba.Domain.Membership;

/// <summary>
/// A person's name as it appears on the application form and the deduction schedule.
/// </summary>
/// <remarks>
/// Held as given and family name rather than as a single string, because the deduction
/// schedule sent to HR requires full names against payroll numbers and HR matches on them.
/// Kenyan naming often runs to three names; the middle one lives in <see cref="OtherNames"/>.
/// </remarks>
public sealed record PersonName
{
    public PersonName(string givenName, string familyName, string? otherNames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(givenName);
        ArgumentException.ThrowIfNullOrWhiteSpace(familyName);

        GivenName = givenName.Trim();
        FamilyName = familyName.Trim();
        OtherNames = string.IsNullOrWhiteSpace(otherNames) ? null : otherNames.Trim();
    }

    public string GivenName { get; }

    public string FamilyName { get; }

    public string? OtherNames { get; }

    /// <summary>All names in order, as written on the form.</summary>
    public string Full => OtherNames is null
        ? $"{GivenName} {FamilyName}"
        : $"{GivenName} {OtherNames} {FamilyName}";

    public override string ToString() => Full;
}
