using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Akiba.Web;

/// <summary>
/// How often one machine may call the expensive and the attackable endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Akiba already locks an account after five bad passwords, which is what stops somebody
/// guessing their way into one account. This is the other half: it stops one machine working
/// through <i>every</i> account five guesses at a time, and it stops somebody locking all five
/// officials out of their own system in a few seconds, which is a denial of service dressed up
/// as a security control.
/// </para>
/// <para>
/// Partitioned by remote address rather than by account, because the account is exactly what an
/// attacker varies. On a LAN each machine has its own address, so an official is never queued
/// behind a colleague.
/// </para>
/// <para>
/// <b>The Blazor circuit is deliberately not limited.</b> A panel session is one long-lived
/// WebSocket carrying every click an official makes; a request limiter across it would count an
/// afternoon's work as an attack. What is limited is the small set of endpoints where a request
/// is either cheap to abuse or expensive to serve.
/// </para>
/// </remarks>
public static class RateLimiting
{
    /// <summary>Signing in, entering a code, enrolling an authenticator.</summary>
    public const string Authentication = "akiba-auth";

    /// <summary>Generating a report, which builds a whole workbook or PDF.</summary>
    public const string Reports = "akiba-reports";

    public static IServiceCollection AddAkibaRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Twenty attempts in five minutes from one machine. A successful sign-in costs two
            // (password, then code), and an official who fumbles both a few times is nowhere
            // near it; somebody working through a list of user names hits it almost at once.
            options.AddPolicy(Authentication, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(5),
                        QueueLimit = 0,
                    }));

            // Reports are generated, not fetched: an AGM pack is a whole PDF and the
            // shareholding summary sums every member's ledger. Thirty a minute is far more than
            // an official produces and far less than a loop can ask for.
            options.AddPolicy(Reports, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Akiba.RateLimit")
                    .LogWarning(
                        "Rate limit reached for {Path} from {Ip}",
                        context.HttpContext.Request.Path,
                        context.HttpContext.Connection.RemoteIpAddress);

                await context.HttpContext.Response
                    .WriteAsync(
                        "Too many attempts from this machine. Wait a few minutes and try again.",
                        cancellationToken)
                    .ConfigureAwait(false);
            };
        });

        return services;
    }

    /// <summary>
    /// The machine a request came from.
    /// </summary>
    /// <remarks>
    /// Taken from the connection, never from a forwarded header - Akiba is LAN-only with no
    /// proxy in front of it, so a header claiming to say where a request came from would be
    /// chosen by the very person being limited.
    /// </remarks>
    private static string PartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
