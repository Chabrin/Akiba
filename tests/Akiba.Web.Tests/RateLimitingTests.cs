using System.Net;

namespace Akiba.Web.Tests;

/// <summary>
/// One machine cannot sit and guess.
/// </summary>
/// <remarks>
/// Its own application instance, not the shared one. The limiter counts requests per host and
/// the test server reports no address for any of them, so every request in the assembly shares
/// a partition - exhausting it on a shared instance would fail whichever other test happened to
/// run next.
/// </remarks>
public sealed class RateLimitingTests
{
    [Fact]
    public async Task One_machine_cannot_keep_guessing_at_the_sign_in_page()
    {
        // Account lockout already stops five guesses at one account. This is what stops the
        // same machine working through every account five guesses at a time - and what stops
        // somebody locking all five officials out of their own system in a few seconds.
        await using var application = new AkibaApplication();
        using var client = application.CreateStrictClient();

        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < 30; attempt++)
        {
            var response = await client.PostAsync(
                "/auth/password",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["userName"] = $"guess-{attempt}",
                    ["password"] = "not-the-password",
                }));

            statuses.Add(response.StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests);

        // And the limit bites well before the thirtieth attempt rather than at the very end.
        statuses.IndexOf(HttpStatusCode.TooManyRequests).Should().BeLessThan(25);
    }

    [Fact]
    public async Task A_refusal_says_when_to_come_back()
    {
        await using var application = new AkibaApplication();
        using var client = application.CreateStrictClient();

        HttpResponseMessage? refused = null;

        for (var attempt = 0; attempt < 30 && refused is null; attempt++)
        {
            var response = await client.PostAsync(
                "/auth/password",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["userName"] = "somebody",
                    ["password"] = "not-the-password",
                }));

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                refused = response;
            }
        }

        refused.Should().NotBeNull();
        refused!.Headers.RetryAfter.Should().NotBeNull();

        (await refused.Content.ReadAsStringAsync())
            .Should().Contain("Too many attempts");
    }

    [Fact]
    public async Task The_panel_itself_is_not_rate_limited()
    {
        // A session is one long-lived circuit carrying every click an official makes. A limiter
        // across it would count an afternoon's work as an attack.
        await using var application = new AkibaApplication();
        using var client = application.CreateStrictClient();

        for (var request = 0; request < 40; request++)
        {
            var response = await client.GetAsync("/sign-in");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
