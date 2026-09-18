using System.Globalization;
using System.Text;
using Akiba.Application.Abstractions;
using Akiba.Domain.Reconciliation;
using ClosedXML.Excel;

namespace Akiba.Infrastructure.Reconciliation;

/// <summary>
/// Reads a bank statement out of a CSV or an Excel workbook.
/// </summary>
/// <remarks>
/// <para>
/// Kenyan banks export statements in their own shapes and change them without warning, so the
/// column names are matched loosely - "Value Date", "Date", "Txn Date" all mean the same
/// thing - and a file with two amount columns (Debit and Credit) is read as readily as one
/// with a single signed column.
/// </para>
/// <para>
/// What it is not forgiving about is a row it cannot read. Every such row is reported. A
/// statement quietly missing three lines reconciles to a wrong figure and looks tidy doing
/// it, which is worse than a file that refuses to import.
/// </para>
/// </remarks>
internal sealed class BankStatementReader : IBankStatementReader
{
    private static readonly string[] DateHeadings =
        ["value date", "date", "txn date", "transaction date", "posting date", "trans date"];

    private static readonly string[] DescriptionHeadings =
        ["description", "narration", "particulars", "details", "transaction details", "remarks"];

    private static readonly string[] DebitHeadings =
        ["debit", "withdrawal", "withdrawals", "money out", "dr", "debit amount"];

    private static readonly string[] CreditHeadings =
        ["credit", "deposit", "deposits", "money in", "cr", "credit amount"];

    private static readonly string[] AmountHeadings = ["amount", "value", "transaction amount"];

    private static readonly string[] ReferenceHeadings =
        ["reference", "ref", "cheque no", "cheque number", "transaction ref", "transaction id"];

    private static readonly string[] DateFormats =
    [
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd", "dd MMM yyyy",
        "d MMM yyyy", "dd-MMM-yyyy", "dd/MM/yy", "d/M/yy", "MM/dd/yyyy",
    ];

    public BankStatementFile Read(byte[] content, string fileName)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var cells = fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? ReadCsv(content)
            : ReadWorkbook(content);

