using Akiba.Domain.Ledger;
using Akiba.Domain.Membership;

namespace Akiba.Domain.Tests.Membership;

/// <summary>
/// Which schedule somebody is deducted on.
/// </summary>
/// <remarks>
/// Worth its own tests because three screens now filter on this answer, and if it were worked
/// out separately on each one they could disagree — the members list showing a landlord that
/// the loan book counts as an employee. One place decides, so these tests are the place it is
/// held to.
/// </remarks>
public sealed class BorrowerCategoryTests
{
    [Fact]
    public void An_ordinary_member_is_an_employee()
    {
        var member = Join();

        BorrowerCategories.Of(member).Should().Be(BorrowerCategory.Employee);
    }

    [Fact]
    public void A_member_marked_as_a_landlord_is_a_landlord()
    {
        var member = Join();

        member.MarkAsLandlord();

        BorrowerCategories.Of(member).Should().Be(BorrowerCategory.Landlord);
    }

    [Fact]
    public void Clearing_landlord_status_puts_them_back_on_the_payroll_schedule()
    {
        // A member who stops renting to CAL goes back to being deducted from a payslip, and
        // the schedules have to follow them. This is the transition that would otherwise leave
        // somebody on a schedule the cheque no longer covers.
        var member = Join();

        member.MarkAsLandlord();
        member.ClearLandlordStatus();

        BorrowerCategories.Of(member).Should().Be(BorrowerCategory.Employee);
    }

    [Fact]
    public void A_non_member_borrower_is_a_client()
    {
        var client = ClientBorrower.Register(
            new PersonName("Ruth", "Wairimu"),
            NationalId.Of("31654987"),
            PhoneNumber.Of("0722334455"));

        BorrowerCategories.Of(client).Should().Be(BorrowerCategory.Client);
    }

    [Fact]
    public void Nobody_is_a_client_rather_than_an_error()
    {
        // A loan whose borrower could not be found still has to appear on the portfolio, and
        // the filter still has to put it somewhere. Client is the honest bucket: whatever it
        // is, it is not a member of this society.
        BorrowerCategories.Of(null).Should().Be(BorrowerCategory.Client);
    }

    [Theory]
    [InlineData(BorrowerCategory.Employee, "CAL employee")]
    [InlineData(BorrowerCategory.Landlord, "Landlord")]
    [InlineData(BorrowerCategory.Client, "Client")]
    public void Each_category_has_the_name_the_office_uses(
        BorrowerCategory category, string expected) =>
        category.DisplayName().Should().Be(expected);

    private static Member Join() =>
        Member.Join(
            MembershipNumber.Of("0042"),
            PayrollNumber.Of("CAL/0042"),
            new PersonName("Grace", "Njeri"),
            NationalId.Of("28765432"),
            PhoneNumber.Of("0712345678"),
            "grace.njeri@example.com",
            ZoneId.New(),
            AccountId.New());
}
