using Akiba.Domain.Ledger;

namespace Akiba.Domain.Lending;

public readonly record struct ChequeBookId(Guid Value)
{
    public static ChequeBookId New() => new(Guid.NewGuid());
}

public readonly record struct ChequeLeafId(Guid Value)
{
    public static ChequeLeafId New() => new(Guid.NewGuid());
}

public enum ChequeLeafStatus
{
    Available = 1,
    Reserved = 2,
    Issued = 3,
    Voided = 4,
}

/// <summary>A bank-issued cheque book whose leaves are controlled individually.</summary>
public sealed class ChequeBook
{
    private readonly List<ChequeLeaf> _leaves;

    private ChequeBook(
        ChequeBookId id,
        string bookReference,
        AccountId bankAccountId,
        DateOnly receivedOn,
        IReadOnlyList<ChequeLeaf> leaves,
        bool isClosed,
        int revision)
    {
        Id = id;
        BookReference = bookReference;
        BankAccountId = bankAccountId;
        ReceivedOn = receivedOn;
        _leaves = [.. leaves];
        IsClosed = isClosed;
        Revision = revision;
        LoadedRevision = revision;
    }

    public ChequeBookId Id { get; }
    public string BookReference { get; }
    public AccountId BankAccountId { get; }
    public DateOnly ReceivedOn { get; }
    public bool IsClosed { get; private set; }
    public int Revision { get; private set; }

    /// <summary>
    /// The revision this book had when it was read, before anything in this command changed it.
    /// </summary>
    /// <remarks>
    /// What a save compares against. If the book in the database no longer carries this
    /// revision, somebody else reserved or issued a cheque from it in between, and the save is
    /// refused rather than handing out a cheque that is no longer free.
    /// </remarks>
    public int LoadedRevision { get; }

    public IReadOnlyList<ChequeLeaf> Leaves => _leaves;

    public static ChequeBook Create(
        string bookReference,
        AccountId bankAccountId,
        DateOnly receivedOn,
        string prefix,
        string firstNumber,
        string lastNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bookReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastNumber);
        if (!bankAccountId.IsSpecified)
        {
            throw new ArgumentException("Select the bank account this book belongs to.", nameof(bankAccountId));
        }

        var start = ParseSerial(firstNumber, nameof(firstNumber));
        var end = ParseSerial(lastNumber, nameof(lastNumber));
        if (end < start)
        {
            throw new ArgumentException("The last cheque number must be at or after the first.");
        }

        var count = end - start + 1;
        if (count > 1_000)
        {
            throw new ArgumentException("A cheque book may contain at most 1,000 leaves.");
        }

