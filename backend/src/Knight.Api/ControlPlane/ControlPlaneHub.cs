using System.Security.Claims;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Knight.Api.ControlPlane;

/// <summary>
/// The realtime channel the dashboard listens on.
///
/// The one design decision here is that **the client cannot choose what it
/// receives**. There is no `Subscribe(customerId)` method, because a hub method
/// taking a customer id is a hub method an authenticated customer can call with
/// somebody else's id — and SignalR groups are not covered by the persistence
/// layer's isolation filter, so nothing downstream would catch it. Instead the
/// connection is placed into its groups on connect, from the claims on the token
/// that authenticated it (docs/authorization.md §3).
///
/// A customer principal joins exactly one group: their own. A platform principal
/// joins the platform group and receives everything, which is what the fleet
/// screens need. Nobody joins by asking.
/// </summary>
[Authorize(Policy = ControlPlaneAuthorizationExtensions.UserPolicy)]
public sealed class ControlPlaneHub : Hub
{
    public const string Path = "/hubs/control-plane";

    /// <summary>Everything that is not one customer's business: fleet state, platform alerts.</summary>
    public const string PlatformGroup = "platform";

    private readonly ILogger<ControlPlaneHub> _logger;

    public ControlPlaneHub(ILogger<ControlPlaneHub> logger)
    {
        _logger = logger;
    }

    public static string GroupFor(Guid customerId) => $"customer:{customerId:N}";

    public override async Task OnConnectedAsync()
    {
        // Claims are read from the hub's own principal, never from
        // IHttpContextAccessor. Once a WebSocket has upgraded there is no
        // HttpContext left to read, so a request-scoped principal reports every
        // connection as anonymous — which looks exactly like an attack and
        // silently kills every connection.
        var user = Context.User;

        var isControlPlaneUser =
            user?.FindFirstValue(PrincipalTypes.ClaimType) == PrincipalTypes.User;

        // An outstanding second factor means the session is half-authenticated,
        // and a half-authenticated session must not receive live operational
        // data any more than it may call an endpoint for it.
        if (!isControlPlaneUser || user?.FindFirstValue(ControlPlaneClaims.MfaSatisfied) != "mfa")
        {
            Context.Abort();

            return;
        }

        if (Guid.TryParse(user.FindFirstValue(ControlPlaneClaims.CustomerId), out var customerId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(customerId));
        }
        else
        {
            // No customer claim on a control-plane user means platform staff.
            await Groups.AddToGroupAsync(Context.ConnectionId, PlatformGroup);
        }

        _logger.LogInformation(
            "Realtime connection {ConnectionId} opened for {Subject}.",
            Context.ConnectionId,
            user.FindFirstValue("sub"));

        await base.OnConnectedAsync();
    }
}

/// <summary>
/// Pushes a message to the connections entitled to see it.
///
/// The routing rule mirrors the persistence filter exactly: a message about one
/// customer goes to that customer and to platform staff; a message about
/// platform infrastructure goes to platform staff only. "No customer" is never
/// read as "everyone" — the same failing-closed rule the query filter applies
/// (docs/authorization.md §3).
/// </summary>
internal sealed class SignalRRealtimeNotifier : IRealtimeNotifier
{
    private readonly IHubContext<ControlPlaneHub> _hub;
    private readonly ILogger<SignalRRealtimeNotifier> _logger;

    public SignalRRealtimeNotifier(IHubContext<ControlPlaneHub> hub, ILogger<SignalRRealtimeNotifier> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task BroadcastAsync(RealtimeMessage message, CancellationToken cancellationToken)
    {
        try
        {
            if (message.CustomerId is { } customerId)
            {
                await _hub.Clients
                    .Groups(ControlPlaneHub.GroupFor(customerId), ControlPlaneHub.PlatformGroup)
                    .SendAsync(message.Event, message.Payload, cancellationToken);

                return;
            }

            await _hub.Clients
                .Group(ControlPlaneHub.PlatformGroup)
                .SendAsync(message.Event, message.Payload, cancellationToken);
        }
        catch (Exception exception)
        {
            // Realtime is an improvement on polling, never something correctness
            // depends on. The change is already saved; the dashboard picks it up
            // on its next fetch.
            _logger.LogWarning(exception, "Failed to broadcast {Event} over the realtime hub.", message.Event);
        }
    }
}

/// <summary>
/// What a host without a hub uses.
///
/// Registered by the integration-test host and anywhere the API is composed
/// without SignalR, so that a background sweep does not fail because nobody is
/// listening. Dropping a broadcast is the correct behaviour when there is no
/// channel; failing the operation that produced it is not.
/// </summary>
internal sealed class NullRealtimeNotifier : IRealtimeNotifier
{
    public Task BroadcastAsync(RealtimeMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
}
