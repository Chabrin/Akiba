using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Akiba.Web.Tests;

/// <summary>
/// Akiba, hosted in process, against the same PostgreSQL the other integration tests use.
/// </summary>
/// <remarks>
/// <para>
/// The real pipeline, in the real order. A middleware that is registered after the one it was
/// meant to protect looks perfectly correct in the file and is obvious the moment a request
/// goes through it, so these tests make requests.
/// </para>
/// <para>
/// HTTPS is turned off for the test host, which is the one place it is legitimate: the test
/// server speaks plain HTTP and cannot present a certificate. Everything else runs as it does
/// on the society's machine.
/// </para>
/// </remarks>
public sealed class AkibaApplication : WebApplicationFactory<Program>
{
    public const string ConnectionStringVariable = "AKIBA_TEST_POSTGRES";

    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=akiba_tests;Username=postgres;Password=postgres";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } configured
            ? configured
            : DefaultConnectionString;

    /// <summary>
    /// Settings that have to exist before the host is built.
    /// </summary>
    /// <remarks>
    /// Environment variables rather than <c>ConfigureAppConfiguration</c>, and that is not a
    /// preference. Program.cs reads <c>Akiba:RequireHttps</c> off the builder while it is being
    /// assembled, which is earlier than any source a test factory adds afterwards - so an
    /// in-memory override arrives too late and the host runs with the default. That is worth
    /// knowing about this application, not only about this fixture: anything read at builder
    /// time can only be set by the environment or by appsettings.
    /// </remarks>
    static AkibaApplication()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Akiba", ConnectionString);
        Environment.SetEnvironmentVariable("Akiba__RequireHttps", "false");
        Environment.SetEnvironmentVariable("AllowedHosts", "localhost");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Production, so the development seed endpoint is not mapped and no fallback actor
        // exists - the configuration these tests are about is the one that ships.
        builder.UseEnvironment("Production");
    }

    /// <summary>A client that follows nothing, so a redirect can be asserted on.</summary>
    public HttpClient CreateStrictClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
}
