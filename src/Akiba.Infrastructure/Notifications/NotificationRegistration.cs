using Akiba.Application.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Akiba.Infrastructure.Notifications;

/// <summary>
/// Wires up sending.
/// </summary>
/// <remarks>
/// Separate from <c>AddAkibaInfrastructure</c> on purpose. Whether this machine is allowed to
/// send anything is a property of the deployment, and making the host pass that in means no
/// test or tool can pick up a sending configuration by accident.
/// </remarks>
public static class NotificationRegistration
{
    /// <summary>
    /// Registers the outbox senders, the policy and the dispatcher.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="configuration">Where the SMTP and SMS settings are read from.</param>
    /// <param name="sendingIsAllowed">
    /// <b>True only in Production.</b> Everywhere else every message is written down, shown on
    /// the notifications screen, and never sent.
    /// </param>
    /// <param name="suppressionReason">Shown against every suppressed message.</param>
    public static IServiceCollection AddAkibaNotifications(
        this IServiceCollection services,
        IConfiguration configuration,
        bool sendingIsAllowed,
        string suppressionReason)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var disabled = configuration
            .GetSection("Akiba:Notifications:Disabled")
            .Get<string[]>();

        // Replaces the send-nothing default that AddAkibaInfrastructure registers.
        services.RemoveAll<INotificationPolicy>();

        services.AddSingleton<INotificationPolicy>(
            new NotificationPolicy(
                sendingIsAllowed,
                suppressionReason,
                disabled is { Length: > 0 } ? disabled : null));

        // The credentials belong in the environment, never in appsettings.json. Configuration
        // reads both, so this works either way - and the deployment guide says which.
        var smtp = new SmtpSettings(
            configuration["Akiba:Smtp:Host"],
            configuration.GetValue("Akiba:Smtp:Port", 587),
            configuration.GetValue("Akiba:Smtp:UseStartTls", true),
            configuration["Akiba:Smtp:UserName"],
            configuration["Akiba:Smtp:Password"],
            configuration["Akiba:Smtp:FromAddress"],
            configuration["Akiba:Smtp:FromName"]);

        services.AddSingleton(smtp);

        services.AddSingleton<INotificationSender>(provider => new SmtpEmailSender(
            provider.GetRequiredService<SmtpSettings>(),
            provider.GetRequiredService<ILogger<SmtpEmailSender>>()));

        var sms = new SmsSettings(
            configuration["Akiba:Sms:BaseAddress"] ?? "https://api.africastalking.com",
            configuration["Akiba:Sms:UserName"],
            configuration["Akiba:Sms:ApiKey"],
            configuration["Akiba:Sms:SenderId"]);

        services.AddSingleton(sms);

        services.AddHttpClient<AfricasTalkingSmsSender>(client =>
        {
            client.BaseAddress = new Uri(sms.BaseAddress);

            // The society's connection is not fast and not always up. A gateway that has not
            // answered in half a minute is not going to.
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton<INotificationSender>(provider =>
            provider.GetRequiredService<AfricasTalkingSmsSender>());

        services.AddHostedService<NotificationDispatcher>();

        return services;
    }
}
