using System.Globalization;
using Akiba.Application.Reporting;
using Akiba.Domain.Dividends;
using ClosedXML.Excel;

namespace Akiba.Infrastructure.Reporting;

/// <summary>
/// Writes the dividend computation schedule as an Excel worksheet.
/// </summary>
/// <remarks>
/// <para>
/// This is what the treasurer reviews and the chairman approves, so it shows the workings
/// rather than only the answer: interest earned, the charges taken off it, the basis each
/// member's share was worked out on, and the basis figure beside every entitlement.
/// </para>
/// <para>
/// It carries which basis was used in words at the top, because the basis is not settled and
/// a schedule that did not say would be unreadable in two years when it has changed.
/// </para>
/// </remarks>
internal sealed class DividendScheduleWriter : IDividendScheduleWriter
{
    private const string MoneyFormat = "#,##0.00";
    private const string Header = "#E8F0E8";

    public GeneratedReport Write(DividendRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet($"Dividend {run.Year}");

        var row = WriteHeading(sheet, run);
        row = WriteWorkings(sheet, run, row);
        row = WriteMembers(sheet, run, row);
        WriteApprovals(sheet, run, row);

        sheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        return new GeneratedReport(
            $"akiba-dividend-{run.Year.ToString(CultureInfo.InvariantCulture)}.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            stream.ToArray());
    }

    private static int WriteHeading(IXLWorksheet sheet, DividendRun run)
    {
        sheet.Cell(1, 1).Value = "AKIBA WELFARE SOCIETY";
        sheet.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);

        sheet.Cell(2, 1).Value =
            $"DIVIDEND FOR {run.Year.ToString(CultureInfo.InvariantCulture)} - {run.Status.ToString().ToUpperInvariant()}";
        sheet.Cell(2, 1).Style.Font.SetBold();

        sheet.Cell(3, 1).Value = run.BasisExplanation;
        sheet.Cell(3, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);

        return 5;
    }

    private static int WriteWorkings(IXLWorksheet sheet, DividendRun run, int row)
    {
        sheet.Cell(row, 1).Value = "HOW THE AMOUNT WAS ARRIVED AT";
        sheet.Cell(row, 1).Style.Font.SetBold();
        row++;

        Label(sheet, row, "Interest earned on loans", run.InterestEarned.Amount);
        row++;

        Label(sheet, row, "Less bank charges", -run.BankCharges.Amount);
        row++;

        Label(sheet, row, "Available to distribute", run.Distributable.Amount, bold: true);
        sheet.Range(row, 1, row, 2).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        row++;

        Label(sheet, row, $"Total shareholding on the {run.BasisName.ToLowerInvariant()} basis",
            run.TotalBasis.Amount);
        row++;

        sheet.Cell(row, 1).Value = "Dividend per shilling of shareholding";
        sheet.Cell(row, 2).SetValue(run.RatePerShilling);
        sheet.Cell(row, 2).Style.NumberFormat.Format = "0.000000";
        row++;

        sheet.Cell(row, 1).Value =
            "The rate is shown for checking only. Every entitlement below is allocated rather " +
            "than multiplied by it, so the column sums to the available amount exactly.";
        sheet.Cell(row, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);

        return row + 2;
    }

    private static int WriteMembers(IXLWorksheet sheet, DividendRun run, int row)
    {
        string[] headings =
        [
            "MEMBER NO.", "MEMBER", $"{run.BasisName.ToUpperInvariant()}", "DIVIDEND",
        ];

        for (var index = 0; index < headings.Length; index++)
        {
            sheet.Cell(row, index + 1).Value = headings[index];
        }

        var header = sheet.Range(row, 1, row, headings.Length);
        header.Style.Font.SetBold();
        header.Style.Fill.SetBackgroundColor(XLColor.FromHtml(Header));
        header.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        var headerRow = row;
        row++;

        foreach (var line in run.Lines)
        {
            sheet.Cell(row, 1).SetValue(line.MembershipNumber);
            sheet.Cell(row, 2).SetValue(line.FullName);
            Money(sheet.Cell(row, 3), line.BasisAmount.Amount);
            Money(sheet.Cell(row, 4), line.Amount.Amount);
            row++;
        }

        sheet.Cell(row, 1).Value = "TOTAL";
        Money(sheet.Cell(row, 3), run.TotalBasis.Amount);
        Money(sheet.Cell(row, 4), run.TotalAllocated.Amount);

        var totals = sheet.Range(row, 1, row, headings.Length);
        totals.Style.Font.SetBold();
        totals.Style.Border.TopBorder = XLBorderStyleValues.Thin;
        totals.Style.Border.BottomBorder = XLBorderStyleValues.Double;

        sheet.SheetView.FreezeRows(headerRow);

        return row + 2;
    }

    private static void WriteApprovals(IXLWorksheet sheet, DividendRun run, int row)
    {
        sheet.Cell(row, 1).Value = "WHO SAW IT";
        sheet.Cell(row, 1).Style.Font.SetBold();
        row++;

        Signature(sheet, row++, "Computed by", run.ComputedBy?.DisplayName, run.ComputedAtUtc);
        Signature(sheet, row++, "Reviewed by (treasurer)", run.ReviewedBy?.DisplayName, run.ReviewedAtUtc);
        Signature(sheet, row++, "Approved by (chairman)", run.ApprovedBy?.DisplayName, run.ApprovedAtUtc);

        sheet.Cell(row + 1, 1).Value = run.Verdict;
        sheet.Cell(row + 1, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);
    }

    private static void Signature(
        IXLWorksheet sheet, int row, string role, string? name, DateTimeOffset? at)
    {
        sheet.Cell(row, 1).Value = role;
        sheet.Cell(row, 2).Value = name ?? "(not yet)";

        if (at is { } moment)
        {
            sheet.Cell(row, 3).Value =
                moment.ToString("d MMMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
        }

        if (name is null)
        {
            sheet.Range(row, 1, row, 3).Style.Font.SetFontColor(XLColor.Gray);
        }
    }

    private static void Label(IXLWorksheet sheet, int row, string label, decimal amount, bool bold = false)
    {
        sheet.Cell(row, 1).Value = label;
        Money(sheet.Cell(row, 2), amount);

        if (bold)
        {
            sheet.Range(row, 1, row, 2).Style.Font.SetBold();
        }
    }

    private static void Money(IXLCell cell, decimal amount)
    {
        cell.SetValue(amount);
        cell.Style.NumberFormat.Format = MoneyFormat;
    }
}
