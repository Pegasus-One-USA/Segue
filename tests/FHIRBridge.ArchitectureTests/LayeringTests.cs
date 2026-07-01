using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;

namespace FHIRBridge.ArchitectureTests;

/// <summary>
/// Enforces the clean-architecture dependency direction: domain layers depend on nothing above them, and
/// application layers never reach into infrastructure or the API host. Dependencies on the layers above are
/// checked by namespace string, so this holds even though the test project does not reference the
/// Infrastructure/Api assemblies. Both the classic (FHIRBridge.*) and runtime (FHIRBridge.Runtime.*) stacks
/// are asserted.
/// </summary>
public sealed class LayeringTests
{
    private const string DomainAssembly = "FHIRBridge.Domain";
    private const string ApplicationAssembly = "FHIRBridge.Application";
    private const string RuntimeDomainAssembly = "FHIRBridge.Runtime.Domain";
    private const string RuntimeApplicationAssembly = "FHIRBridge.Runtime.Application";

    private const string InfrastructureNamespace = "FHIRBridge.Infrastructure";
    private const string RuntimeInfrastructureNamespace = "FHIRBridge.Runtime.Infrastructure";
    private const string ApiNamespace = "FHIRBridge.Api";

    [Fact]
    public void Domain_should_not_depend_on_application_infrastructure_or_api()
    {
        AssertNoDependencyOn(
            DomainAssembly,
            ApplicationAssembly,
            InfrastructureNamespace,
            RuntimeInfrastructureNamespace,
            ApiNamespace);
    }

    [Fact]
    public void Application_should_not_depend_on_infrastructure_or_api()
    {
        AssertNoDependencyOn(
            ApplicationAssembly,
            InfrastructureNamespace,
            RuntimeInfrastructureNamespace,
            ApiNamespace);
    }

    [Fact]
    public void Runtime_domain_should_not_depend_on_application_infrastructure_or_api()
    {
        AssertNoDependencyOn(
            RuntimeDomainAssembly,
            RuntimeApplicationAssembly,
            ApplicationAssembly,
            InfrastructureNamespace,
            RuntimeInfrastructureNamespace,
            ApiNamespace);
    }

    [Fact]
    public void Runtime_application_should_not_depend_on_infrastructure_or_api()
    {
        AssertNoDependencyOn(
            RuntimeApplicationAssembly,
            RuntimeInfrastructureNamespace,
            InfrastructureNamespace,
            ApiNamespace);
    }

    private static void AssertNoDependencyOn(string assemblyName, params string[] forbiddenNamespaces)
    {
        var assembly = Assembly.Load(new AssemblyName(assemblyName));

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespaces)
            .GetResult();

        var offenders = result.FailingTypeNames is null
            ? string.Empty
            : string.Join(", ", result.FailingTypeNames);

        result.IsSuccessful.Should().BeTrue(
            $"{assemblyName} must not depend on [{string.Join(", ", forbiddenNamespaces)}]. Offending types: {offenders}");
    }
}
