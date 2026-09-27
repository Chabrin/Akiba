using Akiba.Domain.Ledger;
using Akiba.Domain.Lending;

namespace Akiba.Domain.Tests.Lending;

public sealed class ChequeBookTests
{
    private static readonly AccountId Bank = AccountId.New();

    [Fact]
    public void Creates_zero_padded_leaves_and_reserves_next_in_order()
    {
        var book = ChequeBook.Create("CB-01", Bank, new DateOnly(2026, 9, 1), "AB", "0098", "0100");
        var application = LoanApplicationId.New();

        var leaf = book.ReserveNext(application);

        leaf.Number.Should().Be("AB0098");
        leaf.Status.Should().Be(ChequeLeafStatus.Reserved);
        leaf.ReservedForApplication.Should().Be(application);
        book.Revision.Should().Be(1);
    }

    [Fact]
    public void Does_not_allow_a_reserved_leaf_to_be_reserved_twice()
    {
        var book = ChequeBook.Create("CB-01", Bank, new DateOnly(2026, 9, 1), "", "1", "2");
        var leaf = book.ReserveNext(LoanApplicationId.New());

        var act = () => book.Reserve(leaf.Id, LoanApplicationId.New());

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cannot_close_a_book_with_a_reserved_leaf()
    {
        var book = ChequeBook.Create("CB-01", Bank, new DateOnly(2026, 9, 1), "", "1", "2");
        book.ReserveNext(LoanApplicationId.New());

        var act = () => book.Close();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Voided_cheque_is_not_reissued_and_can_be_closed_after_void()
    {
        var book = ChequeBook.Create("CB-01", Bank, new DateOnly(2026, 9, 1), "", "1", "1");
        var leaf = book.ReserveNext(LoanApplicationId.New());
        book.Void(leaf.Id, "Cheque spoiled before signing");

        leaf.Status.Should().Be(ChequeLeafStatus.Voided);
        leaf.VoidReason.Should().Be("Cheque spoiled before signing");
        book.Close();
        book.IsClosed.Should().BeTrue();
    }

    [Fact]
    public void Enforces_reasonable_serial_range_and_numeric_serials()
    {
        var act = () => ChequeBook.Create("CB-01", Bank, new DateOnly(2026, 9, 1), "", "A1", "A3");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_reloaded_book_still_issues_its_lowest_cheque_first()
    {
        // The database hands child rows back in whatever order it likes. Before this, the book
        // kept that order and reserved "the first available" - so a reload could send 000137
        // out before 000112, and an unbroken serial sequence is the whole point of a register.
        var created = ChequeBook.Create("CB-01", Bank, new DateOnly(2026, 9, 1), "", "000110", "000140");
        var shuffled = created.Leaves.OrderBy(_ => Guid.NewGuid()).ToList();

        var reloaded = ChequeBook.Rehydrate(
            created.Id, created.BookReference, Bank, created.ReceivedOn, shuffled,
            isClosed: false, revision: 0);

        reloaded.ReserveNext(LoanApplicationId.New()).Number.Should().Be("000110");
        reloaded.ReserveNext(LoanApplicationId.New()).Number.Should().Be("000111");
        reloaded.Leaves.Select(leaf => leaf.Number).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void Serials_without_zero_padding_stay_in_numeric_order()
    {
        // Text order would put 10, 11 and 12 ahead of 9. A book numbered without padding is
        // unusual but not unheard of, and the register must not reorder the bank's printing.
        var created = ChequeBook.Create("CB-02", Bank, new DateOnly(2026, 9, 1), "", "9", "12");

        var reloaded = ChequeBook.Rehydrate(
            created.Id, created.BookReference, Bank, created.ReceivedOn,
            created.Leaves.Reverse().ToList(), isClosed: false, revision: 0);

        reloaded.Leaves.Select(leaf => leaf.Number).Should().Equal("9", "10", "11", "12");
        reloaded.ReserveNext(LoanApplicationId.New()).Number.Should().Be("9");
    }

    [Fact]
    public void A_voided_or_issued_leaf_is_skipped_rather_than_reused()
    {
        var book = ChequeBook.Create("CB-03", Bank, new DateOnly(2026, 9, 1), "", "1", "3");
        var first = book.ReserveNext(LoanApplicationId.New());
        book.Void(first.Id, "Spoilt while writing");

        book.ReserveNext(LoanApplicationId.New()).Number.Should().Be("2");
    }
}
