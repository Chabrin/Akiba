using System.Net;

namespace Akiba.Web.Tests;

/// <summary>
/// The authenticator-code step, as the browser receives it.
/// </summary>
/// <remarks>
/// The six boxes are drawn by js/akiba-otp.js over one real input, and the promise that makes
/// that safe is that the input underneath is an ordinary, complete code field. These tests hold
/// that promise: if somebody replaced it with six separate inputs, or dropped the attribute a
/// phone uses to offer the code, the boxes would still look right and signing in would quietly
/// get worse.
/// </remarks>
public sealed class SignInCodeTests : IClassFixture<AkibaApplication>
{
    private readonly AkibaApplication _application;

    public SignInCodeTests(AkibaApplication application) => _application = application;

    [Fact]
    public async Task There_is_one_real_code_field_that_autofill_and_screen_readers_understand()
    {
        using var client = _application.CreateStrictClient();

        var html = await client.GetStringAsync("/sign-in/code");

        html.Should().Contain("data-akiba-otp");

        // One field named "code", which is what /auth/code reads - six inputs would post six.
        html.Split("name=\"code\"").Length.Should().Be(2, because: "there must be exactly one code field");

        html.Should().Contain("autocomplete=\"one-time-code\"",
            because: "that is what lets a phone or password manager offer the code");
        html.Should().Contain("inputmode=\"numeric\"",
            because: "a phone should open its number pad, not the full keyboard");
        html.Should().Contain("for=\"akiba-otp-input\"",
            because: "a screen reader needs the field labelled even though the label is not shown");
    }

    [Fact]
    public async Task The_code_form_still_carries_its_antiforgery_token()
    {
        // The script posts the form's own fields. If the token ever went missing from the form,
        // every automatic submission would be refused as a forgery.
        using var client = _application.CreateStrictClient();

        var html = await client.GetStringAsync("/sign-in/code");

        html.Should().Contain("action=\"/auth/code\"");
        html.Should().Contain("__RequestVerificationToken");
    }

    [Fact]
    public async Task The_code_boxes_script_is_referenced_and_served()
    {
        using var client = _application.CreateStrictClient();

        var html = await client.GetStringAsync("/sign-in/code");
        html.Should().Contain("src=\"js/akiba-otp.js\"");

        var script = await client.GetAsync("/js/akiba-otp.js");

        script.StatusCode.Should().Be(HttpStatusCode.OK);
        script.Content.Headers.ContentType!.MediaType.Should().Contain("javascript");
    }
}