        return Interpret(cells);
    }

    private static BankStatementFile Interpret(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var problems = new List<string>();

        var headerIndex = FindHeaderRow(rows);

        if (headerIndex < 0)
        {
            return new BankStatementFile(
                [],
                [
                    "No header row was found. The file needs a row naming at least a date " +
                    "column and an amount column - \"Value Date\" and \"Amount\", or a Debit " +
                    "and Credit pair.",
                ]);
        }

        var headings = rows[headerIndex].Select(Normalise).ToList();

        var dateColumn = Column(headings, DateHeadings);
        var descriptionColumn = Column(headings, DescriptionHeadings);
        var debitColumn = Column(headings, DebitHeadings);
        var creditColumn = Column(headings, CreditHeadings);
        var amountColumn = Column(headings, AmountHeadings);
        var referenceColumn = Column(headings, ReferenceHeadings);

        var parsed = new List<BankStatementRow>();
        var lineNumber = 0;

        for (var index = headerIndex + 1; index < rows.Count; index++)
        {
            var row = rows[index];

            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var fileRow = index + 1;

            if (!TryReadDate(Cell(row, dateColumn), out var valueDate))
            {
                problems.Add(
                    $"Row {fileRow}: \"{Cell(row, dateColumn)}\" is not a date this reader " +
                    "recognises, so the row was not imported.");

                continue;
            }

            if (!TryReadAmount(row, debitColumn, creditColumn, amountColumn, out var amount, out var direction))
            {
                problems.Add(
                    $"Row {fileRow}: no amount could be read. Every statement line moves money " +
                    "one way or the other.");

                continue;
            }

            var description = Cell(row, descriptionColumn);

            if (string.IsNullOrWhiteSpace(description))
            {
                // Not fatal. Some banks leave the narration blank on a charge, and a line with
                // a date and an amount still has to be reconciled.
                description = "(no description on the statement)";
            }

            parsed.Add(new BankStatementRow(
                ++lineNumber,
                valueDate,
                Trim(description, 500),
                amount,
                direction,
                Trim(Cell(row, referenceColumn), 100) is { Length: > 0 } reference ? reference : null));
        }

        if (parsed.Count == 0 && problems.Count == 0)
        {
            problems.Add("The file has a header row but no statement lines under it.");
        }

        return new BankStatementFile(parsed, problems);
    }

    private static int FindHeaderRow(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        // Statements carry a few lines of branch and account detail before the table starts,
        // so the header is found by what it says rather than by being first.
        for (var index = 0; index < rows.Count && index < 30; index++)
        {
            var headings = rows[index].Select(Normalise).ToList();

            var hasDate = Column(headings, DateHeadings) >= 0;

            var hasAmount = Column(headings, AmountHeadings) >= 0
                || Column(headings, DebitHeadings) >= 0
                || Column(headings, CreditHeadings) >= 0;

            if (hasDate && hasAmount)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryReadAmount(
        IReadOnlyList<string> row,
        int debitColumn,
        int creditColumn,
        int amountColumn,
        out decimal amount,
        out StatementDirection direction)
    {
        amount = 0m;
        direction = StatementDirection.Credit;

        // A Debit/Credit pair is read first, because a file that has both and an Amount column
        // usually means the Amount column unsigned.
        if (debitColumn >= 0 || creditColumn >= 0)
        {
            var hasDebit = TryReadDecimal(Cell(row, debitColumn), out var debit) && debit != 0m;
            var hasCredit = TryReadDecimal(Cell(row, creditColumn), out var credit) && credit != 0m;

            if (hasDebit && !hasCredit)
            {
                amount = Math.Abs(debit);
                direction = StatementDirection.Debit;

                return true;
            }

            if (hasCredit && !hasDebit)
            {
                amount = Math.Abs(credit);
                direction = StatementDirection.Credit;

                return true;
            }

            if (hasDebit && hasCredit)
            {
                // Both filled means the file is not what it claims to be. Refuse the row
                // rather than guess which column the bank meant.
                return false;
            }
        }

        if (amountColumn >= 0 && TryReadDecimal(Cell(row, amountColumn), out var signed) && signed != 0m)
        {
            amount = Math.Abs(signed);
            direction = signed < 0m ? StatementDirection.Debit : StatementDirection.Credit;

            return true;
        }

        return false;
    }

    private static bool TryReadDecimal(string text, out decimal value)
    {
        value = 0m;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = text
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("KES", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Ksh", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        // Accountants' brackets: (1,200.00) is money out.
        var negated = false;

        if (cleaned.StartsWith('(') && cleaned.EndsWith(')'))
        {
            cleaned = cleaned[1..^1];
            negated = true;
        }

        if (cleaned.EndsWith("DR", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^2].Trim();
            negated = true;
        }
        else if (cleaned.EndsWith("CR", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^2].Trim();
        }

        if (!decimal.TryParse(
                cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        value = negated ? -Math.Abs(parsed) : parsed;

        return true;
    }

    private static bool TryReadDate(string text, out DateOnly date)
    {
        date = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = text.Trim();

        if (DateOnly.TryParseExact(
                cleaned, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        // A workbook cell formatted as a date arrives as a serial number.
        if (double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial)
            && serial is > 0 and < 100000)
        {
            date = DateOnly.FromDateTime(DateTime.FromOADate(serial));

            return true;
        }

        if (DateTime.TryParse(
                cleaned, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = DateOnly.FromDateTime(parsed);

            return true;
        }

        return false;
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadWorkbook(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var workbook = new XLWorkbook(stream);

        var sheet = workbook.Worksheets.First();
        var used = sheet.RangeUsed();

        if (used is null)
        {
            return [];
        }

        return
        [
            .. used.RowsUsed().Select(row =>
                (IReadOnlyList<string>)[.. row.Cells().Select(cell => cell.GetFormattedString())]),
        ];
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadCsv(byte[] content)
    {
        var text = new UTF8Encoding(false).GetString(content).TrimStart('﻿');

        return
        [
            .. text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => (IReadOnlyList<string>)[.. SplitCsvLine(line)]),
        ];
    }

    /// <summary>
    /// Splits one CSV line, honouring quoted fields and doubled quotes inside them.
    /// </summary>
    /// <remarks>
    /// Written out rather than taken from a package because a bank narration routinely
    /// contains a comma - "TRF FRM NJERI, GRACE" - and splitting on commas alone silently
    /// shifts every column after it.
    /// </remarks>
    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];

            if (inQuotes)
            {
                if (character != '"')
                {
                    field.Append(character);
                }
                else if (index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inQuotes = true;
                    break;

                case ',':
                    fields.Add(field.ToString().Trim());
                    field.Clear();
                    break;

                default:
                    field.Append(character);
                    break;
            }
        }

        fields.Add(field.ToString().Trim());

        return fields;
    }

    private static int Column(List<string> headings, string[] candidates)
    {
        for (var index = 0; index < headings.Count; index++)
        {
            if (candidates.Contains(headings[index], StringComparer.Ordinal))
            {
                return index;
            }
        }

        // Nothing matched exactly. Try a contains match, which catches "Value Date (dd/mm)".
        for (var index = 0; index < headings.Count; index++)
        {
            if (headings[index].Length > 0
                && candidates.Any(candidate =>
                    headings[index].Contains(candidate, StringComparison.Ordinal)))
            {
                return index;
            }
        }

        return -1;
    }

    private static string Cell(IReadOnlyList<string> row, int column) =>
        column >= 0 && column < row.Count ? row[column].Trim() : string.Empty;

    private static string Normalise(string heading) =>
        heading.Trim().ToLowerInvariant().Replace(".", string.Empty, StringComparison.Ordinal);

    private static string Trim(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}
