using Akiba.Application.Abstractions;
using Akiba.Infrastructure.Auditing;

namespace Akiba.Web;

/// <summary>
/// Tells the audit trail who is acting and from where.
/// </summary>
/// <remarks>
/// <para>
/// This lives in the composition root rather than in the infrastructure, because who is signed
/// in and what address they came from are HTTP facts. The infrastructure records whatever it is
/// handed and does not know what an HTTP request is.
/// </para>
/// <para>
/// A change made with nobody signed in is still recorded - the startup seed and a migration
/// genuinely have no official behind them - and the trail says so rather than inventing one.
/// </para>
/// </remarks>
public static class AuditConfiguration
{
    /// <summary>Wires the audit trail up. Called once, at startup.</summary>
    public static IApplicationBuilder UseAkibaAuditTrail(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var accessor = app.Services.GetRequiredService<IHttpContextAccessor>();

        AuditTrail.Configure(() => Subject(accessor));

        return app;
    }

    private static AuditSubject? Subject(IHttpContextAccessor accessor)
    {
        var context = accessor.HttpContext;

        if (context is null)
        {
            return null;
        }

        // Resolved from the request's own scope, so it is the official who made this request
        // rather than whoever happened to be signed in when the application started.
        var currentUser = context.RequestServices.GetService<ICurrentUser>();

        var actor = currentUser?.IsAuthenticated == true ? currentUser.Actor : (Domain.Common.Actor?)null;

        return new AuditSubject(
            actor?.UserId,
            actor?.DisplayName,
            Address(context));
    }

    /// <summary>
    /// The address the change came from.
    /// </summary>
    /// <remarks>
    /// Taken from the connection, never from a forwarded header. Akiba is LAN-only with no
    /// proxy in front of it, so a header claiming to say where a request came from would be
    /// somebody's assertion rather than a fact - and the whole point of recording it is that it
    /// is not forgeable by whoever is being audited.
    /// </remarks>
    private static string? Address(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString();
}
