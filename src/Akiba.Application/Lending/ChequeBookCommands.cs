using Akiba.Application.Abstractions;
using Akiba.Domain.Ledger;
using Akiba.Domain.Lending;
using FluentValidation;
using MediatR;

namespace Akiba.Application.Lending;

public sealed record CreateChequeBookCommand(
    string BookReference,
    AccountId BankAccountId,
    DateOnly ReceivedOn,
    string Prefix,
    string FirstNumber,
    string LastNumber) : IRequest<ChequeBookId>;

public sealed class CreateChequeBookValidator : AbstractValidator<CreateChequeBookCommand>
{
    public CreateChequeBookValidator()
    {
        RuleFor(command => command.BookReference).NotEmpty().MaximumLength(100);
        RuleFor(command => command.BankAccountId).Must(id => id.IsSpecified);
        RuleFor(command => command.FirstNumber).NotEmpty();
        RuleFor(command => command.LastNumber).NotEmpty();
        RuleFor(command => command.Prefix).MaximumLength(30);
    }
}

internal sealed class CreateChequeBookHandler : IRequestHandler<CreateChequeBookCommand, ChequeBookId>
{
    private readonly IChequeBookRepository _books;
    private readonly IAccountRepository _accounts;
    private readonly IUnitOfWork _unitOfWork;

    public CreateChequeBookHandler(
        IChequeBookRepository books, IAccountRepository accounts, IUnitOfWork unitOfWork)
    {
        _books = books;
        _accounts = accounts;
        _unitOfWork = unitOfWork;
    }

