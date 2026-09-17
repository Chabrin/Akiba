// Akiba - composition root.
//
// MILESTONE 1: this host exists so the solution builds, runs and answers a health check.
// Blazor Server, MudBlazor, Identity, Serilog and Hangfire are wired up in their own
// milestones (see BUILD_BRIEF.md section 13). Nothing is added here ahead of its milestone.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

// Akiba runs on a dedicated machine on the CAL LAN and is never exposed to the internet.
// The bind address lives in appsettings so it can be locked to the LAN interface without
// a rebuild. See README.md, "Network exposure".
var app = builder.Build();

app.MapHealthChecks("/health");

app.MapGet("/", () => Results.Text(
    """
    Akiba Sacco Management System
    Milestone 1: solution skeleton.

    /health - liveness probe
    """,
    "text/plain"));

await app.RunAsync();

/// <summary>
/// Exposed so integration tests can host the application with WebApplicationFactory.
/// Required because top-level statements generate an internal Program class.
/// </summary>
public partial class Program;
