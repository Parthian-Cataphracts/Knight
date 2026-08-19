using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Knight.Infrastructure.Caching;

/// <summary>
/// Which implementation the replay guard and the cache are running on.
///
/// Registered as a value so <see cref="ReplayGuardGuardrail"/> can check the
/// mode without resolving the guard itself — resolving it would open the Redis
/// connection at startup, which is precisely what a control plane must not
/// require in order to boot.
/// </summary>
public sealed record ReplayProtectionMode(bool IsDistributed);

/// <summary>
/// Wires the distributed cache and the replay guard.
///
/// Redis is used when <c>ConnectionStrings:Redis</c> is set, and an in-process
/// implementation when it is not. The fallback is a real, correct implementation
/// for a single node and nothing more: it cannot see another process's nonces,
/// so a multi-instance deployment running on it would accept a replay on any
/// instance that had not seen the value. That is why
/// <see cref="ReplayGuardGuardrail"/> refuses to start without Redis outside
/// Development ([`adr/0020`](../../../docs/adr/0020-store-ingestion-authentication.md)).
/// </summary>
public static class CacheRegistration
{
    public static IServiceCollection AddSharedInfrastructureCache(this IServiceCollection services, IConfiguration configuration)
    {
        var redis = configuration.GetConnectionString("Redis");
        var isDistributed = !string.IsNullOrWhiteSpace(redis);

        services.TryAddSingleton(new ReplayProtectionMode(isDistributed));

        if (!isDistributed)
        {
            services.AddDistributedMemoryCache();
            services.TryAddSingleton<IReplayGuard, InProcessReplayGuard>();
        }
        else
        {
            services.AddStackExchangeRedisCache(options => options.Configuration = redis);

            services.TryAddSingleton<IConnectionMultiplexer>(_ =>
            {
                var options = ConfigurationOptions.Parse(redis!);

                // The host must not fail to start because Redis is momentarily
                // unreachable: the multiplexer keeps retrying in the background
                // instead of throwing here. Calls made while it is down still
                // fail, which is the right answer for replay protection — it
                // fails closed, and a handshake is refused rather than
                // accepted unchecked.
                options.AbortOnConnectFail = false;

                return ConnectionMultiplexer.Connect(options);
            });

            services.TryAddSingleton<IReplayGuard, RedisReplayGuard>();
        }

        services.TryAddSingleton<ICacheService, DistributedCacheService>();

        return services;
    }
}