    public async Task<ChequeBookId> Handle(
        CreateChequeBookCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var account = await _accounts.FindByIdAsync(command.BankAccountId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected bank ledger account does not exist.");

        if (!account.IsOpen || account.Type != AccountType.Asset || account.Owner.Kind != AccountOwnerKind.Society)
        {
            throw new InvalidOperationException("A cheque book must belong to an open society bank asset account.");
        }

        var book = ChequeBook.Create(
            command.BookReference,
            command.BankAccountId,
            command.ReceivedOn,
            command.Prefix,
            command.FirstNumber,
            command.LastNumber);

        // Refuse a repeated bank book label before inserting leaves; unique indexes remain the
        // final concurrency-safe guard if two officials register the same book simultaneously.
        var existingBooks = await _books.ListAsync(cancellationToken).ConfigureAwait(false);
        if (existingBooks.Any(existing => existing.BankAccountId == book.BankAccountId
            && string.Equals(existing.BookReference, book.BookReference, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Cheque book {book.BookReference} is already registered.");
        }

        var existingNumbers = existingBooks
            .Where(existing => existing.BankAccountId == book.BankAccountId)
            .SelectMany(existing => existing.Leaves)
            .Select(leaf => leaf.Number)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicateNumber = book.Leaves.FirstOrDefault(leaf => existingNumbers.Contains(leaf.Number));
        if (duplicateNumber is not null)
        {
            throw new InvalidOperationException(
                $"Cheque number {duplicateNumber.Number} is already in the inventory for this bank account.");
        }

        _books.Add(book);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return book.Id;
    }
}

public sealed record ChequeLeafSummary(ChequeLeafId Id, string Number, ChequeLeafStatus Status, string? VoidReason);

public sealed record ChequeBookSummary(
    ChequeBookId Id,
    string BookReference,
    AccountId BankAccountId,
    string BankAccountCode,
    string BankAccountName,
    DateOnly ReceivedOn,
    bool IsClosed,
    IReadOnlyList<ChequeLeafSummary> Leaves);

public sealed record ListChequeBooksQuery : IRequest<IReadOnlyList<ChequeBookSummary>>;

public sealed record PrepareLoanPaymentVoucherCommand(
    LoanApplicationId ApplicationId,
    string LoanNumber,
    ChequeBookId ChequeBookId,
    ChequeLeafId? SelectedLeafId,
    DateOnly PreparedOn) : IRequest<PreparedLoanPaymentVoucher>;

public sealed record PreparedLoanPaymentVoucher(string VoucherReference, string ChequeNumber);

internal sealed class PrepareLoanPaymentVoucherHandler
    : IRequestHandler<PrepareLoanPaymentVoucherCommand, PreparedLoanPaymentVoucher>
{
    private readonly ILoanApplicationRepository _applications;
    private readonly IChequeBookRepository _books;
    private readonly IUnitOfWork _unitOfWork;

    public PrepareLoanPaymentVoucherHandler(
        ILoanApplicationRepository applications,
        IChequeBookRepository books,
        IUnitOfWork unitOfWork)
    {
        _applications = applications;
        _books = books;
        _unitOfWork = unitOfWork;
    }

    public async Task<PreparedLoanPaymentVoucher> Handle(
        PrepareLoanPaymentVoucherCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var application = await _applications.FindByIdAsync(command.ApplicationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No application with id {command.ApplicationId}.");
        var book = await _books.FindByIdAsync(command.ChequeBookId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No cheque book {command.ChequeBookId}.");

        var leaf = command.SelectedLeafId is { } selected
            ? book.Reserve(selected, application.Id)
            : book.ReserveNext(application.Id);
        var reference = application.PreparePaymentVoucher(
            command.LoanNumber, book.Id, leaf.Id, leaf.Number, command.PreparedOn);

        _books.Update(book);
        _applications.Update(application);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new PreparedLoanPaymentVoucher(reference, leaf.Number);
    }
}

public sealed record CancelPreparedPaymentVoucherCommand(
    LoanApplicationId ApplicationId, string Reason) : IRequest;

internal sealed class CancelPreparedPaymentVoucherHandler
    : IRequestHandler<CancelPreparedPaymentVoucherCommand>
{
    private readonly ILoanApplicationRepository _applications;
    private readonly IChequeBookRepository _books;
    private readonly IUnitOfWork _unitOfWork;

    public CancelPreparedPaymentVoucherHandler(
        ILoanApplicationRepository applications,
        IChequeBookRepository books,
        IUnitOfWork unitOfWork)
    {
        _applications = applications;
        _books = books;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(
        CancelPreparedPaymentVoucherCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        var application = await _applications.FindByIdAsync(command.ApplicationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No application with id {command.ApplicationId}.");
        if (application.ChequeBookId is not { } bookId || application.ChequeLeafId is not { } leafId)
        {
            throw new InvalidOperationException("This application has no reserved cheque leaf.");
        }

        var book = await _books.FindByIdAsync(bookId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No cheque book {bookId}.");
        book.Void(leafId, command.Reason);
        application.CancelPreparedPaymentVoucher(command.Reason);
        _books.Update(book);
        _applications.Update(application);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class ListChequeBooksHandler
    : IRequestHandler<ListChequeBooksQuery, IReadOnlyList<ChequeBookSummary>>
{
    private readonly IChequeBookRepository _books;
    private readonly IAccountRepository _accounts;

    public ListChequeBooksHandler(IChequeBookRepository books, IAccountRepository accounts)
    {
        _books = books;
        _accounts = accounts;
    }

    public async Task<IReadOnlyList<ChequeBookSummary>> Handle(
        ListChequeBooksQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var books = await _books.ListAsync(cancellationToken).ConfigureAwait(false);
        var accounts = await _accounts.AllAsync(cancellationToken).ConfigureAwait(false);
        var byId = accounts.ToDictionary(account => account.Id);
        return [.. books.Select(book =>
        {
            var account = byId.GetValueOrDefault(book.BankAccountId);
            return new ChequeBookSummary(
                book.Id,
                book.BookReference,
                book.BankAccountId,
                account?.Code.ToString() ?? "Unknown",
                account?.Name ?? "Unknown account",
                book.ReceivedOn,
                book.IsClosed,
                // Already in serial order - the book guarantees it. Re-sorting as text here put
                // 10, 11 and 12 ahead of 9 whenever the serials were not zero-padded.
                [.. book.Leaves.Select(leaf =>
                    new ChequeLeafSummary(leaf.Id, leaf.Number, leaf.Status, leaf.VoidReason))]);
        })];
    }
}

public sealed record CloseChequeBookCommand(ChequeBookId ChequeBookId) : IRequest;

internal sealed class CloseChequeBookHandler : IRequestHandler<CloseChequeBookCommand>
{
    private readonly IChequeBookRepository _books;
    private readonly IUnitOfWork _unitOfWork;

    public CloseChequeBookHandler(IChequeBookRepository books, IUnitOfWork unitOfWork)
    {
        _books = books;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(CloseChequeBookCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var book = await _books.FindByIdAsync(command.ChequeBookId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No cheque book {command.ChequeBookId}.");
        book.Close();
        _books.Update(book);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
