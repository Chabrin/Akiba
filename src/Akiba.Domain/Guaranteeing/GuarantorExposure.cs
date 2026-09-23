using System.Globalization;
using Akiba.Domain.Financial;
using Akiba.Domain.Membership;

namespace Akiba.Domain.Guaranteeing;

/// <summary>
/// One loan a member guarantees, and what it currently exposes them to.
/// </summary>
/// <param name="LoanId">The loan.</param>
/// <param name="LoanNumber">Its human-readable number.</param>
/// <param name="BorrowerName">Who borrowed.</param>
/// <param name="GuaranteedAmount">What this guarantor put their name to.</param>
/// <param name="OutstandingBalance">What the borrower still owes.</param>
/// <param name="BorrowerShareholding">The borrower's own shares, which stand behind the loan first.</param>
/// <param name="AmountAtRisk">
/// What this guarantor would be pursued for today, under the liability basis in force.
/// </param>
/// <param name="IsInArrears">Whether the loan is behind its schedule.</param>
public sealed record GuaranteedLoanExposure(
    Guid LoanId,
    string LoanNumber,
    string BorrowerName,
    Money GuaranteedAmount,
    Money OutstandingBalance,
    Money BorrowerShareholding,
    Money AmountAtRisk,
    bool IsInArrears)
{
    /// <summary>
    /// Whether the borrower's own shares fall short of what they owe.
    /// </summary>
    /// <remarks>
    /// This is the test that matters when a guarantor exits CAL: the office asks for a
    /// replacement only where the loan balance exceeds the borrower's shares, and holds the
    /// exiting member's funds until one is found.
    /// </remarks>
    public bool BorrowerSharesFallShort => OutstandingBalance > BorrowerShareholding;
}

/// <summary>
/// What one member is exposed to across every loan they guarantee.
/// </summary>
/// <param name="GuarantorId">The member.</param>
/// <param name="GuarantorName">Their name.</param>
/// <param name="Loans">Every loan they guarantee.</param>
public sealed record GuarantorExposure(
    BorrowerId GuarantorId,
    string GuarantorName,
    IReadOnlyList<GuaranteedLoanExposure> Loans)
{
    /// <summary>The total this member has put their name to, across all loans.</summary>
    public Money TotalGuaranteed =>
        Loans.Sum(loan => loan.GuaranteedAmount, Currency.Kes);

    /// <summary>What they would be pursued for today if every loan defaulted at once.</summary>
    public Money TotalAtRisk =>
        Loans.Sum(loan => loan.AmountAtRisk, Currency.Kes);

    /// <summary>
    /// The loans that would block release of this member's own funds were they to leave CAL.
    /// </summary>
    public IReadOnlyList<GuaranteedLoanExposure> LoansBlockingRelease =>
        [.. Loans.Where(loan => loan.BorrowerSharesFallShort)];

    /// <summary>
    /// Whether this member's funds may be released if they leave.
    /// </summary>
    /// <remarks>
    /// "In cases where the amount of loan balances exceeds the member's shares, we usually
    /// inform the member to replace the exiting member... Until such a replacement is found,
    /// we do not release funds to the exiting member."
    /// </remarks>
    public bool FundsMayBeReleased => LoansBlockingRelease.Count == 0;

    public IReadOnlyList<GuaranteedLoanExposure> LoansInArrears =>
        [.. Loans.Where(loan => loan.IsInArrears)];
}

/// <summary>
/// Builds the guarantor exposure report, and the review that a member's exit triggers.
/// </summary>
public static class GuarantorExposureReport
{
    /// <summary>
    /// Builds one member's exposure from the loans they guarantee.
    /// </summary>
    /// <param name="guarantorId">The member.</param>
    /// <param name="guarantorName">Their name.</param>
    /// <param name="loans">Every loan they guarantee, with its current position.</param>
    public static GuarantorExposure For(
        BorrowerId guarantorId,
        string guarantorName,
        IReadOnlyList<GuaranteedLoanExposure> loans)
    {
        ArgumentNullException.ThrowIfNull(loans);

        return new GuarantorExposure(guarantorId, guarantorName, loans);
    }

    /// <summary>
    /// What must happen when a guarantor leaves CAL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A guarantor must be a current CAL employee, so an exit puts every loan they guarantee
    /// in question. The office's practice is narrower than that, though: a replacement is
    /// asked for only where the loan balance exceeds the borrower's own shares, because
    /// otherwise the borrower's shares already stand behind it.
    /// </para>
    /// <para>
    /// What happens when <i>every</i> guarantor on a loan has gone and no replacement can be
    /// found is not answered anywhere - see docs/open-questions.md, item 13. This reports the
    /// position and raises the task; it takes no automatic action.
    /// </para>
    /// </remarks>
    public static GuarantorExitReview ReviewExit(GuarantorExposure exposure, DateOnly exitedOn)
    {
        ArgumentNullException.ThrowIfNull(exposure);

        return new GuarantorExitReview(
            exposure.GuarantorId,
            exposure.GuarantorName,
            exitedOn,
            exposure.LoansBlockingRelease,
            exposure.Loans);
    }
}

/// <summary>
/// The position when a guarantor leaves CAL: what needs a replacement, and whether their own
/// funds may be released.
/// </summary>
/// <param name="GuarantorId">The departing member.</param>
/// <param name="GuarantorName">Their name.</param>
/// <param name="ExitedOn">When they left.</param>
/// <param name="LoansNeedingReplacement">
/// Loans whose balance exceeds the borrower's own shares. The borrower is asked to find a
/// replacement guarantor for each.
/// </param>
/// <param name="AllGuaranteedLoans">Every loan they guarantee, flagged for review.</param>
public sealed record GuarantorExitReview(
    BorrowerId GuarantorId,
    string GuarantorName,
    DateOnly ExitedOn,
    IReadOnlyList<GuaranteedLoanExposure> LoansNeedingReplacement,
    IReadOnlyList<GuaranteedLoanExposure> AllGuaranteedLoans)
{
    /// <summary>
    /// Whether the departing member's own shares and final dues may be paid out.
    /// </summary>
    public bool FundsMayBeReleased => LoansNeedingReplacement.Count == 0;

    /// <summary>
    /// The task raised for the accounts clerk, in the words they would use.
    /// </summary>
    /// <remarks>
    /// The date is written out rather than printed as yyyy-MM-dd. This string is read by a
    /// person and appears on screen beside the member's name; an ISO date in a sentence is a
    /// machine's idea of a date, and it is the sort of thing that makes an office decide the
    /// system was not built for them.
    ///
    /// The invariant culture is used deliberately, so the wording is identical on every
    /// machine in the office regardless of what Windows is set to.
    /// </remarks>
    public string ClerkTask => FundsMayBeReleased
        ? $"They guarantee {AllGuaranteedLoans.Count} loan(s), all of them covered by the " +
          "borrowers' own shares. Their funds may be released."
        : $"{LoansNeedingReplacement.Count} of the {AllGuaranteedLoans.Count} loan(s) they " +
          "guarantee exceed the borrower's own shares. Ask those borrowers for replacement " +
          "guarantors. Do not release this member's funds until replacements are found.";

    /// <summary>Who left and when, written out for a person to read.</summary>
    public string Departure =>
        $"{GuarantorName} left CAL on " +
        $"{ExitedOn.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}.";
}
