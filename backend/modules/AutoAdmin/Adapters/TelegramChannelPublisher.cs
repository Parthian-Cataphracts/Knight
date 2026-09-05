using System.Text.Json;
using AutoAdmin.Domain;
using Microsoft.Extensions.Options;

namespace AutoAdmin.Adapters;

/// <summary>
/// The bot token and target chat a real Telegram publisher needs. Owner-supplied:
/// until a token is set the simulated publisher stands in and the journey still
/// runs (docs/adr/0038). A single KNIGHT-wide bot is the first step; per-store
/// credentials through the phase-24/31 secret delivery are the follow-up.
/// </summary>
public sealed class TelegramOptions
{
    public const string SectionName = "AutoAdmin:Telegram";

    public string? BotToken { get; set; }

    public string? ChatId { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);
}

/// <summary>
/// Telegram, behind the <see cref="IChannelPublisher"/> seam — the first real
/// channel next to the simulated one, and the most reachable from Iran
/// (docs/adr/0038). It posts the run's content to the store's channel through the
/// Bot API's <c>sendMessage</c>. The request it builds is unit-tested directly;
/// the live round-trip needs a real bot token, which only the owner supplies.
/// </summary>
internal sealed class TelegramChannelPublisher : IChannelPublisher
{
    private readonly HttpClient _http;
    private readonly TelegramOptions _options;

    public TelegramChannelPublisher(HttpClient http, IOptions<TelegramOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public string ChannelKey => "telegram";

    public string Name => "telegram";

    public async Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            return new PublishOutcome(false, "Telegram is not configured; no bot token or chat id is set.", null);
        }

        var url = $"https://api.telegram.org/bot{_options.BotToken}/sendMessage";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = _options.ChatId!,
                ["text"] = ComposeMessage(request),
            }),
        };

        using var response = await _http.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new PublishOutcome(false, $"Telegram refused the post ({(int)response.StatusCode}).", null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind is not JsonValueKind.True)
            {
                return new PublishOutcome(false, "Telegram reported the post did not go through.", null);
            }

            var messageId = root.TryGetProperty("result", out var result) && result.TryGetProperty("message_id", out var id)
                ? id.ToString()
                : null;

            return new PublishOutcome(true, "Published to Telegram.", messageId is null ? null : $"tg://{messageId}");
        }
        catch (JsonException)
        {
            return new PublishOutcome(false, "Telegram returned a response that could not be read.", null);
        }
    }

    /// <summary>
    /// The message a post carries: the caption when the admin generated one,
    /// otherwise the topic itself, so a channel with only a caption part still
    /// posts something meaningful.
    /// </summary>
    internal static string ComposeMessage(PublishRequest request)
    {
        var caption = request.Content.FirstOrDefault(piece => piece.Kind is ContentKind.Caption);
        return caption is not null ? caption.Body : request.Topic;
    }
}
