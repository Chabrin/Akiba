using System.Net;

namespace Akiba.Web.Tests;

/// <summary>
/// What a request gets when nobody is signed in, and what every response carries.
/// </summary>
public sealed class SecurityTests : IClassFixture<AkibaApplication>
{
    private readonly AkibaApplication _application;

    public SecurityTests(AkibaApplication application) => _application = application;

    [Theory]
    [InlineData("/")]
    [InlineData("/members")]
    [InlineData("/dividends")]
    [InlineData("/audit")]
    [InlineData("/reconciliation")]
    public async Task No_screen_answers_without_a_session(string path)
    {
        using var client = _application.CreateStrictClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Found);

        // The cookie handler redirects to an absolute URL. What matters is that it points at
        // the sign-in page on Akiba's own host - a sign-in redirect that can be pointed
        // somewhere else is how a convincing phishing link gets built out of a trusted address.
        var location = response.Headers.Location!;
        var target = location.IsAbsoluteUri ? location : new Uri(new Uri("http://localhost"), location);

        target.Host.Should().Be("localhost");
        target.AbsolutePath.Should().Be("/sign-in");
    }

    [Theory]
    [InlineData("/api/members")]
    [InlineData("/api/ledger/accounts")]
    [InlineData("/api/ledger/trial-balance")]
    [InlineData("/api/reconciliations")]
    [InlineData("/api/ledger/period-close/2026/9")]
    public async Task No_JSON_endpoint_answers_without_a_session(string path)
    {
        // Left open, these would be a way to read every member's position without signing in.
        using var client = _application.CreateStrictClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Found, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/reports/audit")]
    [InlineData("/reports/shareholding")]
    [InlineData("/reports/deductions/employees/2026/9")]
    [InlineData("/reports/agm/2026")]
    public async Task No_report_downloads_without_a_session(string path)
    {
        using var client = _application.CreateStrictClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_development_seed_endpoint_does_not_exist_in_production()
    {
        // It invents members. It is mapped only outside Production, and this is what proves
        // that rather than a comment saying so.
        using var client = _application.CreateStrictClient();

        var response = await client.PostAsync("/api/dev/seed-demo", content: null);

        // Not mapped, so the request falls through to the panel's catch-all route and is sent
        // to the sign-in page like any other unknown address. What matters is that it never
        // seeds anything.
        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("monthsOfContributions");
    }

    [Fact]
    public async Task Health_is_the_one_thing_that_answers_anonymously()
    {
        // The deployment guide tells an administrator to check it, and a probe that needs a
        // password is a probe nobody runs. It says Healthy or Unhealthy and nothing else.
        using var client = _application.CreateStrictClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().BeOneOf("Healthy", "Degraded", "Unhealthy");
    }

    [Fact]
    public async Task Every_response_carries_the_security_headers()
    {
        using var client = _application.CreateStrictClient();

        var response = await client.GetAsync("/sign-in");

        var headers = response.Headers;

        headers.GetValues("Content-Security-Policy").Should().ContainSingle()
            .Which.Should().Contain("default-src 'self'")
            .And.Contain("frame-ancestors 'none'")
            .And.Contain("object-src 'none'");

        headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        headers.GetValues("Referrer-Policy").Should().Equal("no-referrer");
        headers.GetValues("Cross-Origin-Opener-Policy").Should().Equal("same-origin");
        headers.GetValues("X-Permitted-Cross-Domain-Policies").Should().Equal("none");
        headers.GetValues("Permissions-Policy").Should().ContainSingle()
            .Which.Should().Contain("camera=()");
    }

    [Fact]
    public async Task Scripts_are_not_allowed_to_run_inline()
    {
        // The half of the policy that actually stops an injected payload. Inline *styles* are
        // allowed because MudBlazor positions every popover with a style attribute; inline
        // scripts are not, and neither is eval.
        using var client = _application.CreateStrictClient();

        var response = await client.GetAsync("/sign-in");
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();

        policy.Should().Contain("script-src 'self'");
        policy.Should().NotContain("script-src 'self' 'unsafe-inline'");
        policy.Should().NotContain("unsafe-eval");
    }

    [Fact]
    public async Task A_page_showing_member_data_is_never_cached_by_the_browser()
    {
        // On a shared office machine a cached page is readable by the next person at the desk.
        using var client = _application.CreateStrictClient();

        var response = await client.GetAsync("/sign-in");

        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Akiba_does_not_announce_what_it_is_running_on()
    {
        using var client = _application.CreateStrictClient();

        var response = await client.GetAsync("/health");

        response.Headers.Contains("Server").Should().BeFalse();
        response.Headers.Contains("X-Powered-By").Should().BeFalse();
        response.Headers.Contains("X-AspNet-Version").Should().BeFalse();
    }

    [Fact]
    public async Task Nothing_offers_itself_to_another_origin()
    {
        // No CORS policy is configured anywhere in Akiba, which is the correct answer for a
        // LAN system with no external caller: the browser's same-origin rule applies and no
        // Access-Control header is ever emitted. This is what proves none crept in.
        using var client = _application.CreateStrictClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/members");
        request.Headers.Add("Origin", "https://example.invalid");

        var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    [Fact]
    public async Task A_sign_in_post_with_no_antiforgery_token_is_refused()
    {
        // Without this, a page anywhere else can post here and sign an official into an account
        // the attacker controls - after which everything that official records lands in the
        // attacker's books rather than the society's.
        using var client = _application.CreateStrictClient();

        var response = await client.PostAsync(
            "/auth/password",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["userName"] = "setup",
                ["password"] = "whatever-it-is",
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString.Should().Be("/sign-in?error=expired");

        // And no session cookie came back.
        response.Headers.TryGetValues("Set-Cookie", out var cookies);
        (cookies ?? []).Should().NotContain(cookie => cookie.StartsWith("akiba.session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sign_out_post_with_no_antiforgery_token_is_refused()
    {
        using var client = _application.CreateStrictClient();

        var response = await client.PostAsync("/auth/sign-out", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString.Should().Be("/");
    }

    [Fact]
    public async Task The_idle_timeout_goes_out_through_the_same_antiforgery_check()
    {
        // The idle clock signs out by submitting a form, not by calling a softer endpoint of
        // its own. If somebody ever gives it one, this test says so: a post carrying the
        // timeout reason and no token is refused exactly like any other.
        using var client = _application.CreateStrictClient();

        var content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("reason", "timeout"),
        ]);

        var response = await client.PostAsync("/auth/sign-out", content);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString.Should().Be("/");
    }

    [Fact]
    public async Task The_sign_in_page_hands_out_an_antiforgery_token()
    {
        // The other half of the check above. If the form stopped emitting one, every sign-in
        // would fail - so this is the test that says which of the two broke.
        using var client = _application.CreateStrictClient();

        var html = await client.GetStringAsync("/sign-in");

        html.Should().Contain("__RequestVerificationToken");
    }
}
