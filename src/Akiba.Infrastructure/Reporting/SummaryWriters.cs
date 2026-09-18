using System.Globalization;
using Akiba.Application.Reporting;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Akiba.Infrastructure.Reporting;

/// <summary>
/// Writes the shareholding summary as an Excel worksheet.
/// </summary>
/// <remarks>
/// Excel rather than PDF because this is a working document - the office sorts it, filters it
/// and checks totals against the register. A PDF would look tidier and be useless for that.
/// </remarks>
internal sealed class ShareholdingSummaryWriter : IShareholdingSummaryWriter
{
    private const string MoneyFormat = "#,##0.00";

    public GeneratedReport Write(ShareholdingSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Shareholding");

        sheet.Cell(1, 1).Value = "AKIBA WELFARE SOCIETY";
        sheet.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);

        sheet.Cell(2, 1).Value =
            $"SHAREHOLDING AS AT {summary.AsAt.ToString("d MMMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant()}";
        sheet.Cell(2, 1).Style.Font.SetBold();

        var headerRow = 4;
        string[] headings =
        [
            "MEMBER NO.", "STAFF NO.", "SHARE HOLDER", "SHAREHOLDING",
            "BORROWING LIMIT", "LOANS OUTSTANDING", "NET POSITION", "STATUS",
        ];

        for (var index = 0; index < headings.Length; index++)
        {
            sheet.Cell(headerRow, index + 1).Value = headings[index];
        }

        var header = sheet.Range(headerRow, 1, headerRow, headings.Length);
        header.Style.Font.SetBold();
        header.Style.Fill.SetBackgroundColor(XLColor.FromHtml("#E8F0E8"));
        header.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        var row = headerRow + 1;

        foreach (var line in summary.Lines)
        {
            sheet.Cell(row, 1).SetValue(line.MembershipNumber);
            sheet.Cell(row, 2).SetValue(line.PayrollNumber);
            sheet.Cell(row, 3).SetValue(line.FullName);
            Money(sheet.Cell(row, 4), line.Shareholding.Amount);
            Money(sheet.Cell(row, 5), line.BorrowingLimit.Amount);
            Money(sheet.Cell(row, 6), line.LoansOutstanding.Amount);
            Money(sheet.Cell(row, 7), line.NetPosition.Amount);
            sheet.Cell(row, 8).SetValue(line.IsActive ? "Active" : "Left CAL");

            if (line.NetPosition.IsNegative)
            {
                // Owes more than they hold. Not a rule breach - shares are not automatically set
                // against a loan - but the figure an official looks for first.
                sheet.Cell(row, 7).Style.Font.SetFontColor(XLColor.FromHtml("#C62828"));
            }

            row++;
        }

        sheet.Cell(row, 1).Value = "TOTAL";
        Money(sheet.Cell(row, 4), summary.TotalShareholding.Amount);
        Money(sheet.Cell(row, 6), summary.TotalLoansOutstanding.Amount);
        Money(sheet.Cell(row, 7), (summary.TotalShareholding - summary.TotalLoansOutstanding).Amount);

        var totals = sheet.Range(row, 1, row, headings.Length);
        totals.Style.Font.SetBold();
        totals.Style.Border.TopBorder = XLBorderStyleValues.Thin;
        totals.Style.Border.BottomBorder = XLBorderStyleValues.Double;

        sheet.Cell(row + 2, 1).Value =
            $"{summary.MemberCount} member(s). Every figure is summed from the ledger as at " +
            $"{summary.AsAt.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}; none is stored.";
        sheet.Cell(row + 2, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);

        sheet.Columns().AdjustToContents();
        sheet.SheetView.FreezeRows(headerRow);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        return new GeneratedReport(
            $"akiba-shareholding-{summary.AsAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            stream.ToArray());
    }

    private static void Money(IXLCell cell, decimal amount)
    {
        cell.SetValue(amount);
        cell.Style.NumberFormat.Format = MoneyFormat;
    }
}

/// <summary>
/// Writes the AGM pack as a PDF.
/// </summary>
/// <remarks>
/// What the members are shown once a year: the treasurer's report, the chairman's report, and
/// the figures. The two reports are written by people and printed as supplied - Akiba does not
/// generate prose that an official then has to stand behind.
/// </remarks>
internal sealed class AgmPackWriter : IAgmPackWriter
{
    private const string Green = "#1b5e20";
    private const string Grey = "#666666";

