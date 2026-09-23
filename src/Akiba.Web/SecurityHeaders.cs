using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Akiba.Web;

/// <summary>
/// The headers every response carries.
/// </summary>
/// <remarks>
/// <para>
/// Akiba renders nothing it did not encode itself - there is no <c>MarkupString</c>, no
/// <c>innerHTML</c> and no JavaScript interop anywhere in the application, so Razor's automatic
/// encoding is the real defence against cross-site scripting. These headers are the second
/// line: they decide what a browser would be permitted to do if something ever did get through.
/// </para>
/// <para>
/// Written out rather than taken from a package, because a content security policy that is not
/// read and understood by whoever owns the application is a policy nobody can loosen safely
/// when a screen stops working.
/// </para>
/// </remarks>
public static class SecurityHeaders
{
    /// <summary>
    /// What the browser may load, and from where.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything comes from Akiba itself. The society runs on a LAN with no internet route, so
    /// there is no CDN to allow and nothing third-party to fetch - MudBlazor's stylesheet, its
    /// script and the Roboto font are all served from the application.
    /// </para>
    /// <para>
    /// <c>style-src</c> allows inline styles and that is not laziness: MudBlazor positions
    /// popovers, dialogs and the data grid by writing <c>style</c> attributes from JavaScript,
    /// and without it every menu in the panel opens in the wrong place. Scripts are <b>not</b>
    /// given the same latitude - <c>script-src 'self'</c> with no <c>unsafe-inline</c> and no
    /// <c>unsafe-eval</c>, which is the half that actually stops an injected payload running.
    /// </para>
    /// <para>
    /// <c>connect-src 'self'</c> covers the Blazor Server circuit: a same-origin WebSocket
    /// matches <c>'self'</c> under CSP Level 3.
    /// </para>
    /// </remarks>
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'; " +
        "manifest-src 'self'";

    /// <summary>
    /// Adds the response headers, and stops member data being cached by the browser.
    /// </summary>
    /// <remarks>
    /// Registered before anything that produces a response, so a request refused by
    /// authorisation carries them too.
    /// </remarks>
    public static IApplicationBuilder UseAkibaSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            // Set as the response starts rather than on the way in, so these are the last word.
            //
            // Blazor's component endpoint adds a Content-Security-Policy of its own further
            // down the pipeline. Two policies are not a hole - a browser enforces the
            // intersection of every policy it is given - but it means the one in force is not
            // the one written here, and the next person to loosen a directive would find it had
            // no effect. Registered first, this callback runs last, and the header it writes is
            // the one that ships.
            context.Response.OnStarting(() =>
            {
                Apply(context);

                return Task.CompletedTask;
            });

            await next().ConfigureAwait(false);
        });
    }

    private static void Apply(HttpContext context)
    {
        var headers = context.Response.Headers;

        headers["Content-Security-Policy"] = ContentSecurityPolicy;

        // Belt and braces with frame-ancestors above: X-Frame-Options is what older
        // browsers understand, and clickjacking a system that approves dividends is worth
        // two headers.
        headers["X-Frame-Options"] = "DENY";

        // Stops a browser deciding for itself that a report is really HTML.
        headers["X-Content-Type-Options"] = "nosniff";

        // A member's id appears in the path of a statement. Nothing outside Akiba should
        // ever learn one from a referrer.
        headers["Referrer-Policy"] = "no-referrer";

        headers["Permissions-Policy"] =
                "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), " +
                "microphone=(), payment=(), usb=()";

        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["X-Permitted-Cross-Domain-Policies"] = "none";

        MarkUncacheable(context);
    }

    /// <summary>
    /// Tells the browser not to keep anything Akiba rendered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every page in the panel shows somebody's financial position. On a shared office machine
    /// a cached page is readable by the next person at the desk, and the back button after a
    /// sign-out should not reproduce a member's statement.
    /// </para>
    /// <para>
    /// Static files are left alone. <c>app.css</c>, the favicon and MudBlazor's assets carry no
    /// member data and are fetched on every page load; making them uncacheable would slow the
    /// panel down for nothing.
    /// </para>
    /// </remarks>
    private static void MarkUncacheable(HttpContext context)
    {
        if (IsStaticAsset(context.Request.Path))
        {
            return;
        }

        context.Response.Headers[HeaderNames.CacheControl] =
            new StringValues("no-store, no-cache, must-revalidate");

        context.Response.Headers[HeaderNames.Pragma] = new StringValues("no-cache");
    }

    private static bool IsStaticAsset(PathString path) =>
        path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/js", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app.css", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/favicon.svg", StringComparison.OrdinalIgnoreCase);
}
