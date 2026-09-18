using System.Globalization;
using Akiba.Application.Reporting;
using ClosedXML.Excel;

namespace Akiba.Infrastructure.Reporting;

/// <summary>
/// Writes the monthly deduction schedule as an Excel worksheet.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately shaped like the society's own register: staff number, name, what they held at
/// the end of last month, this month's contribution, what they will hold at the end of this
/// one. A clerk comparing the two should be comparing like with like, and HR should recognise
/// what lands in their inbox.
/// </para>
/// <para>
/// The loan instalment columns are the addition. The register tracks shares only; the schedule
/// has to tell HR the whole figure to take off a payslip, which is the contribution plus every
/// instalment falling due.
/// </para>
/// <para>
/// Money is written as a number with a shilling format, never as text. A figure HR cannot sum
/// is a figure HR cannot check.
/// </para>
/// </remarks>
internal sealed class DeductionScheduleWriter : IDeductionScheduleWriter
{
    private const string MoneyFormat = "#,##0.00";

    public GeneratedReport Write(DeductionSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(schedule.MonthEnd.ToString("MMM yyyy", CultureInfo.InvariantCulture));

        // Loan columns are laid out per loan, because a member may hold two.
        var maximumLoans = Math.Max(1, schedule.Lines.Max(line => (int?)line.LoanInstalments.Count) ?? 1);

        var row = WriteHeading(sheet, schedule);
        var headerRow = row;

        row = WriteColumnHeadings(sheet, row, schedule, maximumLoans);
        row = WriteLines(sheet, row, schedule, maximumLoans);
        row = WriteTotals(sheet, row, headerRow, schedule, maximumLoans);

        WriteFootnotes(sheet, row + 1, schedule);

        sheet.Columns().AdjustToContents();
        sheet.SheetView.FreezeRows(headerRow + 1);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        var kind = schedule.Kind == DeductionScheduleKind.Employees ? "employees" : "landlords";

        return new GeneratedReport(
            $"akiba-deductions-{kind}-{schedule.Year:D4}-{schedule.Month:D2}.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            stream.ToArray());
    }

    private static int WriteHeading(IXLWorksheet sheet, DeductionSchedule schedule)
    {
        sheet.Cell(1, 1).Value = "AKIBA WELFARE SOCIETY";
        sheet.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);

        sheet.Cell(2, 1).Value = schedule.Title.ToUpperInvariant();
        sheet.Cell(2, 1).Style.Font.SetBold();

        sheet.Cell(3, 1).Value =
            $"To reach HR by {schedule.MustReachHrBy.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}";
        sheet.Cell(3, 1).Style.Font.SetItalic();

        return 5;
    }

    private static int WriteColumnHeadings(
        IXLWorksheet sheet, int row, DeductionSchedule schedule, int maximumLoans)
    {
        var column = 1;

        // The register's own wording, so the two sheets read the same way.
        sheet.Cell(row, column++).Value = "STAFF NO.";
        sheet.Cell(row, column++).Value = "MEMBER NO.";
        sheet.Cell(row, column++).Value = "SHARE HOLDER";
        sheet.Cell(row, column++).Value =
            $"END OF {schedule.PreviousMonthEnd.ToString("MMMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant()}";
        sheet.Cell(row, column++).Value =
            $"CONTRIBUTION IN {schedule.MonthEnd.ToString("MMMM", CultureInfo.InvariantCulture).ToUpperInvariant()}";

        for (var loan = 1; loan <= maximumLoans; loan++)
        {
            sheet.Cell(row, column++).Value = $"LOAN {loan} NO.";
            sheet.Cell(row, column++).Value = $"LOAN {loan} INSTALMENT";
        }

        sheet.Cell(row, column++).Value = "TOTAL DEDUCTION";
        sheet.Cell(row, column).Value =
            $"END OF {schedule.MonthEnd.ToString("MMM. yyyy", CultureInfo.InvariantCulture).ToUpperInvariant()}";