        var normalizedPrefix = prefix?.Trim().ToUpperInvariant() ?? string.Empty;
        var normalizedReference = bookReference.Trim().ToUpperInvariant();
        var leaves = Enumerable.Range(0, checked((int)count))
            .Select(offset => new ChequeLeaf(
                ChequeLeafId.New(),
                normalizedPrefix + (start + offset).ToString($"D{firstNumber.Trim().Length}",
                    System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

        if (leaves.Select(leaf => leaf.Number).Distinct(StringComparer.OrdinalIgnoreCase).Count() != leaves.Length)
        {
            throw new ArgumentException("Cheque numbers in a book must be unique.");
        }

        return new ChequeBook(
            ChequeBookId.New(), normalizedReference, bankAccountId, receivedOn, leaves,
            isClosed: false, revision: 0);
    }

    public static ChequeBook Rehydrate(
        ChequeBookId id,
        string bookReference,
        AccountId bankAccountId,
        DateOnly receivedOn,
        IEnumerable<ChequeLeaf> leaves,
        bool isClosed,
        int revision)
    {
        ArgumentNullException.ThrowIfNull(leaves);

        // Put back in serial order. The database returns child rows in whatever order it
        // likes, and before this the book held its leaves in that order - so ReserveNext,
        // which takes the first available, could issue cheque 000137 before 000112.
        return new ChequeBook(
            id, bookReference, bankAccountId, receivedOn, [.. InSerialOrder(leaves)],
            isClosed, revision);
    }

    /// <summary>
    /// Reserves the lowest-numbered leaf still available.
    /// </summary>
    /// <remarks>
    /// Lowest, explicitly, rather than first in the list. Cheques going out in unbroken serial
    /// order is the control: a gap in the sequence is how a missing or stolen leaf is noticed,
    /// and a register that skips about cannot show a gap.
    /// </remarks>
    public ChequeLeaf ReserveNext(LoanApplicationId applicationId)
    {
        EnsureOpen();
        var leaf = InSerialOrder(_leaves).FirstOrDefault(candidate => candidate.Status == ChequeLeafStatus.Available)
            ?? throw new InvalidOperationException($"Cheque book {BookReference} has no available leaves.");
        leaf.Reserve(applicationId);
        Revision++;
        return leaf;
    }

    public ChequeLeaf Reserve(ChequeLeafId leafId, LoanApplicationId applicationId)
    {
        EnsureOpen();
        var leaf = _leaves.FirstOrDefault(candidate => candidate.Id == leafId)
            ?? throw new InvalidOperationException($"Cheque leaf {leafId} is not in book {BookReference}.");
        leaf.Reserve(applicationId);
        Revision++;
        return leaf;
    }

    public void Issue(ChequeLeafId leafId, LoanApplicationId applicationId, LoanId loanId)
    {
        Leaf(leafId).Issue(applicationId, loanId);
        Revision++;
    }

    public void Void(ChequeLeafId leafId, string reason)
    {
        Leaf(leafId).Void(reason);
        Revision++;
    }

    public ChequeLeaf Leaf(ChequeLeafId id) =>
        _leaves.FirstOrDefault(leaf => leaf.Id == id)
        ?? throw new InvalidOperationException($"Cheque leaf {id} is not in book {BookReference}.");

    public void Close()
    {
        if (_leaves.Any(leaf => leaf.Status == ChequeLeafStatus.Reserved))
        {
            throw new InvalidOperationException("A book with reserved cheque leaves cannot be closed.");
        }

        IsClosed = true;
        Revision++;
    }

    /// <summary>
    /// Leaves in the order the bank printed them.
    /// </summary>
    /// <remarks>
    /// Length first, then the characters. Every leaf in a book carries the same prefix, so this
    /// is numeric order - and it stays numeric when the serials are not zero-padded, where plain
    /// text order would put 10, 11 and 12 before 9.
    /// </remarks>
    private static IEnumerable<ChequeLeaf> InSerialOrder(IEnumerable<ChequeLeaf> leaves) =>
        leaves.OrderBy(leaf => leaf.Number.Length)
            .ThenBy(leaf => leaf.Number, StringComparer.Ordinal);

    private void EnsureOpen()
    {
        if (IsClosed)
        {
            throw new InvalidOperationException($"Cheque book {BookReference} is closed.");
        }
    }

    private static long ParseSerial(string value, string parameterName)
    {
        if (!long.TryParse(value.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number)
            || number < 0)
        {
            throw new ArgumentException("Cheque serials must be numeric; place any prefix in its own field.",
                parameterName);
        }

        return number;
    }
}

public sealed class ChequeLeaf
{
    internal ChequeLeaf(ChequeLeafId id, string number)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        Id = id;
        Number = number.Trim();
        Status = ChequeLeafStatus.Available;
    }

    public ChequeLeafId Id { get; }
    public string Number { get; }
    public ChequeLeafStatus Status { get; private set; }
    public LoanApplicationId? ReservedForApplication { get; private set; }
    public LoanId? IssuedForLoan { get; private set; }
    public string? VoidReason { get; private set; }

    public static ChequeLeaf Rehydrate(
        ChequeLeafId id,
        string number,
        ChequeLeafStatus status,
        LoanApplicationId? reservedForApplication,
        LoanId? issuedForLoan,
        string? voidReason) =>
        new(id, number)
        {
            Status = status,
            ReservedForApplication = reservedForApplication,
            IssuedForLoan = issuedForLoan,
            VoidReason = voidReason,
        };

    public void Reserve(LoanApplicationId applicationId)
    {
        if (!applicationId.IsSpecified)
        {
            throw new ArgumentException("A reservation must name the loan application.", nameof(applicationId));
        }

        if (Status != ChequeLeafStatus.Available)
        {
            throw new InvalidOperationException($"Cheque {Number} is {Status} and cannot be reserved.");
        }

        Status = ChequeLeafStatus.Reserved;
        ReservedForApplication = applicationId;
    }

    public void Issue(LoanApplicationId applicationId, LoanId loanId)
    {
        if (Status != ChequeLeafStatus.Reserved || ReservedForApplication != applicationId)
        {
            throw new InvalidOperationException($"Cheque {Number} is not reserved for this application.");
        }

        if (!loanId.IsSpecified)
        {
            throw new ArgumentException("Issuing a cheque records the loan it paid.", nameof(loanId));
        }

        Status = ChequeLeafStatus.Issued;
        IssuedForLoan = loanId;
    }

    public void Void(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Status is ChequeLeafStatus.Issued or ChequeLeafStatus.Voided)
        {
            throw new InvalidOperationException($"Cheque {Number} cannot be voided from {Status}.");
        }

        Status = ChequeLeafStatus.Voided;
        VoidReason = reason.Trim();
    }
}
