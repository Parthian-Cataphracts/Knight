using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Knight.Infrastructure.Caching;

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

        if (string.IsNullOrWhiteSpace(redis))
        {
            services.AddDistributedMemoryCache();
            services.TryAddSingleton<IReplayGuard, InProcessReplayGuard>();
        }
        else
        {
            services.AddStackExchangeRedisCache(options => options.Configuration = redis);

            services.TryAddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
            services.TryAddSingleton<IReplayGuard, RedisReplayGuard>();
        }

        services.TryAddSingleton<ICacheService, DistributedCacheService>();

        return services;
    }
}
