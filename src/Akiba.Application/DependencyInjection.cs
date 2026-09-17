using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Akiba.Application;

/// <summary>
/// Registers the application layer's handlers and validators.
/// </summary>
/// <remarks>
/// MediatR's types stay inside this project. <c>IRequest</c> never reaches Akiba.Domain, so
/// replacing the dispatcher later would be a change to this layer alone - which matters,
/// because MediatR is commercially licensed above a revenue threshold Akiba is currently far
/// below but might not always be.
/// </remarks>
public static class DependencyInjection
{
    public static IServiceCollection AddAkibaApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var assembly = typeof(ApplicationAssemblyMarker).Assembly;

        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssembly(assembly);
            configuration.AddOpenBehavior(typeof(ValidationBehaviour<,>));
        });

        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);

        return services;
    }
}

/// <summary>
/// Runs a command's validator before its handler.
/// </summary>
/// <remarks>
/// <para>
/// This catches the shape of a request - a blank cheque number, a negative amount, a cheque
/// with no clearance date. It does <b>not</b> catch the rules that matter, and it is not
/// meant to: whether an entry balances, whether a period is closed, whether a member already
/// holds two loans. Those live in the domain and at the repository boundary, where they hold
/// however the request arrived.
/// </para>
/// <para>
/// A validator here is a convenience for whoever is typing. The invariants are elsewhere.
/// </para>
/// </remarks>
public sealed class ValidationBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehaviour(IEnumerable<IValidator<TRequest>> validators) =>
        _validators = validators;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var failures = _validators
            .Select(validator => validator.Validate(new ValidationContext<TRequest>(request)))
            .SelectMany(result => result.Errors)
            .Where(failure => failure is not null)
            .ToList();

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }

        return await next(cancellationToken).ConfigureAwait(false);
    }
}
