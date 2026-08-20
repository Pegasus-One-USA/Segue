using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Enums;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

public sealed class WorkflowModuleAccessAuthorizationHandler : AuthorizationHandler<WorkflowModuleAccessRequirement>
{
    // The set of permission-group wire-prefixes that represent a workflow "node" (a source vendor or
    // destination type usable inside a workflow) rather than the Workflow module itself — e.g. "epic",
    // "sqlserver". Derived from SourceSystemPermissionGroups.AllGroupsFor, the exact same enum-crossing
    // PermissionCatalog already uses to auto-discover epic.view/sqlserver.edit/etc. at startup — not
    // hand-maintained here, so a newly added vendor or destination type (with its own same-named
    // PermissionGroupCode member) is picked up automatically with no edit to this file.
    private static readonly Lazy<HashSet<string>> NodeGroupPrefixes = new(() =>
        new HashSet<string>(
            SourceSystemPermissionGroups.AllGroupsFor(typeof(SourceSystemType))
                .Concat(SourceSystemPermissionGroups.AllGroupsFor(typeof(DestinationType)))
                .Select(group => group.ToString()),
            StringComparer.OrdinalIgnoreCase));

    private readonly ICurrentUserService _currentUserService;
    private readonly IUserPermissionsProvider _permissionsProvider;

    public WorkflowModuleAccessAuthorizationHandler(
        ICurrentUserService currentUserService,
        IUserPermissionsProvider permissionsProvider)
    {
        _currentUserService = currentUserService;
        _permissionsProvider = permissionsProvider;
    }

    // Module ACCESS only — succeeds on workflow.view OR any node-group permission (any action). Never
    // used for workflow.create/edit/delete/run, which stay gated on their own literal code, independent
    // of node permissions, at the endpoints that actually mutate/run a workflow (see WorkflowEndpoints.cs).
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WorkflowModuleAccessRequirement requirement)
    {
        var userId = _currentUserService.CurrentUser.UserId;
        if (userId is null)
        {
            return;
        }

        var codes = await _permissionsProvider.GetEffectivePermissionCodesAsync(userId.Value, CancellationToken.None);

        var hasModuleAccess = codes.Contains("workflow.view", StringComparer.OrdinalIgnoreCase)
            || codes.Any(IsWorkflowNodeCode);

        if (hasModuleAccess)
        {
            context.Succeed(requirement);
        }
    }

    private static bool IsWorkflowNodeCode(string code)
    {
        var dot = code.IndexOf('.');
        var group = dot >= 0 ? code[..dot] : code;
        return NodeGroupPrefixes.Value.Contains(group);
    }
}
