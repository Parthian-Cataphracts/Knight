using AutoAdmin.Adapters;
using AutoAdmin.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AutoAdmin;

/// <summary>
/// The Automatic Admin engine (docs/adr/0038): the orchestrator and the two
/// provider seams, with the simulated adapters standing in for an unchosen AI
/// model and unconnected channels — the same shape PlatformBilling uses for
/// payments. A real generator or a real channel publisher is added alongside the
/// simulated one and wins by being registered after it.
///
/// The content-job and settings repositories are the persistence half and are
/// registered by Infrastructure, where the schema lives.
/// </summary>
public static class AutoAdminModule
{
    public static IServiceCollection AddAutoAdminModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IAutoAdminService, AutoAdminService>();

        // One generator is active at a time. The simulated one is always present
        // so the journey runs with no key; a real model replaces this line.
        services.AddSingleton<IContentGenerator, SimulatedContentGenerator>();

        // A simulated publisher per known channel, registered under its channel
        // key. A real publisher for a channel is registered after this and wins.
        foreach (var channelKey in AutoAdminParts.Channels.Values.Distinct(StringComparer.Ordinal))
        {
            services.AddSingleton<IChannelPublisher>(new SimulatedChannelPublisher(channelKey));
        }

        // Telegram is the first real channel (reachable from Iran, simple Bot API).
        // It joins only when a bot token is configured — then it wins for "telegram"
        // by being registered after the simulated one, exactly as Stripe joins
        // payments (PlatformBillingModule). Until then the simulated one stands in.
        services.AddOptions<TelegramOptions>().Bind(configuration.GetSection(TelegramOptions.SectionName));
        if (!string.IsNullOrWhiteSpace(configuration[$"{TelegramOptions.SectionName}:BotToken"]))
        {
            services.AddHttpClient<TelegramChannelPublisher>();
            services.AddSingleton<IChannelPublisher>(sp => sp.GetRequiredService<TelegramChannelPublisher>());
        }

        services.AddSingleton<IChannelPublisherRegistry, ChannelPublisherRegistry>();

        return services;
    }
}
