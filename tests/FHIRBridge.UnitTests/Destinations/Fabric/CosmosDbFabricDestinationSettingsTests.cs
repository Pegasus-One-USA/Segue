using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
using FHIRBridge.Infrastructure.Destinations.Fabric;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Destinations.Fabric;

/// <summary>
/// Covers parsing and container resolution for Cosmos DB in Fabric — everything decided before a network call.
/// The client itself needs a live tenant (and Fabric's Gateway-only connection mode is a property of the service,
/// not of this code), so it is exercised against a real database rather than a mock that would only prove this
/// test's own assumptions.
/// </summary>
public sealed class CosmosDbFabricDestinationSettingsTests
{
    private const string ValidMetadata =
        """
        {"dest_cosmosFabricEndpoint":"https://abc.documents.fabric.microsoft.com",
         "dest_cosmosFabricDatabase":"Clinical"}
        """;

    private static DestinationConfiguration Destination(string? metadata) =>
        new("Cosmos Fabric", DestinationType.CosmosDbFabric, new SecretReference("kv", "secret"), null, metadata);

    private static MappingProfile Mapping(string resourceType = "Patient", string destinationObject = "Patients") =>
        new("Cosmos Mapping", resourceType, Guid.NewGuid(), Guid.NewGuid(), destinationObject, []);

    [Fact]
    public void Settings_parse_with_their_defaults()
    {
        var settings = CosmosDbFabricDestinationSettings.Parse(Destination(ValidMetadata));

        settings.Endpoint.Should().Be("https://abc.documents.fabric.microsoft.com");
        settings.Database.Should().Be("Clinical");
        settings.Container.Should().BeNull();
        settings.PartitionKeyPath.Should().BeNull();
        // Managed identity is the default, matching every other Fabric surface.
        settings.AuthMode.Should().Be(FabricAuthMode.ManagedIdentity);
        settings.RequiresSecret.Should().BeFalse();
    }

    /// <summary>
    /// Endpoint and database cannot be defaulted — neither is derivable from anything else on the destination,
    /// so a missing one is a configuration error caught at parse time rather than a failure partway through a
    /// write.
    /// </summary>
    [Theory]
    [InlineData("""{"dest_cosmosFabricDatabase":"Clinical"}""", "*endpoint*")]
    [InlineData("""{"dest_cosmosFabricEndpoint":"https://abc.documents.fabric.microsoft.com"}""", "*database name*")]
    public void Missing_required_settings_are_refused_by_name(string metadata, string expected)
    {
        var parse = () => CosmosDbFabricDestinationSettings.Parse(Destination(metadata));

        parse.Should().Throw<InvalidOperationException>().WithMessage(expected);
    }

    /// <summary>
    /// A hostname pasted without its scheme is the obvious mistake, and CosmosClient's own error for it arrives
    /// at write time and names the URI rather than the setting — so it is caught here instead.
    /// </summary>
    [Theory]
    [InlineData("abc.documents.fabric.microsoft.com")]
    [InlineData("not a url")]
    public void A_malformed_endpoint_is_refused_with_where_to_find_the_right_one(string endpoint)
    {
        var parse = () => CosmosDbFabricDestinationSettings.Parse(Destination(
            $$"""{"dest_cosmosFabricEndpoint":"{{endpoint}}","dest_cosmosFabricDatabase":"Clinical"}"""));

        parse.Should().Throw<InvalidOperationException>().WithMessage("*absolute https URL*Settings*");
    }

    /// <summary>
    /// Service principal needs tenant and client ids; managed identity needs neither. Same rule as every other
    /// Fabric surface, so one identity is configured the same way whichever it reaches.
    /// </summary>
    [Fact]
    public void Service_principal_requires_its_full_credential_set()
    {
        var parse = () => CosmosDbFabricDestinationSettings.Parse(Destination(
            """
            {"dest_cosmosFabricEndpoint":"https://abc.documents.fabric.microsoft.com",
             "dest_cosmosFabricDatabase":"Clinical","dest_cosmosFabricAuthMode":"servicePrincipal"}
            """));

        parse.Should().Throw<InvalidOperationException>().WithMessage("*tenant id*");
    }

