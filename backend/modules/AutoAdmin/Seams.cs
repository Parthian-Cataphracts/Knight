using AutoAdmin.Domain;

namespace AutoAdmin;

// The two provider seams the Automatic Admin generates and publishes behind
// (docs/adr/0038-the-automatic-admin-generates-and-publishes-behind-seams.md).
// A simulated adapter stands in for each so the whole journey runs with no AI
// key and no channel account; a real provider is a drop-in that changes only the
// adapter, exactly as payments do with IPlatformPaymentProvider.

/// <summary>What the admin was asked to make: the topic and the kind of content.</summary>
public sealed record GenerationBrief(Guid CustomerId, string Topic, ContentKind Kind);

/// <summary>A generated piece, tagged with the generator that made it.</summary>
public sealed record GeneratedContent(ContentKind Kind, string Body, string GeneratorName);

/// <summary>
/// Turns a brief into content. The real generator — the owner's chosen model,
/// bound by adr/0030 on what store data may reach it — implements this; the
/// simulated one stands in until then.
/// </summary>
public interface IContentGenerator
{
    /// <summary>The generator's name, recorded on every draft it produces.</summary>
    string Name { get; }

    Task<GeneratedContent> GenerateAsync(GenerationBrief brief, CancellationToken cancellationToken);
}

/// <summary>The content to publish to one channel: the topic and the pieces the admin generated.</summary>
public sealed record PublishRequest(string ChannelKey, string Topic, IReadOnlyCollection<GeneratedContent> Content);

/// <summary>Whether a publish attempt succeeded, and a reference to trace it by.</summary>
public sealed record PublishOutcome(bool Succeeded, string Detail, string? ExternalReference);

/// <summary>
/// Publishes content to one channel. A real publisher per channel (Telegram
/// first, Meta last — adr/0038) gets its credentials through the per-store secret
/// delivery of phases 24 and 31; the simulated one records the publish instead.
/// </summary>
public interface IChannelPublisher
{
    /// <summary>The channel this publisher serves: "telegram", "instagram", "divar", "basalam".</summary>
    string ChannelKey { get; }

    /// <summary>The publisher's name, recorded on the publication — "simulated" until a real one is wired in.</summary>
    string Name { get; }

    Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken cancellationToken);
}

/// <summary>Resolves the publisher for a channel key, or reports that none is configured.</summary>
public interface IChannelPublisherRegistry
{
    bool TryResolve(string channelKey, out IChannelPublisher publisher);
}
