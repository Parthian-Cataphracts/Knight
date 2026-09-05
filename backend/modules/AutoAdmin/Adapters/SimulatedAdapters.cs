using AutoAdmin.Domain;

namespace AutoAdmin.Adapters;

/// <summary>
/// The stand-in generator that runs the journey with no model and no key
/// (docs/adr/0038). It behaves like a real one in the only way the rest of the
/// system observes: it returns content for a brief, deterministically, so a test
/// or a local run gets a stable body. Swapping in a real model changes only this
/// class.
/// </summary>
internal sealed class SimulatedContentGenerator : IContentGenerator
{
    public string Name => "simulated";

    public Task<GeneratedContent> GenerateAsync(GenerationBrief brief, CancellationToken cancellationToken)
    {
        var body = brief.Kind switch
        {
            ContentKind.Image => $"[image] a studio-style shot for \"{brief.Topic}\"",
            ContentKind.Caption => $"[caption] {brief.Topic} — now available. #{Slug(brief.Topic)}",
            ContentKind.Story => $"[story] a 3-frame story about \"{brief.Topic}\"",
            ContentKind.Video => $"[video] a 15s promo for \"{brief.Topic}\"",
            _ => $"[{brief.Kind}] {brief.Topic}",
        };

        return Task.FromResult(new GeneratedContent(brief.Kind, body, Name));
    }

    private static string Slug(string topic)
    {
        var chars = topic.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        return new string(chars);
    }
}

/// <summary>
/// The stand-in publisher for one channel: it "publishes" by returning success
/// with a traceable reference, so the publish pass, the reporting and the
/// delivery drill all run with no channel account. A real publisher for the
/// channel replaces the registration for its key (docs/adr/0038).
/// </summary>
internal sealed class SimulatedChannelPublisher : IChannelPublisher
{
    public SimulatedChannelPublisher(string channelKey)
    {
        ChannelKey = channelKey;
    }

    public string ChannelKey { get; }

    public string Name => "simulated";

    public Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        var reference = $"sim://{ChannelKey}/{Guid.NewGuid():n}";
        var detail = $"Simulated publish of {request.Content.Count} piece(s) to {ChannelKey}.";
        return Task.FromResult(new PublishOutcome(Succeeded: true, detail, reference));
    }
}

internal sealed class ChannelPublisherRegistry : IChannelPublisherRegistry
{
    private readonly Dictionary<string, IChannelPublisher> _publishers;

    public ChannelPublisherRegistry(IEnumerable<IChannelPublisher> publishers)
    {
        // A real publisher for a channel is registered under the same key as the
        // simulated one, so it wins by being added after it.
        _publishers = new Dictionary<string, IChannelPublisher>(StringComparer.Ordinal);
        foreach (var publisher in publishers)
        {
            _publishers[publisher.ChannelKey] = publisher;
        }
    }

    public bool TryResolve(string channelKey, out IChannelPublisher publisher) =>
        _publishers.TryGetValue(channelKey, out publisher!);
}
