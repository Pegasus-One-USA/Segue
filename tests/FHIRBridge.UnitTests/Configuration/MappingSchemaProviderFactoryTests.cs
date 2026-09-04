using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.UnitTests.Configuration;

/// <summary>
/// Regression guard for the "MySQL mapping profiles can never be saved" bug: MappingImportService always calls
/// IMappingSchemaProviderFactory.Create(destination.DestinationType) unconditionally (see
/// MappingImportService.ImportResourceMappingAsync) — before this fix, MySql wasn't in
/// MappingSchemaProviderFactory.DefaultRegistrations at all, so every MySQL import threw NotSupportedException
/// regardless of whether any actual schema change was needed. Covers the resolution behavior directly, without
/// needing a live database (a real DB connection is only opened later, inside BeginTransactionAsync, which
/// these tests never call).
/// </summary>
public sealed class MappingSchemaProviderFactoryTests
{
    private sealed class FakeSecretProvider : ISecretProvider
    {
        public Task<string> GetSecretAsync(SecretReference secretReference, CancellationToken cancellationToken) =>
            Task.FromResult("fake-connection-string");
    }

    private static MappingSchemaProviderFactory CreateSut()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecretProvider, FakeSecretProvider>();
        services.AddScoped<SqlServerMappingSchemaProvider>();
        services.AddScoped<MySqlMappingSchemaProvider>();
        services.AddScoped<PostgreSqlMappingSchemaProvider>();
        foreach (var registration in MappingSchemaProviderFactory.DefaultRegistrations)
        {
            services.AddSingleton(registration);
        }

        var provider = services.BuildServiceProvider();
        return new MappingSchemaProviderFactory(
            provider, provider.GetServices<MappingSchemaProviderRegistration>());
    }

    [Fact]
    public void MySql_resolves_to_a_real_provider_instead_of_throwing()
    {
        var factory = CreateSut();

        var resolved = factory.Create(DestinationType.MySql);

        resolved.Should().BeOfType<MySqlMappingSchemaProvider>();
    }

    [Fact]
    public void PostgreSql_resolves_to_a_real_provider_instead_of_throwing()
    {
        var factory = CreateSut();

        var resolved = factory.Create(DestinationType.PostgreSql);

        resolved.Should().BeOfType<PostgreSqlMappingSchemaProvider>();
    }

    [Fact]
    public void SqlServer_still_resolves_as_before_the_MySql_registration_was_added()
    {
        var factory = CreateSut();

        var resolved = factory.Create(DestinationType.SqlServer);

        resolved.Should().BeOfType<SqlServerMappingSchemaProvider>();
    }

    [Fact]
    public void AzureSql_still_resolves_to_the_shared_SqlServerMappingSchemaProvider()
    {
        var factory = CreateSut();

        var resolved = factory.Create(DestinationType.AzureSql);

        resolved.Should().BeOfType<SqlServerMappingSchemaProvider>();
    }

    [Fact]
    public void A_genuinely_unregistered_destination_type_still_throws_NotSupportedException()
    {
        var factory = CreateSut();

        var act = () => factory.Create(DestinationType.Mongo);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Mongo*does not support mapping schema import yet*");
    }
}
