using Akiba.Application.Reporting;
using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Akiba.Infrastructure.Reporting;

internal sealed class LoanPaymentVoucherWriter : ILoanPaymentVoucherWriter
{
    private const string Green = "#14532D";
    private const string Grey = "#52606D";

    public GeneratedReport Write(LoanPaymentVoucherData voucher)
    {
        ArgumentNullException.ThrowIfNull(voucher);
        var document = Document.Create(root => root.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1.8f, Unit.Centimetre);
            page.DefaultTextStyle(text => text.FontFamily(ReportFonts.Body).FontSize(10));
            page.Header().Column(column =>
            {
                column.Item().Text("AKIBA WELFARE SOCIETY").FontSize(17).Bold().FontColor(Green);
                column.Item().Text("PAYMENT VOUCHER").FontSize(12).Bold();
                column.Item().PaddingTop(8).LineHorizontal(1).LineColor(Green);
            });
            page.Content().PaddingTop(18).Column(column =>
            {
                column.Spacing(12);
                column.Item().Row(row =>
                {
                    row.RelativeItem().Element(cell => Field(cell, "Voucher reference", voucher.VoucherReference));
                    row.RelativeItem().Element(cell => Field(cell, "Prepared on", voucher.PreparedOn.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)));
                });
                column.Item().Row(row =>
                {
                    row.RelativeItem().Element(cell => Field(cell, "Pay to", voucher.BorrowerName));
                    row.RelativeItem().Element(cell => Field(cell, "Loan number", voucher.LoanNumber));
                });
                column.Item().Element(cell => Field(cell, "Purpose", "Approved loan disbursement"));
                column.Item().Element(cell => Field(cell, "Bank account", $"{voucher.BankAccountCode} — {voucher.BankAccountName}"));

                column.Item().PaddingTop(8).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(4);
                        columns.RelativeColumn(2);
                        columns.RelativeColumn(2);
                    });
                    table.Header(header =>
                    {
                        HeaderCell(header.Cell(), "Description");
                        HeaderCell(header.Cell(), "Reference");
                        HeaderCell(header.Cell(), "Amount (KES)");
                    });
                    BodyCell(table.Cell(), "Loan principal by cheque");
                    BodyCell(table.Cell(), $"Cheque {voucher.ChequeNumber}");
                    AmountCell(table.Cell(), voucher.Principal.Amount);
                    BodyCell(table.Cell(), $"Flat interest ({voucher.TermMonths} months)");
                    BodyCell(table.Cell(), voucher.LoanNumber);
                    AmountCell(table.Cell(), voucher.Interest.Amount);
                    table.Cell().ColumnSpan(2).BorderTop(1).BorderColor(Grey).Padding(7)
                        .Text("Total loan receivable").Bold();
                    table.Cell().BorderTop(1).BorderColor(Grey).Padding(7)
                        .AlignRight().Text($"{voucher.Principal.Amount + voucher.Interest.Amount:N2}").Bold();
                });

                column.Item().PaddingTop(8).Row(row =>
                {
                    row.RelativeItem().Element(cell => Signature(cell, "Prepared by"));
                    row.RelativeItem().Element(cell => Signature(cell, "Authorized signatory 1"));
                });
                column.Item().Row(row =>
                {
                    row.RelativeItem().Element(cell => Signature(cell, "Authorized signatory 2"));
                    row.RelativeItem().Element(cell => Signature(cell, "Payee acknowledgement"));
                });
                column.Item().PaddingTop(8).Text(
                    "Retain the signed cheque instrument and supporting documents with this voucher. The system records the assigned cheque number and final disbursement against this reference.")
                    .FontSize(8).FontColor(Grey);
            });
            page.Footer().AlignCenter().DefaultTextStyle(style => style.FontSize(8).FontColor(Grey))
                .Text(text =>
                {
                    text.Span("System generated voucher | ");
                    text.Span(voucher.VoucherReference).Bold();
                });
        }));

        return new GeneratedReport(
            $"akiba-payment-voucher-{voucher.VoucherReference}.pdf",
            "application/pdf",
            document.GeneratePdf());
    }

    private static void Field(IContainer container, string label, string value) =>
        container.Column(column =>
        {
            column.Item().Text(label).FontSize(8).FontColor(Grey);
            column.Item().PaddingTop(2).BorderBottom(0.5f).BorderColor(Grey).PaddingBottom(5)
                .Text(value).Bold();
        });

    private static void Signature(IContainer container, string label) =>
        container.Column(column =>
        {
            column.Item().PaddingTop(22).BorderBottom(0.7f).BorderColor(Grey).Height(1);
            column.Item().PaddingTop(4).Text(label).FontSize(8).FontColor(Grey);
            column.Item().PaddingTop(12).Text("Name: ____________________    Date: ______________")
                .FontSize(8).FontColor(Grey);
        });

    private static void HeaderCell(IContainer container, string value) =>
        container.Background(Green).Padding(7).Text(value).FontColor(Colors.White).Bold();

    private static void BodyCell(IContainer container, string value) =>
        container.BorderBottom(0.5f).BorderColor("#D9E2EC").Padding(7).Text(value);

    private static void AmountCell(IContainer container, decimal amount) =>
        container.BorderBottom(0.5f).BorderColor("#D9E2EC").Padding(7).AlignRight().Text($"{amount:N2}");
}
