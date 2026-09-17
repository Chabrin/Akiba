using Akiba.Domain.Common;

namespace Akiba.Domain.Ledger;

/// <summary>
/// What an account belongs to, where it belongs to something.
/// </summary>
/// <remarks>
/// Most accounts are singletons - there is one Bank account and one Loan Interest Income
/// account. Two are not: Member Shares has one account per member, and Loans Receivable has
/// one per loan. That is what makes a member's shareholding and a loan's outstanding balance
/// derivable by summing a single account rather than by filtering every line in the ledger.
/// </remarks>
public enum AccountOwnerKind
{
    /// <summary>A society-wide account: Bank, Interest Income, Bank Charges.</summary>
    Society = 0,

    /// <summary>One account per member, for their shares.</summary>
    Member = 1,

    /// <summary>One account per loan, for its receivable balance.</summary>
    Loan = 2,
}

/// <summary>Links an account to the member or loan it belongs to.</summary>
public readonly record struct AccountOwner(AccountOwnerKind Kind, Guid OwnerId)
{
    /// <summary>A society-wide account, belonging to no particular member or loan.</summary>
    public static AccountOwner Society => new(AccountOwnerKind.Society, Guid.Empty);

    public static AccountOwner Member(Guid memberId) => Create(AccountOwnerKind.Member, memberId);

    public static AccountOwner Loan(Guid loanId) => Create(AccountOwnerKind.Loan, loanId);

    private static AccountOwner Create(AccountOwnerKind kind, Guid ownerId)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException($"A {kind} account must name its owner.", nameof(ownerId));
        }

        return new AccountOwner(kind, ownerId);
    }

    public override string ToString() =>
        Kind == AccountOwnerKind.Society ? "Society" : $"{Kind} {OwnerId}";
}

/// <summary>
/// A ledger account. Journal lines post against these, and every balance Akiba reports is a
/// sum over one of them.
/// </summary>
/// <remarks>
/// <para>
/// An account carries <b>no balance</b>. There is no <c>Balance</c> property here and there
/// never will be. Balances are derived from journal lines as at a date - see
/// <see cref="LedgerBalances"/> - because a stored balance can disagree with the ledger, and
/// when it does you have two numbers and no way to tell which is real.
/// </para>
/// <para>
/// Accounts are closed rather than deleted. A financial record is never hard-deleted, and a
/// closed account keeps every line ever posted to it.
/// </para>
/// </remarks>
public sealed class Account : AggregateRoot<AccountId>
{
    private Account(
        AccountId id,
        AccountCode code,
        string name,
        AccountType type,
        AccountOwner owner)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Code = code;
        Name = name;
        Type = type;
        Owner = owner;
        IsOpen = true;
    }

    public AccountCode Code { get; private set; }

    public string Name { get; private set; }

    public AccountType Type { get; private set; }

    public AccountOwner Owner { get; private set; }

    /// <summary>False once the account has been closed. Closed accounts accept no new lines.</summary>
    public bool IsOpen { get; private set; }

    public DateOnly? ClosedOn { get; private set; }

    /// <summary>The side on which this account normally carries a positive balance.</summary>
    public BalanceSide NormalBalance => Type.NormalBalance();

    /// <summary>Opens a society-wide account such as Bank or Loan Interest Income.</summary>
    public static Account OpenSocietyAccount(AccountCode code, string name, AccountType type) =>
        new(AccountId.New(), code, name, type, AccountOwner.Society);

    /// <summary>Opens the shares account for a member. One per member, created on joining.</summary>
    public static Account OpenMemberSharesAccount(AccountCode code, string memberName, Guid memberId) =>
        new(
            AccountId.New(),
            code,
            $"Member Shares - {memberName}",
            AccountType.Liability,
            AccountOwner.Member(memberId));

    /// <summary>Opens the receivable account for a loan. One per loan, created at disbursement.</summary>
    public static Account OpenLoanReceivableAccount(AccountCode code, string loanNumber, Guid loanId) =>
        new(
            AccountId.New(),
            code,
            $"Loans Receivable - {loanNumber}",
            AccountType.Asset,
            AccountOwner.Loan(loanId));

    /// <summary>
    /// Rebuilds an account from storage. For the persistence layer only - application code
    /// opens accounts through the factory methods above.
    /// </summary>
    public static Account Rehydrate(
        AccountId id,
        AccountCode code,
        string name,
        AccountType type,
        AccountOwner owner,
        bool isOpen,
        DateOnly? closedOn) =>
        new(id, code, name, type, owner)
        {
            IsOpen = isOpen,
            ClosedOn = closedOn,
        };

    /// <summary>
    /// Closes the account to new postings. Its history stays in the ledger and still counts
    /// towards every balance derived as at a date before or after closure.
    /// </summary>
    public void Close(DateOnly closedOn)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException($"Account {Code} is already closed.");
        }

        IsOpen = false;
        ClosedOn = closedOn;
    }

    public void Reopen()
    {
        IsOpen = true;
        ClosedOn = null;
    }

    public override string ToString() => $"{Code} {Name}";
}
