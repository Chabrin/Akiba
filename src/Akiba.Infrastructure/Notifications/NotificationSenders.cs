using System.Globalization;
using System.Net;
using System.Net.Mail;
using Akiba.Application.Notifications;
using Akiba.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace Akiba.Infrastructure.Notifications;

/// <summary>Where the mail server is, and who Akiba is when it writes.</summary>
/// <param name="Host">The SMTP host.</param>
/// <param name="Port">Its port. 587 for STARTTLS, which is what to use.</param>
/// <param name="UseStartTls">Whether to negotiate TLS. Leave on.</param>
/// <param name="UserName">The mailbox Akiba signs in as.</param>
/// <param name="Password">Its password. <b>From the environment, never from appsettings.</b></param>
/// <param name="FromAddress">What members see in the From line.</param>
/// <param name="FromName">The display name against it.</param>
public sealed record SmtpSettings(
    string? Host,
    int Port,
    bool UseStartTls,
    string? UserName,
    string? Password,
    string? FromAddress,
    string? FromName)
{
    /// <summary>Whether there is enough here to send anything.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

/// <summary>
/// Sends email through an ordinary SMTP server.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SmtpClient"/> rather than a package. It is marked obsolete for scenarios that
/// need modern authentication against a large provider, and none of those apply: this is one
/// office sending a handful of plain-text messages through whatever mail server the society
/// already pays for. A dependency would buy nothing and add something to keep patched.
/// </para>
/// <para>
/// A failure is returned, not thrown. A full mailbox is an ordinary outcome for an outbox.
/// </para>
/// </remarks>
internal sealed class SmtpEmailSender : INotificationSender
{
    private readonly SmtpSettings _settings;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(SmtpSettings settings, ILogger<SmtpEmailSender> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<string?> SendAsync(
        Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (!_settings.IsConfigured)
        {
            return "No mail server is configured. Set Akiba:Smtp:Host and Akiba:Smtp:FromAddress.";
        }

        try
        {
            using var client = new SmtpClient(_settings.Host, _settings.Port)
            {
                EnableSsl = _settings.UseStartTls,
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };

            if (!string.IsNullOrWhiteSpace(_settings.UserName))
            {
                client.Credentials = new NetworkCredential(_settings.UserName, _settings.Password);
            }

            using var message = new MailMessage
            {
                From = new MailAddress(
                    _settings.FromAddress!,
                    string.IsNullOrWhiteSpace(_settings.FromName)
                        ? "Akiba Welfare Society"
                        : _settings.FromName),
                Subject = notification.Subject,
                Body = notification.Body,
                IsBodyHtml = false,
            };

            message.To.Add(new MailAddress(notification.RecipientAddress, notification.RecipientName));

            await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Sent {Kind} to {Recipient} by email.", notification.Kind, notification.RecipientName);

            return null;
        }
        catch (Exception exception) when (
            exception is SmtpException or InvalidOperationException or FormatException
                or System.IO.IOException or ArgumentException)
        {
            _logger.LogWarning(
                exception, "Could not email {Kind} to {Recipient}.",
                notification.Kind, notification.RecipientName);

            return exception.Message;
        }
    }
}

/// <summary>Where the SMS gateway is, and who pays for it.</summary>
/// <param name="BaseAddress">Africa's Talking, or a sandbox while testing.</param>
/// <param name="UserName">The Africa's Talking account name.</param>
/// <param name="ApiKey">The key. <b>From the environment, never from appsettings.</b></param>
/// <param name="SenderId">The short name messages appear from, where the account has one.</param>
public sealed record SmsSettings(
    string BaseAddress,
    string? UserName,
    string? ApiKey,
    string? SenderId)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Sends SMS through Africa's Talking.
/// </summary>
/// <remarks>
/// <para>
/// A plain HTTP form post against a documented endpoint, rather than their SDK. The whole
/// integration is one request; a package would be a dependency to keep patched in exchange for
/// saving fifteen lines.
/// </para>
/// <para>
/// <b>Every message costs money.</b> That is the difference between this channel and email, and
/// the reason the outbox gives up after a few attempts rather than retrying forever - a loop
/// against a gateway that is accepting and charging for messages it then fails to deliver would
/// spend the society's money without anybody watching.
/// </para>
/// </remarks>
internal sealed class AfricasTalkingSmsSender : INotificationSender
{
    private readonly HttpClient _http;
    private readonly SmsSettings _settings;
    private readonly ILogger<AfricasTalkingSmsSender> _logger;

    public AfricasTalkingSmsSender(
        HttpClient http, SmsSettings settings, ILogger<AfricasTalkingSmsSender> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public NotificationChannel Channel => NotificationChannel.Sms;

    public async Task<string?> SendAsync(
        Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (!_settings.IsConfigured)
        {
            return "No SMS gateway is configured. Set Akiba:Sms:UserName and Akiba:Sms:ApiKey.";
        }

        try
        {
            var fields = new Dictionary<string, string>
            {
                ["username"] = _settings.UserName!,
                ["to"] = notification.RecipientAddress,
                ["message"] = notification.Body,
            };

            if (!string.IsNullOrWhiteSpace(_settings.SenderId))
            {
                fields["from"] = _settings.SenderId;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "/version1/messaging")
            {
                Content = new FormUrlEncodedContent(fields),
            };

            request.Headers.Add("apiKey", _settings.ApiKey);
            request.Headers.Add("Accept", "application/json");

            using var response = await _http.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"The SMS gateway answered {(int)response.StatusCode}: {Shorten(body)}");
            }

            _logger.LogInformation(
                "Sent {Kind} to {Recipient} by SMS.", notification.Kind, notification.RecipientName);

            return null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogWarning(
                exception, "Could not text {Kind} to {Recipient}.",
                notification.Kind, notification.RecipientName);

            return exception.Message;
        }
    }

    /// <summary>Enough of the gateway's answer to act on, without filling a column with JSON.</summary>
    private static string Shorten(string body) =>
        body.Length <= 300 ? body : body[..300] + "...";
}
