using System.Collections.Concurrent;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Knight.Infrastructure.Caching;

/// <summary>
/// Replay protection on Redis. <c>SET key value EX ttl NX</c> is a single
/// round trip and is atomic across every instance, which is the entire reason
/// this is the production implementation: two API nodes must never both believe
/// they were the first to see a nonce.
/// </summary>
internal sealed class RedisReplayGuard : IReplayGuard
{
    private readonly IConnectionMultiplexer _redis;

    public RedisReplayGuard(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<bool> TryConsumeAsync(string scope, string value, TimeSpan window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await _redis.GetDatabase()
            .StringSetAsync($"knight:replay:{scope}:{value}", "1", window, When.NotExists);
    }
}

/// <summary>
/// Replay protection in this process only.
///
/// Correct for one instance and useless for two, which is exactly what it says
/// on the tin: <see cref="ReplayGuardGuardrail"/> stops a deployment that would
/// rely on it outside Development. Entries are swept lazily on write rather than
/// by a timer, so an idle process holds nothing and a busy one pays a bounded
/// cost per call.
/// </summary>
internal sealed class InProcessReplayGuard : IReplayGuard
{
    /// <summary>Beyond this, the sweep runs on every call rather than occasionally, so memory is bounded even under a flood of unique values.</summary>
    private const int SweepThreshold = 10_000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public InProcessReplayGuard(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public Task<bool> TryConsumeAsync(string scope, string value, TimeSpan window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = _time.GetUtcNow();
        Sweep(now);

        var key = $"{scope}:{value}";
        var expiresAt = now + window;

        // A key whose entry has expired is claimable again; TryAdd alone would
        // remember a nonce for the life of the process.
        var claimed = _seen.AddOrUpdate(
            key,
            expiresAt,
            (_, existing) => existing <= now ? expiresAt : existing);

        return Task.FromResult(claimed == expiresAt);
    }

    private void Sweep(DateTimeOffset now)
    {
        if (_seen.Count < SweepThreshold && Random.Shared.Next(100) != 0)
        {
            return;
        }

        foreach (var entry in _seen)
        {
            if (entry.Value <= now)
            {
                _seen.TryRemove(entry);
            }
        }
    }
}

/// <summary>
/// Refuses to start a non-development host that has no Redis configured.
///
/// This is the guardrail that makes the in-process fallback safe to ship: a
/// misconfigured production deployment fails at startup with a sentence
/// explaining itself, rather than running for months while quietly accepting
/// replayed handshakes on whichever instance had not seen the nonce.
/// </summary>
public sealed class ReplayGuardGuardrail : IHostedService
{
    private readonly ReplayProtectionMode _mode;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ReplayGuardGuardrail> _logger;

    /// <summary>
    /// Takes the mode rather than the guard on purpose. Injecting
    /// <see cref="IReplayGuard"/> would construct it, and constructing the Redis
    /// one opens the connection — turning a startup check into a startup
    /// dependency on Redis being up at that instant.
    /// </summary>
    public ReplayGuardGuardrail(ReplayProtectionMode mode, IHostEnvironment environment, ILogger<ReplayGuardGuardrail> logger)
    {
        _mode = mode;
        _environment = environment;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_mode.IsDistributed)
        {
            return Task.CompletedTask;
        }

        if (!_environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "No Redis connection string is configured, so replay protection would run in-process. " +
                "That is correct for a single instance only and is refused outside Development: set " +
                "ConnectionStrings:Redis. See docs/adr/0020-store-ingestion-authentication.md.");
        }

        _logger.LogWarning(
            "Replay protection and caching are running in-process because no Redis connection string is configured. " +
            "This is development-only behaviour and is refused in other environments.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
