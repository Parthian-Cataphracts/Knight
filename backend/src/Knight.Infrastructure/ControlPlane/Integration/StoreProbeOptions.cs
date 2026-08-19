using System.ComponentModel.DataAnnotations;

namespace Knight.Infrastructure.ControlPlane.Integration;

/// <summary>
/// How KNIGHT calls out to stores (section "Stores:Probe").
///
/// Every value here exists because the call leaves the control plane: a store's
/// domain is operator-supplied, resolves through DNS nobody in this system
/// controls, and answers with a payload written by a different application. The
/// timeout, the attempt cap and the address policy are what keep one
/// misbehaving store from becoming KNIGHT's problem.
/// </summary>
public sealed class StoreProbeOptions
{
    public const string SectionName = "Stores:Probe";

    /// <summary>Turns the background poller off without unregistering it. Stores can still push heartbeats.</summary>
    public bool PollingEnabled { get; init; } = true;

    [Required]
    public string Scheme { get; init; } = "https";

    [Required]
    public string HealthPath { get; init; } = "/api/knight/health";

    [Range(1, 60)]
    public int TimeoutSeconds { get; init; } = 5;

    /// <summary>Total attempts per poll, including the first. Three is enough to ride out a restart and few enough not to amplify an outage.</summary>
    [Range(1, 5)]
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Base delay between attempts; doubled each time, with jitter.</summary>
    [Range(0, 10000)]
    public int BackoffMilliseconds { get; init; } = 250;

    [Range(5, 3600)]
    public int PollIntervalSeconds { get; init; } = 60;

    [Range(1, 500)]
    public int PollBatchSize { get; init; } = 50;

    /// <summary>
    /// Allows outbound calls to loopback and private ranges. False everywhere
    /// that matters: with it on, a store domain pointed at 169.254.169.254 turns
    /// the poller into a request forger against the machine KNIGHT runs on
    /// (docs/security-threat-model.md, SSRF). Local development sets it true
    /// because the reference store genuinely is on localhost.
    /// </summary>
    public bool AllowPrivateNetworks { get; init; }

    /// <summary>
    /// Absolute base URLs to use instead of the store's domain, keyed by domain.
    ///
    /// Two real cases: a store whose integration surface is not on the same host
    /// as its storefront, and local development, where the reference store is on
    /// 127.0.0.1:8000 and no DNS name points at it. Operator-controlled by
    /// design — a store may never tell KNIGHT where to call it back, which is
    /// exactly the input an attacker would want.
    /// </summary>
    public Dictionary<string, string> BaseUrlOverrides { get; init; } = [];
}
