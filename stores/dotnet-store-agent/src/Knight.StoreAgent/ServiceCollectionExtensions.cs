using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Knight.StoreAgent;

/// <summary>
/// Wiring the agent into an ASP.NET Core store.
///
/// Two lines in <c>Program.cs</c> and a configuration section. The intent is
/// that connecting a store to KNIGHT is not a project: it is a package
/// reference, a credential, and a restart.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the client, the runner and the two background services.
    ///
    /// <code>
    /// builder.Services.AddKnightStoreAgent(builder.Configuration);
    /// </code>
    ///
    /// The background services do nothing when <c>Knight:Enabled</c> is false,
    /// which is what a test host and a one-shot command want: a store that
    /// polled for jobs from inside its own test suite would install Features
    /// into whatever database the suite happened to point at.
    /// </summary>
    public static IServiceCollection AddKnightStoreAgent(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = KnightOptions.SectionName)
    {
        services.AddOptions<KnightOptions>()
            .Bind(configuration.GetSection(sectionName))
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.ClientId),
                "Knight:ClientId is required when the agent is enabled.")
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.ClientSecret),
                "Knight:ClientSecret is required when the agent is enabled.")
            // Not conditioned on `Enabled` any more. A store can now be
            // connected from its own panel, so the keys it verifies downloads
            // against have to be in place before that happens rather than
            // checked at a start-up that has already gone by.
            .Validate(
                options => options.SigningKeys.Count > 0,
                "Knight:SigningKeys must name at least one trusted key, or this store can verify nothing it downloads.")
            .ValidateOnStart();

        services.AddHttpClient<KnightClient>();

        // The client the proxy forwards with. Named, so a store can give it its
        // own timeout and egress policy without touching this library.
        services.AddHttpClient(KnightServiceProxyMiddleware.HttpClientName, client =>
        {
            // Short: this is on a shopper's request path, and a Feature's
            // service being slow must not become the store being slow.
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddSingleton<JobRunner>();
        services.AddSingleton<FeatureRegistryAccessor>();
        services.AddSingleton<KnightAgentStatus>();
        services.AddSingleton<KnightConnection>();
        services.AddSingleton<KnightStatusReader>();

        // TryAdd, so a store that keeps credentials in its own encrypted
        // settings table, or names its staff roles differently, replaces one
        // registration rather than forking the library.
        services.TryAddSingleton<IKnightCredentialStore, FileKnightCredentialStore>();
        services.TryAddSingleton<IKnightProxyIdentity, ClaimsProxyIdentity>();

        services.AddHostedService<KnightHeartbeatService>();
        services.AddHostedService<KnightAgentService>();

        return services;
    }
}

/// <summary>
/// The registry, for the store's own code to read.
///
/// A store needs to know which Features it is serving — to mount their
/// endpoints, to show them on an admin page, to refuse a request for one whose
/// entitlement has lapsed. This is that, and it is deliberately read-only from
/// the store's side: what is installed is decided by delivery, not by the
/// application that received it.
/// </summary>
public sealed class FeatureRegistryAccessor(Microsoft.Extensions.Options.IOptions<KnightOptions> options)
{
    private readonly FeatureRegistry _registry = new(options.Value.FeatureRoot);

    public Task<IReadOnlyDictionary<string, InstalledFeature>> AllAsync(CancellationToken cancellationToken = default) =>
        _registry.AllAsync(cancellationToken);

    public Task<InstalledFeature?> FindAsync(string slug, CancellationToken cancellationToken = default) =>
        _registry.FindAsync(slug, cancellationToken);

    /// <summary>
    /// Whether this store may serve a Feature right now.
    ///
    /// Installed **and** enabled. The two are separate facts and a store
    /// enforces both: installed code still refuses to run without a valid
    /// entitlement, which is what makes "the subscription ended" mean something
    /// on the day it happens rather than whenever somebody redeploys.
    /// </summary>
    public async Task<bool> IsServingAsync(string slug, CancellationToken cancellationToken = default) =>
        await _registry.FindAsync(slug, cancellationToken) is { Enabled: true };
}
