using System.Security.Claims;
using Akiba.Application.Abstractions;
using Akiba.Domain.Common;

namespace Akiba.Web;

/// <summary>
/// The signed-in official, from the HTTP context.
/// </summary>
/// <remarks>
/// Throws when nobody is signed in rather than returning an anonymous actor. Every ledger
/// entry records its author, so a posting path that somehow ran unauthenticated must fail
/// loudly rather than write something nobody can be held to.
/// </remarks>
internal sealed class HttpContextCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;
    private readonly Actor? _developmentFallback;

    /// <param name="accessor">The current request.</param>
    /// <param name="developmentFallback">
    /// An actor to use when nobody is signed in. <b>Supplied only outside Production</b>, and
    /// only so the development seeder - which runs with no request behind it - can post. In
    /// Production this is null and an unauthenticated request cannot write anything at all.
    /// </param>
    public HttpContextCurrentUser(IHttpContextAccessor accessor, Actor? developmentFallback = null)
    {
        _accessor = accessor;
        _developmentFallback = developmentFallback;
    }

    public bool IsAuthenticated =>
        _accessor.HttpContext?.User?.Identity?.IsAuthenticated == true
        || _developmentFallback is not null;

    public Actor Actor
    {
        get
        {
            var signedIn = _accessor.HttpContext?.User?.Identity?.IsAuthenticated == true;

            if (!signedIn && _developmentFallback is { } fallback)
            {
                return fallback;
            }

            var principal = _accessor.HttpContext?.User
                ?? throw new InvalidOperationException(
                    "Nobody is signed in. Every ledger entry and period close records who acted, " +
                    "so an unauthenticated request cannot post anything.");

            var id = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            // The display name is a claim rather than a lookup, so posting does not need to
            // touch the user table. It is copied onto the entry and stays legible after the
            // account is renamed or disabled.
            var name = principal.FindFirst("akiba:display_name")?.Value
                ?? principal.FindFirst(ClaimTypes.Name)?.Value
                ?? principal.Identity?.Name;

            if (!Guid.TryParse(id, out var userId) || string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    "The signed-in principal has no usable id or name. Akiba records both against " +
                    "every entry.");
            }

            return new Actor(userId, name);
        }
    }
}
