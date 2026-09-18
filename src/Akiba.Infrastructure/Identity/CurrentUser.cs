using Akiba.Application.Abstractions;
using Akiba.Domain.Common;

namespace Akiba.Infrastructure.Identity;

/// <summary>
/// A fixed actor, for development and for tests.
/// </summary>
/// <remarks>
/// Registered only outside Production. It exists so the panel can be driven before Identity
/// lands in milestone 15, and the startup logs say plainly when it is in use.
/// </remarks>
internal sealed class FixedCurrentUser : ICurrentUser
{
    public FixedCurrentUser(Actor actor) => Actor = actor;

    public Actor Actor { get; }

    public bool IsAuthenticated => true;
}
