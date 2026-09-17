using System.Security.Claims;
using Akiba.Application.Abstractions;
using Akiba.Domain.Common;

namespace Akiba.Infrastructure.Identity;

/// <summary>
/// The signed-in official, from the HTTP context.
/// </summary>
/// <remarks>
/// <para>
/// Throws when nobody is signed in rather than returning an anonymous actor. Every ledger
/// entry records its author, and an entry attributed to nobody is not an audit trail - so a
/// posting path that somehow ran unauthenticated must fail loudly rather than write something
/// unattributable.
/// </para>
/// <para>
/// ASP.NET Core Identity with mandatory TOTP arrives in milestone 15. Until then this reads
/// whatever principal the host supplies, which in development is the seeded clerk.
/// </para>
/// </remarks>
internal sealed class CurrentUser : ICurrentUser
{
    private readonly Func<ClaimsPrincipal?> _principal;

    public CurrentUser(Func<ClaimsPrincipal?> principal) => _principal = principal;

    public bool IsAuthenticated => _principal()?.Identity?.IsAuthenticated == true;

    public Actor Actor
    {
        get
        {
            var principal = _principal()
                ?? throw new InvalidOperationException(
                    "Nobody is signed in. Every ledger entry and period close records who acted, " +
                    "so an unauthenticated request cannot post anything.");

            var id = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var name = principal.FindFirst(ClaimTypes.Name)?.Value ?? principal.Identity?.Name;

            if (!Guid.TryParse(id, out var userId) || string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    "The signed-in principal has no usable id or name. Akiba records both against " +
                    "every entry, and stores the name alongside the id so the ledger stays " +
                    "readable after an account is renamed or disabled.");
            }

            return new Actor(userId, name);
        }
    }
}

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
