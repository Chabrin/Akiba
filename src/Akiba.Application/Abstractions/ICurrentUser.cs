using Akiba.Domain.Common;

namespace Akiba.Application.Abstractions;

/// <summary>
/// Who is signed in.
/// </summary>
/// <remarks>
/// Every ledger entry and every period close records its author, so this is not an optional
/// convenience - a handler that cannot say who acted cannot post anything. It throws rather
/// than returning an anonymous actor, because an entry attributed to nobody is not an audit
/// trail.
/// </remarks>
public interface ICurrentUser
{
    /// <summary>The signed-in official.</summary>
    /// <exception cref="InvalidOperationException">Nobody is signed in.</exception>
    Actor Actor { get; }

    bool IsAuthenticated { get; }
}
