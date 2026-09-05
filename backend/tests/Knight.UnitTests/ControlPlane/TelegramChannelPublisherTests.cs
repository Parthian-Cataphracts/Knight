using System.Net;
using AutoAdmin;
using AutoAdmin.Adapters;
using AutoAdmin.Domain;
using Microsoft.Extensions.Options;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The Telegram adapter's buildable half — the request it constructs and how it
/// reads the Bot API's answer — pinned here without reaching Telegram, the same
/// way the Stripe adapter's webhook is (docs/adr/0038). The live round-trip needs
/// a real bot token, which only the owner supplies.
/// </summary>
public sealed class TelegramChannelPublisherTests
{
    private static TelegramChannelPublisher Build(CapturingHandler handler, string? token = "BOT-TOKEN", string? chat = "C123") =>
        new(new HttpClient(handler), Options.Create(new TelegramOptions { BotToken = token, ChatId = chat }));

    private static PublishRequest Request(params GeneratedContent[] content) =>
        new("telegram", "Yalda sale", content);

    [Fact]
    public async Task ItPostsTheCaptionToTheConfiguredChatAndReportsTheMessageId()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"ok":true,"result":{"message_id":77}}""");
        var publisher = Build(handler);

        var outcome = await publisher.PublishAsync(
            Request(new GeneratedContent(ContentKind.Caption, "Buy now #yalda", "simulated")), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("tg://77", outcome.ExternalReference);
        Assert.Contains("/botBOT-TOKEN/sendMessage", handler.Request!.RequestUri!.ToString());
        Assert.Contains("chat_id=C123", handler.RequestBody);
        Assert.Contains("Buy", handler.RequestBody); // the caption, URL-encoded
    }

    [Fact]
    public async Task AnUnconfiguredPublisherReportsRatherThanCallingOut()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var publisher = Build(handler, token: null);

        var outcome = await publisher.PublishAsync(
            Request(new GeneratedContent(ContentKind.Caption, "x", "simulated")), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Null(handler.Request); // it never reached Telegram
    }

    [Fact]
    public async Task ARefusalFromTelegramIsAFailure()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError, "boom");
        var publisher = Build(handler);

        var outcome = await publisher.PublishAsync(
            Request(new GeneratedContent(ContentKind.Caption, "x", "simulated")), CancellationToken.None);

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task AnOkFalseBodyIsAFailure()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"ok":false,"description":"chat not found"}""");
        var publisher = Build(handler);

        var outcome = await publisher.PublishAsync(
            Request(new GeneratedContent(ContentKind.Caption, "x", "simulated")), CancellationToken.None);

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void ComposeMessagePrefersTheCaptionAndFallsBackToTheTopic()
    {
        Assert.Equal(
            "The caption",
            TelegramChannelPublisher.ComposeMessage(Request(new GeneratedContent(ContentKind.Caption, "The caption", "simulated"))));

        // No caption part — the topic carries the post.
        Assert.Equal(
            "Yalda sale",
            TelegramChannelPublisher.ComposeMessage(Request(new GeneratedContent(ContentKind.Image, "an image", "simulated"))));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public CapturingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public HttpRequestMessage? Request { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
        }
    }
}