        var heading = sheet.Range(row, 1, row, column);
        heading.Style.Font.SetBold();
        heading.Style.Fill.SetBackgroundColor(XLColor.FromHtml("#E8F0E8"));
        heading.Style.Alignment.SetWrapText();
        heading.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        return row + 1;
    }

    private static int WriteLines(
        IXLWorksheet sheet, int row, DeductionSchedule schedule, int maximumLoans)
    {
        foreach (var line in schedule.Lines)
        {
            var column = 1;

            // Written as text, because a staff number is an identifier rather than a quantity -
            // and leading zeros matter to HR.
            sheet.Cell(row, column++).SetValue(line.PayrollNumber);
            sheet.Cell(row, column++).SetValue(line.MembershipNumber);
            sheet.Cell(row, column++).SetValue(line.FullName);

            Money(sheet.Cell(row, column++), line.OpeningShareholding.Amount);
            Money(sheet.Cell(row, column++), line.ShareContribution.Amount);

            for (var loan = 0; loan < maximumLoans; loan++)
            {
                if (loan < line.LoanInstalments.Count)
                {
                    sheet.Cell(row, column++).SetValue(line.LoanInstalments[loan].LoanNumber);
                    Money(sheet.Cell(row, column++), line.LoanInstalments[loan].Amount.Amount);
                }
                else
                {
                    column += 2;
                }
            }

            Money(sheet.Cell(row, column++), line.TotalDeduction.Amount).Style.Font.SetBold();
            Money(sheet.Cell(row, column), line.ClosingShareholding.Amount);

            if (!line.IsOnPayroll)
            {
                // HR cannot deduct from somebody with no payslip. Shaded so it is obvious at a
                // glance rather than left to be discovered at payroll time.
                sheet.Range(row, 1, row, column).Style.Fill
                    .SetBackgroundColor(XLColor.FromHtml("#FFF4E5"));
            }

            row++;
        }

        return row;
    }

    private static int WriteTotals(
        IXLWorksheet sheet, int row, int headerRow, DeductionSchedule schedule, int maximumLoans)
    {
        var column = 1;

        sheet.Cell(row, column).Value = "TOTAL";
        column += 3;

        Money(sheet.Cell(row, column++), schedule.OpeningShareholding.Amount);
        Money(sheet.Cell(row, column++), schedule.TotalShareContributions.Amount);

        for (var loan = 0; loan < maximumLoans; loan++)
        {
            column++;

            // Only the last loan column carries the instalment total, so the figure appears once.
            if (loan == maximumLoans - 1)
            {
                Money(sheet.Cell(row, column), schedule.TotalLoanInstalments.Amount);
            }

            column++;
        }

        Money(sheet.Cell(row, column++), schedule.TotalDeductions.Amount);
        Money(sheet.Cell(row, column), schedule.ClosingShareholding.Amount);

        var totals = sheet.Range(row, 1, row, column);
        totals.Style.Font.SetBold();
        totals.Style.Border.TopBorder = XLBorderStyleValues.Thin;
        totals.Style.Border.BottomBorder = XLBorderStyleValues.Double;

        _ = headerRow;

        return row + 1;
    }

    private static void WriteFootnotes(IXLWorksheet sheet, int row, DeductionSchedule schedule)
    {
        sheet.Cell(row + 1, 1).Value =
            $"Cheque to Akiba Welfare Society: {schedule.TotalDeductions}";
        sheet.Cell(row + 1, 1).Style.Font.SetBold();

        sheet.Cell(row + 2, 1).Value = schedule.Kind == DeductionScheduleKind.Employees
            ? "Drawn on the business account."
            : "Drawn on the main account.";

        if (schedule.NotOnPayroll.Count > 0)
        {
            sheet.Cell(row + 4, 1).Value =
                $"{schedule.NotOnPayroll.Count} shareholder(s) shaded above are not on the CAL " +
                "payroll and cannot be deducted at source. They are listed so the office can " +
                "collect from them another way.";
            sheet.Cell(row + 4, 1).Style.Font.SetItalic();
        }

        sheet.Cell(row + 6, 1).Value =
            "Every figure is derived from the Akiba ledger as at the dates shown.";
        sheet.Cell(row + 6, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);
    }

    private static IXLCell Money(IXLCell cell, decimal amount)
    {
        // A number with a format, never a string. A figure HR cannot sum is a figure HR cannot
        // check - and the register they already use is full of live totals.
        cell.SetValue(amount);
        cell.Style.NumberFormat.Format = MoneyFormat;

        return cell;
    }
}
