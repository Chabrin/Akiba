namespace Akiba.Domain.Ledger;

/// <summary>What kind of paper an entry came from.</summary>
public enum SourceDocumentKind
{
    /// <summary>No document - opening balances and internal adjustments.</summary>
    None = 0,
    LoanApplication = 1,
    PaymentVoucher = 2,
    Cheque = 3,
    BankDeposit = 4,
    MpesaReceipt = 5,
    PayrollSchedule = 6,
    LandlordSchedule = 7,
    BankStatement = 8,
    DividendSchedule = 9,
    OpeningBalance = 10,
    Correction = 11,
}

/// <summary>
/// The document an entry was posted from: a cheque number, a payment voucher reference, the
/// payroll schedule for a month.
/// </summary>
/// <remarks>
/// Every journal entry carries one. This is what lets an official walk from a figure on a
/// screen back to a piece of paper in a file - the property the paper ledger had, and the
/// one a system of record cannot afford to lose.
/// </remarks>
public readonly record struct SourceDocument
{
    private SourceDocument(SourceDocumentKind kind, string reference)
    {
        Kind = kind;
        Reference = reference;
    }

    public SourceDocumentKind Kind { get; }

    /// <summary>The document's own reference: a cheque number, a voucher number, "2026-09".</summary>
    public string Reference { get; }

    /// <summary>For entries that genuinely have no source document.</summary>
    public static SourceDocument None => new(SourceDocumentKind.None, string.Empty);

    public static SourceDocument Of(SourceDocumentKind kind, string reference)
    {
        if (kind == SourceDocumentKind.None)
        {
            return None;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        return new SourceDocument(kind, reference.Trim());
    }

    public static SourceDocument Cheque(string chequeNumber) =>
        Of(SourceDocumentKind.Cheque, chequeNumber);

    public static SourceDocument PaymentVoucher(string voucherNumber) =>
        Of(SourceDocumentKind.PaymentVoucher, voucherNumber);

    public static SourceDocument PayrollSchedule(int year, int month) =>
        Of(SourceDocumentKind.PayrollSchedule, $"{year:D4}-{month:D2}");

    public override string ToString() =>
        Kind == SourceDocumentKind.None ? "(no document)" : $"{Kind} {Reference}";
}
