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

    /// <summary>
    /// Reads a name written as one string, the way a register writes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "LUCY WANJIRU KARANJA" becomes given Lucy, other Wanjiru, family Karanja. Kenyan naming
    /// commonly runs to three names in that order, so this is right far more often than not -
    /// but it is a guess, and the migration dry run shows every name so the clerk can see what
    /// it made of theirs.
    /// </para>
    /// <para>
    /// The capitals are dropped. A register shouting every name is a formatting habit of the
    /// spreadsheet, not how anybody writes their own name, and storing it would put that habit
    /// on every statement Akiba ever sends. The trade is that a name which is genuinely
    /// irregular - McDonald, or an initial - comes back plainer than it went in, and the clerk
    /// corrects it from the member's file.
    /// </para>
    /// </remarks>
    public static PersonName FromRegister(string written)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(written);

        var parts = written
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Capitalise)
            .ToArray();

        return parts.Length switch
        {
            1 => new PersonName(parts[0], parts[0]),
            2 => new PersonName(parts[0], parts[1]),
            _ => new PersonName(parts[0], parts[^1], string.Join(' ', parts[1..^1])),
        };
    }

    /// <summary>
    /// "KARANJA" becomes "Karanja". Left alone if it is already mixed case, since somebody has
    /// then already made a decision about it.
    /// </summary>
    private static string Capitalise(string part)
    {
        if (part.Any(char.IsLower))
        {
            return part;
        }

        return string.Create(part.Length, part, (span, source) =>
        {
            span[0] = char.ToUpperInvariant(source[0]);

            for (var index = 1; index < source.Length; index++)
            {
                span[index] = char.ToLowerInvariant(source[index]);
            }
        });
    }

    public override string ToString() => Full;
}