    [Fact]
    public void Service_principal_resolves_a_secret_and_managed_identity_does_not()
    {
        var servicePrincipal = CosmosDbFabricDestinationSettings.Parse(Destination(
            """
            {"dest_cosmosFabricEndpoint":"https://abc.documents.fabric.microsoft.com",
             "dest_cosmosFabricDatabase":"Clinical","dest_cosmosFabricAuthMode":"servicePrincipal",
             "dest_cosmosFabricTenantId":"t","dest_cosmosFabricClientId":"c"}
            """));

        servicePrincipal.RequiresSecret.Should().BeTrue();
        CosmosDbFabricDestinationSettings.Parse(Destination(ValidMetadata)).RequiresSecret.Should().BeFalse();
    }

    /// <summary>
    /// The wizard posts every field it renders, so an untouched optional one arrives as "" rather than absent.
    /// Normalising at parse time is what stops an empty override reaching a consumer as a meaningful value — the
    /// bug an empty account-url once caused for OneLake.
    /// </summary>
    [Fact]
    public void Blank_optional_fields_normalize_to_null()
    {
        var settings = CosmosDbFabricDestinationSettings.Parse(Destination(
            """
            {"dest_cosmosFabricEndpoint":"https://abc.documents.fabric.microsoft.com",
             "dest_cosmosFabricDatabase":"Clinical","dest_cosmosFabricContainer":"",
             "dest_cosmosFabricAuthorityHost":"","dest_cosmosFabricManagedIdentityClientId":"",
             "dest_cosmosFabricPartitionKeyPath":""}
            """));

        settings.Container.Should().BeNull();
        settings.AuthorityHost.Should().BeNull();
        settings.ManagedIdentityClientId.Should().BeNull();
        settings.PartitionKeyPath.Should().BeNull();
    }

    /// <summary>
    /// A partition key path is a JSON pointer, and typing the bare field name is the obvious mistake. Cosmos
    /// rejects it with a message about the path rather than the missing slash, so it is corrected here.
    /// </summary>
    [Theory]
    [InlineData("resourceType", "/resourceType")]
    [InlineData("/resourceType", "/resourceType")]
    [InlineData("  resourceType  ", "/resourceType")]
    public void A_partition_key_path_gains_its_leading_slash(string configured, string expected)
        => CosmosDbFabricDestinationSettings.NormalizePartitionKeyPath(configured).Should().Be(expected);

    /// <summary>
    /// A container name is a flat identifier, so a schema-qualified mapping target would otherwise create one
    /// literally called "dbo.Patient". Same rule the Lakehouse Delta surface applies, so one mapping profile
    /// names the same thing on either destination.
    /// </summary>
    [Theory]
    [InlineData("dbo.Patient", "Patient")]
    [InlineData("Patient", "Patient")]
    [InlineData("dbo.Patient;mode=upsert", "Patient")]
    public void The_container_is_the_last_segment_of_the_mapping_target(string destinationObject, string expected)
    {
        var settings = CosmosDbFabricDestinationSettings.Parse(Destination(ValidMetadata));

        settings.ResolveContainer(Mapping(destinationObject: destinationObject)).Should().Be(expected);
    }

    [Fact]
    public void An_explicit_container_override_wins()
    {
        var settings = CosmosDbFabricDestinationSettings.Parse(Destination(
            """
            {"dest_cosmosFabricEndpoint":"https://abc.documents.fabric.microsoft.com",
             "dest_cosmosFabricDatabase":"Clinical","dest_cosmosFabricContainer":"PatientGold"}
            """));

        settings.ResolveContainer(Mapping(destinationObject: "dbo.Patient")).Should().Be("PatientGold");
    }

    [Fact]
    public void An_unusable_mapping_target_falls_back_to_the_resource_type()
    {
        var settings = CosmosDbFabricDestinationSettings.Parse(Destination(ValidMetadata));

        settings.ResolveContainer(Mapping("Observation", destinationObject: "")).Should().Be("Observation");
    }

    /// <summary>
    /// The default is the only mode under which FHIRBridge cannot commit the customer to a partition key at
    /// all. A destination that says nothing about container creation must therefore not create them.
    /// </summary>
    [Fact]
    public void Containers_are_never_created_unless_the_destination_opts_in()
    {
        var settings = CosmosDbFabricDestinationSettings.Parse(Destination(ValidMetadata));

        settings.ContainerCreationMode.Should().Be(CosmosContainerCreationMode.Never);
        settings.CanCreateContainer.Should().BeFalse();
        settings.PartitionKeyPathForNewContainer.Should().BeNull();
        settings.CreatesOnUnchosenPartitionKey.Should().BeFalse();
    }

