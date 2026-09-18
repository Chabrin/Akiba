using Akiba.Domain.Financial;

namespace Akiba.Domain.Reconciliation;

/// <summary>
/// What Akiba asked HR to deduct from one payslip.
/// </summary>
/// <param name="PayrollNumber">What HR matches on.</param>
/// <param name="MemberName">Whose payslip, as Akiba holds the name.</param>
/// <param name="ShareContribution">The member's chosen monthly amount.</param>
/// <param name="LoanInstalments">Instalments falling due this month.</param>
public sealed record PayrollExpectation(
    string PayrollNumber,
    string MemberName,
    Money ShareContribution,
    Money LoanInstalments)
{
    public Money Expected => ShareContribution + LoanInstalments;
}

/// <summary>
/// What HR says they actually deducted, as returned with the cheque.
/// </summary>
/// <param name="PayrollNumber">HR's own staff number.</param>
/// <param name="NameAsHrWroteIt">
/// Kept as HR wrote it. When a payroll number does not match anything, the name is all the
/// clerk has to go on.
/// </param>
/// <param name="Deducted">The figure on the payslip.</param>
public sealed record PayrollActual(
    string PayrollNumber,
    string NameAsHrWroteIt,
    Money Deducted);

/// <summary>What happened to one person's deduction.</summary>
public enum PayrollVarianceKind
{
    /// <summary>HR deducted exactly what was asked.</summary>
    Agreed = 1,

    /// <summary>HR deducted less. Usually an affordability cap, unpaid leave, or a part month.</summary>
    UnderDeducted = 2,

    /// <summary>HR deducted more than was asked.</summary>
    OverDeducted = 3,

    /// <summary>On the schedule, but HR's return does not mention them at all.</summary>
    NotDeducted = 4,

    /// <summary>
    /// HR deducted from somebody Akiba did not ask about.
    /// </summary>
    /// <remarks>
    /// The one to look at first. Either a member is missing from the schedule, or money has
    /// been taken from an employee who never agreed to it.
    /// </remarks>
    NotOnSchedule = 5,
}

/// <summary>One person's line on the payroll reconciliation.</summary>
/// <param name="PayrollNumber">What the two sides were matched on.</param>
/// <param name="MemberName">Akiba's name for them, or HR's where Akiba has none.</param>
/// <param name="Expected">What the schedule asked for.</param>
/// <param name="Deducted">What HR says came off.</param>
/// <param name="Kind">How the two differ, if they do.</param>
public sealed record PayrollVariance(
    string PayrollNumber,
    string MemberName,
    Money Expected,
    Money Deducted,
    PayrollVarianceKind Kind)
{
    /// <summary>Deducted less expected. Negative where HR took less than was asked.</summary>
    public Money Variance => Deducted - Expected;

    public bool NeedsAttention => Kind != PayrollVarianceKind.Agreed;

    /// <summary>What the clerk does about it, in plain words.</summary>
    public string ClerkTask => Kind switch
    {
        PayrollVarianceKind.Agreed => "Nothing.",

        PayrollVarianceKind.UnderDeducted =>
            $"{MemberName} is short by {Variance.Abs()}. Find out why before allocating - if the " +
            "shortfall is on a loan instalment the loan falls into arrears whether or not HR " +
            "explains it.",

        PayrollVarianceKind.OverDeducted =>
            $"{MemberName} was over-deducted by {Variance}. It is theirs: either add it to " +
            "shares or refund it, and record which.",

        PayrollVarianceKind.NotDeducted =>
            $"Nothing came off for {MemberName}, who was asked for {Expected}. Any loan " +
            "instalment in that figure is now unpaid.",

        PayrollVarianceKind.NotOnSchedule =>
            $"HR deducted {Deducted} from payroll number {PayrollNumber}, whom Akiba did not " +
            "ask about. Establish whose money this is before receipting it.",

        _ => "Unrecognised variance.",
    };
}

