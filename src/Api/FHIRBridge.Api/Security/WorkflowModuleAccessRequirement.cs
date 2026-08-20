using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Security;

/// <summary>
/// Satisfied when the current user can reach the Workflow module at all — either they hold the literal
/// <c>workflow.view</c> permission, or they hold ANY permission on a workflow node (a source vendor or
/// destination type usable inside a workflow, e.g. <c>epic.view</c>, <c>sqlserver.edit</c>). This is
/// deliberately separate from <see cref="PermissionRequirement"/>/<see cref="AuthorizationPolicies"/>'s
/// per-code policies: it's an "OR across many codes" check with no single matching wire code of its own,
/// used ONLY to gate view-level access to the module (see <see cref="WorkflowModuleAccessAuthorizationHandler"/>).
/// Workflow CRUD/operation endpoints (build, copy, run, delete) are untouched by this and keep requiring
/// their own literal <c>workflow.create</c>/<c>edit</c>/<c>delete</c>/<c>run</c> code, independently of
/// whatever node permissions the role holds — node access implies module ACCESS, never module CRUD.
/// </summary>
public sealed class WorkflowModuleAccessRequirement : IAuthorizationRequirement
{
}
