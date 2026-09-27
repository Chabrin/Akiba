using System.Reflection;
using Akiba.Application.Abstractions;
using MediatR;

namespace Akiba.ArchitectureTests;

/// <summary>
/// Every request that writes is named *Command, because that name is what gets it authorized.
/// </summary>
/// <remarks>
/// <para>
/// CommandAuthorizationBehaviour checks a role policy for every MediatR request whose name ends
/// in "Command", and lets everything else through as a read. That makes the name load-bearing:
/// a handler that saves to the ledger but handles a request called <c>SettleLoanRequest</c> or
/// <c>SaveReceipt</c> would run for anybody signed in, and nothing on any screen would show it.
/// </para>
/// <para>
/// "Writes" is taken to mean "is handed the unit of work", since that is the only way anything
/// in Akiba.Application reaches the database to change it. A handler that only reads never
/// needs one.
/// </para>
/// </remarks>
public sealed class CommandNamingTests
{
    [Fact]
    public void Every_handler_that_can_save_handles_a_request_named_Command()
    {
        var application = typeof(IUnitOfWork).Assembly;

        var offenders = application.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(WritesToTheDatabase)
            .SelectMany(HandledRequests)
            .Where(request => !request.Name.EndsWith("Command", StringComparison.Ordinal))
            .Select(request => request.FullName)
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty(
            because: "a request that can change the ledger but is not named *Command skips "
                + "the role check in CommandAuthorizationBehaviour entirely. Rename it.");
    }

    [Fact]
    public void The_rule_actually_finds_the_write_handlers()
    {
        // Guards the test above against passing vacuously. If a refactor renamed IUnitOfWork
        // or moved the handlers, "no offenders" would be true of an empty list.
        var application = typeof(IUnitOfWork).Assembly;

        var writers = application.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(WritesToTheDatabase)
            .SelectMany(HandledRequests)
            .Select(request => request.Name)
            .ToList();

        writers.Should().Contain(["EnrolMemberCommand", "DisburseLoanCommand", "ClosePeriodCommand"]);
    }

    private static bool WritesToTheDatabase(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(constructor => constructor.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(IUnitOfWork)));

    /// <summary>The request types a class handles, with or without a response.</summary>
    private static IEnumerable<Type> HandledRequests(Type handler) =>
        handler.GetInterfaces()
            .Where(contract => contract.IsGenericType)
            .Where(contract =>
                contract.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)
                || contract.GetGenericTypeDefinition() == typeof(IRequestHandler<>))
            .Select(contract => contract.GetGenericArguments()[0]);
}
