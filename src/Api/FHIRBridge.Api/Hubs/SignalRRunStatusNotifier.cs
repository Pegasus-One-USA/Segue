using FHIRBridge.Runtime.Application.Workflows;
using Microsoft.AspNetCore.SignalR;

namespace FHIRBridge.Api.Hubs;

/// <summary>
/// The API-hosted implementation of <see cref="IRunStatusNotifier"/> — relays a run's status transition to every
/// connected <see cref="RunStatusHub"/> client. Registered only in this host's DI container (see Program.cs).
/// The Worker's runs arrive here too: it publishes them over Redis and RedisRunStatusRelay forwards them to this
/// notifier. Without Redis they get no live push and clients pick them up on their next REST fetch instead.
/// </summary>
public sealed class SignalRRunStatusNotifier : IRunStatusNotifier
{
    private readonly IHubContext<RunStatusHub> _hubContext;

    public SignalRRunStatusNotifier(IHubContext<RunStatusHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task NotifyAsync(RunStatusChangedEvent statusEvent, CancellationToken cancellationToken)
        => _hubContext.Clients.All.SendAsync("RunStatusChanged", statusEvent, cancellationToken);
}
