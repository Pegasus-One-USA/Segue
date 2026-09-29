using FHIRBridge.Application.Services.Terminology;
using Microsoft.AspNetCore.SignalR;

namespace FHIRBridge.Api.Hubs;

/// <summary>
/// The API-hosted implementation of <see cref="ITerminologyStatusNotifier"/> — relays one sync's status
/// transition to every connected <see cref="TerminologyStatusHub"/> client. Registered only in this host's DI
/// container (see Program.cs); the Worker process never registers one, so a SCHEDULED sync it runs still
/// records its history normally, just without a live push — see the interface's own remarks.
/// </summary>
public sealed class SignalRTerminologyStatusNotifier : ITerminologyStatusNotifier
{
    private readonly IHubContext<TerminologyStatusHub> _hubContext;

    public SignalRTerminologyStatusNotifier(IHubContext<TerminologyStatusHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task NotifyAsync(TerminologyStatusChangedEvent statusEvent, CancellationToken cancellationToken)
        => _hubContext.Clients.All.SendAsync("TerminologyStatusChanged", statusEvent, cancellationToken);
}
