using FHIRBridge.Api.Security;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// The workflow /run endpoint checks Execute permissions that no controller attribute declares. Program.cs registers a
/// "HasPermission:{code}" policy only for codes in <see cref="PermissionCatalog.AllPermissionCodes"/>, so each of these
/// must be seeded by hand; a missing one makes every such run fail with "No policy found".
/// </summary>
public sealed class WorkflowRunPermissionPolicyTests
{
    [Theory]
    [InlineData("tabularsources.execute")]
    [InlineData("sourceconnections.execute")]
    public void The_run_endpoint_execute_permission_has_a_registered_policy(string code)
    {
        PermissionCatalog.AllPermissionCodes(typeof(PermissionCatalog).Assembly)
            .Should().Contain(code);
    }
}