    /// <summary>
    /// Creating on the user's own key uses exactly that key — the whole point of the mode is that nothing is
    /// guessed, so nothing here may substitute a default.
    /// </summary>
    [Fact]
    public void Creating_on_a_configured_key_uses_that_key()
    {
        var settings = CosmosDbFabricDestinationSettings.Parse(Destination(
            """
            {"dest_cosmosFabricEndpoint":"https://abc.documents.fabric.microsoft.com",
             "dest_cosmosFabricDatabase":"Clinical",
             "dest_cosmosFabricContainerCreationMode":"useConfiguredPartitionKey",
             "dest_cosmosFabricPartitionKeyPath":"/subject/reference"}
            """));

        settings.CanCreateContainer.Should().BeTrue();
        settings.PartitionKeyPathForNewContainer.Should().Be("/subject/reference");
        // The user chose it, so there is nothing to warn about.
        settings.CreatesOnUnchosenPartitionKey.Should().BeFalse();
    }

    /// <summary>
    /// Refused, NOT silently downgraded to the default key. Falling back would hand the user the exact outcome
    /// selecting this mode says they want to avoid, and a partition key cannot be changed after creation.
    /// </summary>
    [Fact]
    public void Creating_on_a_configured_key_is_refused_when_no_key_is_configured()
    {
        var parse = () => CosmosDbFabricDestinationSettings.Parse(Destination(
            """
            {"dest_cosmosFabricEndpoint":"https://abc.documents.fabric.microsoft.com",
             "dest_cosmosFabricDatabase":"Clinical",
             "dest_cosmosFabricContainerCreationMode":"useConfiguredPartitionKey"}
            """));

        parse.Should().Throw<InvalidOperationException>()
            .WithMessage("*partition key*");
    }

    /// <summary>
    /// The default-key mode falls back to /id and reports that nobody chose it, which is what drives the
    /// writer's warning. The flag matters more than the path: it is the only signal that an irreversible
    /// decision was made by a default rather than by a person.
    /// </summary>
    [Fact]
    public void The_default_key_mode_uses_slash_id_and_says_it_was_not_chosen()
    {
        var settings = CosmosDbFabricDestinationSettings.Parse(Destination(
            """
            {"dest_cosmosFabricEndpoint":"https://abc.documents.fabric.microsoft.com",
             "dest_cosmosFabricDatabase":"Clinical",
             "dest_cosmosFabricContainerCreationMode":"useDefaultPartitionKey"}
            """));

        settings.CanCreateContainer.Should().BeTrue();
        settings.PartitionKeyPathForNewContainer.Should().Be("/id");
        settings.CreatesOnUnchosenPartitionKey.Should().BeTrue();
    }

    /// <summary>
    /// A key configured alongside the default-key mode still wins — the mode says "a default is acceptable",
    /// not "ignore what I typed". Nothing is then unchosen, so no warning is raised.
    /// </summary>
    [Fact]
    public void A_configured_key_beats_the_default_even_under_the_default_mode()
    {
        var settings = CosmosDbFabricDestinationSettings.Parse(Destination(
            """
            {"dest_cosmosFabricEndpoint":"https://abc.documents.fabric.microsoft.com",
             "dest_cosmosFabricDatabase":"Clinical",
             "dest_cosmosFabricContainerCreationMode":"useDefaultPartitionKey",
             "dest_cosmosFabricPartitionKeyPath":"resourceType"}
            """));

        // Also confirms the leading slash is added, as it is for every other path.
        settings.PartitionKeyPathForNewContainer.Should().Be("/resourceType");
        settings.CreatesOnUnchosenPartitionKey.Should().BeFalse();
    }

    /// <summary>
    /// An unrecognized mode falls back to Never rather than to a creating one. A destination saved by a newer
    /// portal, or hand-edited, must not start creating containers because this could not read its setting.
    /// </summary>
    [Fact]
    public void An_unrecognized_creation_mode_falls_back_to_never_creating()
    {
        var settings = CosmosDbFabricDestinationSettings.Parse(Destination(
            """
            {"dest_cosmosFabricEndpoint":"https://abc.documents.fabric.microsoft.com",
             "dest_cosmosFabricDatabase":"Clinical",
             "dest_cosmosFabricContainerCreationMode":"createEverythingPlease"}
            """));

        settings.ContainerCreationMode.Should().Be(CosmosContainerCreationMode.Never);
        settings.CanCreateContainer.Should().BeFalse();
    }
}
