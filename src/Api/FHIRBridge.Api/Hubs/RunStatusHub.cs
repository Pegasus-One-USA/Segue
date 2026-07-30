using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FHIRBridge.Api.Hubs;

/// <summary>
/// Pure server-to-client push channel for workflow-run status changes (Running/Succeeded/Failed) — backs the
/// Dashboard's "Running" stat tile/Recent Pipelines table and the Workflow List's per-row status, replacing what
/// would otherwise be periodic polling. Clients never call a method on this hub; it only ever sends
/// "RunStatusChanged" (see <see cref="SignalRRunStatusNotifier"/> for the event shape).
///
/// No tenant/group scoping: <see cref="Runtime.Domain.Workflows.WorkflowRun"/> carries no TenantId today (single
/// app-level tenant per FHIRBridge install), so every authenticated connection receives every run's status —
/// consistent with how every other Dashboard/Workflow List query already works uniformly across the whole install.
/// </summary>
[Authorize]
public sealed class RunStatusHub : Hub
{
}