    public GeneratedReport Write(AgmPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(text => text.FontSize(9.5f).FontFamily(ReportFonts.Body));

                page.Header().Element(header => Header(header, pack));
                page.Content().Element(content => Content(content, pack));

                page.Footer().AlignCenter().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(7).FontColor(Grey));
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        });

        return new GeneratedReport(
            $"akiba-agm-pack-{pack.Year}.pdf", "application/pdf", document.GeneratePdf());
    }

    private static void Header(IContainer container, AgmPack pack) =>
        container.Column(column =>
        {
            column.Item().Text("AKIBA WELFARE SOCIETY").FontSize(15).Bold().FontColor(Green);
            column.Item().Text($"Annual General Meeting - {pack.Year}").FontSize(11);
            column.Item().Text(
                    $"For the year {pack.From.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)} " +
                    $"to {pack.To.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}")
                .FontSize(8).FontColor(Grey);
            column.Item().PaddingTop(8).LineHorizontal(1).LineColor(Green);
        });

    private static void Content(IContainer container, AgmPack pack) =>
        container.PaddingVertical(14).Column(column =>
        {
            column.Spacing(18);

            column.Item().Element(section => Narrative(section, "Chairman's report", pack.ChairmansReport));
            column.Item().Element(section => Narrative(section, "Treasurer's report", pack.TreasurersReport));
            column.Item().Element(section => IncomeAndExpenditure(section, pack));
            column.Item().Element(section => Position(section, pack));
        });

    private static void Narrative(IContainer container, string title, string body) =>
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).Text(title).FontSize(12).Bold().FontColor(Green);

            column.Item().Text(string.IsNullOrWhiteSpace(body)
                ? "(not yet supplied)"
                : body.Trim())
                .FontSize(9.5f)
                .FontColor(string.IsNullOrWhiteSpace(body) ? Grey : Colors.Black);
        });

    private static void IncomeAndExpenditure(IContainer container, AgmPack pack) =>
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).Text("Income and expenditure")
                .FontSize(12).Bold().FontColor(Green);

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(4);
                    columns.RelativeColumn(2);
                });

                foreach (var line in pack.IncomeAndExpenditure.Income)
                {
                    Row(table, line.Name, line.Amount.ToString());
                }

                Row(table, "Total income", pack.IncomeAndExpenditure.TotalIncome.ToString(), bold: true);

                foreach (var line in pack.IncomeAndExpenditure.Expenditure)
                {
                    Row(table, line.Name, $"({line.Amount})");
                }

                Row(table, "Total expenditure",
                    $"({pack.IncomeAndExpenditure.TotalExpenditure})", bold: true);

                Row(table, "Surplus for the year",
                    pack.IncomeAndExpenditure.Surplus.ToString(), bold: true, rule: true);
            });

            column.Item().PaddingTop(4).Text(
                    "Bank charges are shown because they are deducted when working out the " +
                    "interest the dividend is calculated from.")
                .FontSize(7.5f).FontColor(Grey);
        });

    private static void Position(IContainer container, AgmPack pack) =>
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).Text($"Position at {pack.To:d MMMM yyyy}")
                .FontSize(12).Bold().FontColor(Green);

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(4);
                    columns.RelativeColumn(2);
                });

                Row(table, "Members", pack.MembersAtYearEnd.ToString(CultureInfo.InvariantCulture));
                Row(table, "Total shareholding", pack.Shareholding.TotalShareholding.ToString());
                Row(table, "Loans running", pack.LoansRunningAtYearEnd.ToString(CultureInfo.InvariantCulture));
                Row(table, "Loans outstanding", pack.LoansOutstandingAtYearEnd.ToString());
                Row(table, "Trial balance difference",
                    pack.TrialBalance.Difference.ToString(), bold: true, rule: true);
            });

            column.Item().PaddingTop(4).Text(
                    pack.TrialBalance.Balances
                        ? "The books balance. Every journal entry sums to zero by construction, so " +
                          "this figure cannot be anything else."
                        : "THE BOOKS DO NOT BALANCE. This must be explained before the meeting.")
                .FontSize(7.5f)
                .FontColor(pack.TrialBalance.Balances ? Grey : "#C62828");
        });

    private static void Row(TableDescriptor table, string label, string amount, bool bold = false, bool rule = false)
    {
        var labelCell = table.Cell().PaddingVertical(2.5f);
        var amountCell = table.Cell().PaddingVertical(2.5f).AlignRight();

        if (rule)
        {
            labelCell = labelCell.BorderTop(0.5f).BorderColor("#999999").PaddingTop(4);
            amountCell = amountCell.BorderTop(0.5f).BorderColor("#999999").PaddingTop(4);
        }

        var labelText = labelCell.Text(label).FontSize(9);
        var amountText = amountCell.Text(amount).FontSize(9);

        if (bold)
        {
            labelText.Bold();
            amountText.Bold();
        }
    }
}
