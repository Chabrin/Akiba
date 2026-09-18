namespace Akiba.Domain.Membership;

/// <summary>
/// A CAL payroll number. HR matches the monthly deduction schedule on this.
/// </summary>
/// <remarks>
/// <para>
/// The schedule must carry payroll numbers and full names - HR asked for both - so this is
/// part of the domain rather than an incidental field.
/// </para>
/// <para>
/// <b>It is optional.</b> The deduction register shows shareholders who are not on the CAL
/// payroll: two of them share staff number <c>0</c>, which is the office's way of writing "no
/// payroll number". They hold shares and are deducted by other means. A required, unique
/// payroll number would have refused to load the society's own records.
/// </para>
/// <para>
/// <see cref="None"/> represents an absent number, and several members may have it. A number
/// that <i>is</i> present is unique, because HR matches on it.
/// </para>
/// </remarks>
public readonly record struct PayrollNumber
{
    private PayrollNumber(string value) => Value = value;

    public string Value { get; }

    public bool IsSpecified => !string.IsNullOrEmpty(Value);

    /// <summary>A shareholder who is not on the CAL payroll.</summary>
    public static PayrollNumber None => default;

    public static PayrollNumber Of(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim().ToUpperInvariant();

        if (trimmed.Length > 20 || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-'))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid payroll number.", nameof(value));
        }

        return new PayrollNumber(trimmed);
    }

    /// <summary>
    /// Reads a payroll number from a register, treating the office's stand-ins for "none" as
    /// absent.
    /// </summary>
    /// <remarks>
    /// The register writes <c>0</c> for a shareholder who is not on the payroll, and more than
    /// one person carries it. Taken literally it is a duplicate key; taken as the office means
    /// it, it is a blank.
    /// </remarks>
    public static PayrollNumber FromRegister(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return None;
        }

        var trimmed = value.Trim();

        return trimmed is "0" or "-" or "N/A" or "NA" ? None : Of(trimmed);
    }

    public override string ToString() => IsSpecified ? Value : "(not on payroll)";
}

/// <summary>A Kenyan national identity card number.</summary>
public readonly record struct NationalId
{
    private NationalId(string value) => Value = value;

    public string Value { get; }

    public bool IsSpecified => !string.IsNullOrEmpty(Value);

    /// <summary>
    /// Not recorded yet.
    /// </summary>
    /// <remarks>
    /// The deduction register carries names and shareholdings and nothing else, so members
    /// imported from it have no ID until the clerk enters one from their file. Absent is an
    /// honest state; a made-up number would not be.
    /// </remarks>
    public static NationalId Unknown => default;

    public static NationalId Of(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var digits = value.Trim();

        if (digits.Length is < 5 or > 12 || !digits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid national ID number.", nameof(value));
        }

        return new NationalId(digits);
    }

    public override string ToString() => IsSpecified ? Value : "(no ID)";
}

/// <summary>
/// The member's number within Akiba, used on the deduction schedule and as the suffix of
/// their share account code.
/// </summary>
public readonly record struct MembershipNumber
{
    private MembershipNumber(string value) => Value = value;

    public string Value { get; }

    public bool IsSpecified => !string.IsNullOrEmpty(Value);

    public static MembershipNumber Of(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim().ToUpperInvariant();

        if (trimmed.Length > 20 || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid membership number.", nameof(value));
        }

        return new MembershipNumber(trimmed);
    }

    public override string ToString() => IsSpecified ? Value : "(no membership number)";
}

/// <summary>
/// A Kenyan mobile number, normalised to international form.
/// </summary>
/// <remarks>
/// Normalised on the way in because the same number gets written as 0712..., +254712... and
/// 254712... on different forms, and SMS dispatch needs one of them. Storing whatever was
/// typed would mean deciding again at every send.
/// </remarks>
public readonly record struct PhoneNumber
{
    private PhoneNumber(string value) => Value = value;

    /// <summary>The number in international form, e.g. <c>+254712345678</c>.</summary>
    public string Value { get; }

    public bool IsSpecified => !string.IsNullOrEmpty(Value);

    /// <summary>Not recorded yet. See <see cref="NationalId.Unknown"/>.</summary>
    public static PhoneNumber Unknown => default;

    public static PhoneNumber Of(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var digits = new string([.. value.Where(char.IsAsciiDigit)]);

        var national = digits switch
        {
            // 0712345678 -> 712345678
            { Length: 10 } when digits[0] == '0' => digits[1..],
            // 254712345678 -> 712345678
            { Length: 12 } when digits.StartsWith("254", StringComparison.Ordinal) => digits[3..],
            // 712345678, already national
            { Length: 9 } => digits,
            _ => throw new ArgumentException(
                $"'{value}' is not a recognisable Kenyan mobile number.", nameof(value)),
        };

        return new PhoneNumber("+254" + national);
    }

    public override string ToString() => IsSpecified ? Value : "(no phone number)";
}