/// <summary>
/// The month's deduction schedule set against what HR actually took.
/// </summary>
/// <remarks>
/// <para>
/// The schedule reaches HR by the 25th, HR runs the payroll, and a cheque comes back. This is
/// where the office finds out whether the cheque covers what was asked for - and, more to the
/// point, <i>whose</i> loan instalment did not come off, because an instalment HR skipped is a
/// loan in arrears no matter how tidy the total looks.
/// </para>
/// <para>
/// It is a comparison, not a correction. Nothing here changes a loan or posts an entry: the
/// clerk reads the variances and acts on each one, and each action is recorded on its own.
/// </para>
/// </remarks>
public sealed record PayrollReconciliation(
    int Year,
    int Month,
    IReadOnlyList<PayrollVariance> Lines,
    Money ChequeReceived)
{
    public Money TotalExpected => Lines.Sum(line => line.Expected, Currency.Kes);

    public Money TotalDeducted => Lines.Sum(line => line.Deducted, Currency.Kes);

    /// <summary>What HR took less what was asked for.</summary>
    public Money TotalVariance => TotalDeducted - TotalExpected;

    /// <summary>
    /// The cheque less the deductions HR listed.
    /// </summary>
    /// <remarks>
    /// A different question from <see cref="TotalVariance"/>, and worth asking separately. A
    /// cheque that does not equal HR's own schedule is an error in the payment; a schedule that
    /// does not equal Akiba's is an error in the deduction.
    /// </remarks>
    public Money ChequeDifference => ChequeReceived - TotalDeducted;

    public IReadOnlyList<PayrollVariance> NeedingAttention =>
        [.. Lines.Where(line => line.NeedsAttention)];

    public bool Agrees => NeedingAttention.Count == 0 && ChequeDifference.IsZero;

    /// <summary>
    /// Compares a schedule with HR's return.
    /// </summary>
    /// <param name="year">The payroll month.</param>
    /// <param name="month">The payroll month.</param>
    /// <param name="expectations">What Akiba asked for. Members without a payroll number are skipped.</param>
    /// <param name="actuals">What HR says came off.</param>
    /// <param name="chequeReceived">The cheque that came back with it.</param>
    /// <remarks>
    /// Members who are not on the CAL payroll are left out entirely rather than shown as
    /// not deducted. HR cannot deduct from somebody who has no payslip, so listing them as a
    /// failure would bury the real ones - the society's own register carries several such
    /// shareholders.
    /// </remarks>
    public static PayrollReconciliation Compare(
        int year,
        int month,
        IEnumerable<PayrollExpectation> expectations,
        IEnumerable<PayrollActual> actuals,
        Money chequeReceived)
    {
        ArgumentNullException.ThrowIfNull(expectations);
        ArgumentNullException.ThrowIfNull(actuals);

        var asked = expectations
            .Where(expectation => !string.IsNullOrWhiteSpace(expectation.PayrollNumber))
            .ToDictionary(expectation => expectation.PayrollNumber.Trim(), StringComparer.OrdinalIgnoreCase);

        var returned = actuals
            .ToDictionary(actual => actual.PayrollNumber.Trim(), StringComparer.OrdinalIgnoreCase);

        var lines = new List<PayrollVariance>(asked.Count + returned.Count);

        foreach (var (payrollNumber, expectation) in asked)
        {
            if (!returned.TryGetValue(payrollNumber, out var actual))
            {
                lines.Add(new PayrollVariance(
                    payrollNumber,
                    expectation.MemberName,
                    expectation.Expected,
                    Money.Zero(expectation.Expected.Currency),
                    PayrollVarianceKind.NotDeducted));

                continue;
            }

            var expected = expectation.Expected.Round();
            var deducted = actual.Deducted.Round();

            var kind = deducted == expected
                ? PayrollVarianceKind.Agreed
                : deducted < expected
                    ? PayrollVarianceKind.UnderDeducted
                    : PayrollVarianceKind.OverDeducted;

            lines.Add(new PayrollVariance(
                payrollNumber, expectation.MemberName, expected, deducted, kind));
        }

        foreach (var (payrollNumber, actual) in returned.Where(entry => !asked.ContainsKey(entry.Key)))
        {
            lines.Add(new PayrollVariance(
                payrollNumber,
                actual.NameAsHrWroteIt,
                Money.Zero(actual.Deducted.Currency),
                actual.Deducted.Round(),
                PayrollVarianceKind.NotOnSchedule));
        }

        return new PayrollReconciliation(
            year,
            month,
            [
                .. lines
                    .OrderBy(line => line.Kind == PayrollVarianceKind.Agreed ? 1 : 0)
                    .ThenBy(line => line.Kind)
                    .ThenBy(line => line.PayrollNumber, StringComparer.Ordinal),
            ],
            chequeReceived.Round());
    }

    /// <summary>What the month came to, in words.</summary>
    public string Verdict => Agrees
        ? "HR deducted exactly what the schedule asked for, and the cheque covers it."
        : $"{NeedingAttention.Count} line(s) need attention" +
          (ChequeDifference.IsZero
              ? "."
              : $", and the cheque is out by {ChequeDifference.Abs()}.");
}
