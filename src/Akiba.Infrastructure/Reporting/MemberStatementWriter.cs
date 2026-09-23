using System.Globalization;
using Akiba.Application.Members;
using Akiba.Domain.Lending;
using Akiba.Application.Reporting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Akiba.Infrastructure.Reporting;

/// <summary>
/// Writes a member's statement as a PDF.
/// </summary>
/// <remarks>
/// <para>
/// This is the one document a member actually receives, so it is written to be read by
/// somebody who is not an accountant: what you hold, what you owe, and every movement behind
/// both, with the source document against each line so a figure can be queried.
/// </para>
/// <para>
/// It carries the as-at date prominently, because a statement is a position on a day rather
/// than a running total - and because the same request made next year produces the same
/// document.
/// </para>
/// </remarks>
internal sealed class MemberStatementWriter : IMemberStatementWriter
{
    private const string Grey = "#666666";

    public GeneratedReport Write(MemberStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(text => text.FontSize(9).FontFamily(ReportFonts.Body));

                page.Header().Element(header => Header(header, statement));
                page.Content().Element(content => Content(content, statement));
                page.Footer().Element(Footer);
            });
        });

        return new GeneratedReport(
            $"akiba-statement-{statement.MembershipNumber}-" +
            $"{statement.AsAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.pdf",
            "application/pdf",
            document.GeneratePdf());
    }

    /// <summary>
    /// The masthead, deliberately quiet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A statement is handed over at a counter and then carried through an office, and it
    /// spends most of that time face-up on somebody's desk. What a passer-by can read from two
    /// metres is a design decision, so this is designed: nothing on the page is set large
    /// enough to be read at a distance, the society's name included.
    /// </para>
    /// <para>
    /// It still <b>says</b> which society issued it, and the committee should keep it that way.
    /// A statement a member cannot attribute is a statement they cannot query, rely on, or
    /// bring to a meeting - and a document designed so the society could deny issuing it
    /// protects the society against the member rather than the member against anything. The
    /// threat worth designing for is a page glanced at across a room, and that is answered by
    /// type size, not by anonymity.
    /// </para>
    /// </remarks>
    private static void Header(IContainer container, MemberStatement statement) =>
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    // 9pt, grey, not a coloured masthead. Legible in the hand, and nothing at
                    // the top of the page announces what the document is from across a desk.
                    left.Item().Text("Akiba Welfare Society").FontSize(9).FontColor(Grey);
                    left.Item().Text("P.O. Box 16659-00620, Nairobi").FontSize(7).FontColor(Grey);
                });

                row.ConstantItem(180).AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text("Member statement").FontSize(9).FontColor(Grey);
                    right.Item().AlignRight()
                        .Text($"As at {statement.AsAt.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}")
                        .FontSize(8).FontColor(Grey);
                });
            });

            column.Item().PaddingTop(6).LineHorizontal(0.5f).LineColor("#DDDDDD");

            column.Item().PaddingTop(10).Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    // The member's own name is the one thing on the page that identifies a
                    // person, so it is the last thing that should be set large.
                    left.Item().Text(statement.MemberName).FontSize(10).SemiBold();
                    left.Item().Text($"Membership number {statement.MembershipNumber}")
                        .FontSize(8).FontColor(Grey);
                });

                row.RelativeItem().AlignRight().Text(text =>
                {
                    text.Span("Member since ").FontSize(8).FontColor(Grey);
                    text.Span(statement.MembershipSince?.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)
                              ?? "no contributions yet").FontSize(8);
                });
            });
        });

    private static void Content(IContainer container, MemberStatement statement) =>
        container.PaddingVertical(14).Column(column =>
        {
            column.Spacing(16);

            column.Item().Row(row =>
            {
                row.Spacing(10);
                row.RelativeItem().Element(box => Figure(box, "Shareholding", statement.Shareholding.ToString()));
                row.RelativeItem().Element(box => Figure(box, "Borrowing limit", statement.BorrowingLimit.ToString()));
                row.RelativeItem().Element(box => Figure(
                    box, "Loans outstanding",
                    statement.Loans
                        .Select(loan => loan.OutstandingBalance)
                        .Aggregate(Domain.Financial.Money.ZeroKes, (running, amount) => running + amount)
                        .ToString()));
            });

            if (statement.Loans.Count > 0)
            {
                column.Item().Element(section => LoansTable(section, statement));
            }

            column.Item().Element(section => SharesTable(section, statement));

            column.Item().PaddingTop(6).Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(7.5f).FontColor(Grey));
                text.Span(
                    "Every figure on this statement is summed from the society's journal entries " +
                    "as at the date shown. If you believe a line is wrong, quote the date and the " +
                    "document reference to the accounts clerk - each one traces back to a " +
                    "specific entry.");
            });
        });

    private static void Figure(IContainer container, string label, string value) =>
        container
            .Border(1).BorderColor("#DDDDDD").Padding(8)
            .Column(column =>
            {
                column.Item().Text(label.ToUpperInvariant()).FontSize(7).FontColor(Grey);

                // 10pt rather than 12. Still the largest thing on the page, because a member
                // reading their own statement should find their position first - but no longer
                // large enough to be read over a shoulder.
                column.Item().PaddingTop(2).Text(value).FontSize(10).SemiBold();
            });

    private static void LoansTable(IContainer container, MemberStatement statement) =>
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).Text("Loans").FontSize(9).SemiBold().FontColor(Grey);

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(1.5f);
                    columns.RelativeColumn(1.5f);
                    columns.RelativeColumn(1.5f);
                    columns.RelativeColumn(1.5f);
                });

                table.Header(header =>
                {
                    HeaderCell(header.Cell(), "Loan");
                    HeaderCell(header.Cell(), "Product");
                    HeaderCell(header.Cell(), "Repayable", right: true);
                    HeaderCell(header.Cell(), "Outstanding", right: true);
                    HeaderCell(header.Cell(), "Instalment", right: true);
                    HeaderCell(header.Cell(), "First due", right: true);
                });

                foreach (var loan in statement.Loans)
                {
                    Cell(table.Cell(), loan.LoanNumber);
                    Cell(table.Cell(), loan.Product.DisplayName());
                    Cell(table.Cell(), loan.TotalRepayable.ToString(), right: true);
                    Cell(table.Cell(), loan.OutstandingBalance.ToString(), right: true);
                    Cell(table.Cell(), loan.MonthlyInstalment.ToString(), right: true);
                    Cell(table.Cell(),
                        loan.FirstDueDate.ToString("d MMM yyyy", CultureInfo.InvariantCulture), right: true);
                }
            });

            column.Item().PaddingTop(3).Text(
                    "These are the loans that were running as at the statement date.")
                .FontSize(7).FontColor(Grey);
        });

    private static void SharesTable(IContainer container, MemberStatement statement) =>
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).Text("Share account").FontSize(9).SemiBold().FontColor(Grey);

            if (statement.ShareMovements.Count == 0)
            {
                column.Item().Text("No contributions recorded as at this date.")
                    .FontSize(9).FontColor(Grey);

                return;
            }

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(1.4f);
                    columns.RelativeColumn(3.4f);
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(1.6f);
                    columns.RelativeColumn(1.6f);
                });

                table.Header(header =>
                {
                    HeaderCell(header.Cell(), "Date");
                    HeaderCell(header.Cell(), "Narration");
                    HeaderCell(header.Cell(), "Document");
                    HeaderCell(header.Cell(), "Movement", right: true);
                    HeaderCell(header.Cell(), "Balance", right: true);
                });

                foreach (var line in statement.ShareMovements)
                {
                    Cell(table.Cell(), line.Date.ToString("d MMM yyyy", CultureInfo.InvariantCulture));
                    Cell(table.Cell(), line.Narration);
                    Cell(table.Cell(), line.SourceDocument, small: true);
                    Cell(table.Cell(), line.Movement.ToString(), right: true);
                    Cell(table.Cell(), line.RunningBalance.ToString(), right: true);
                }
            });
        });

    private static void HeaderCell(IContainer container, string text, bool right = false)
    {
        // Grey rather than the society's green. A block of colour is what the eye finds on a
        // page it is not reading, and a statement should not advertise itself across a desk.
        var cell = container
            .Background("#F2F2F2")
            .BorderBottom(0.5f).BorderColor("#CCCCCC")
            .PaddingVertical(4).PaddingHorizontal(3);

        (right ? cell.AlignRight() : cell).Text(text).FontSize(8).Bold();
    }

    private static void Cell(IContainer container, string text, bool right = false, bool small = false)
    {
        var cell = container
            .BorderBottom(0.5f).BorderColor("#EEEEEE")
            .PaddingVertical(3).PaddingHorizontal(3);

        (right ? cell.AlignRight() : cell)
            .Text(text)
            .FontSize(small ? 7 : 8)
            .FontColor(small ? Grey : Colors.Black);
    }

    private static void Footer(IContainer container) =>
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor("#DDDDDD");

            column.Item().Row(row =>
            {
                row.RelativeItem().Text(
                        $"Produced {DateTime.UtcNow:d MMMM yyyy}. This statement is not a demand for payment.")
                    .FontSize(7).FontColor(Grey);

                row.ConstantItem(80).AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(7).FontColor(Grey));
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        });
}
