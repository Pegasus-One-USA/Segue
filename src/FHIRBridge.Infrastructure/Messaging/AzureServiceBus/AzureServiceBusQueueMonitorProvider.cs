using Azure.Identity;
using Azure.Messaging.ServiceBus.Administration;
using FHIRBridge.Application.Abstractions.Messaging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Messaging.AzureServiceBus;

/// <summary>
/// Queries the Azure Service Bus admin API for real queue runtime properties (active/dead-letter message counts).
/// A per-queue failure (queue not provisioned yet, auth failure) is reported via
/// <see cref="QueueDepthDto.UnavailableReason"/>, never silently zeroed.
/// </summary>
public sealed class AzureServiceBusQueueMonitorProvider : IQueueMonitorProvider
{
    private readonly IOptions<AzureServiceBusOptions> _options;

    public AzureServiceBusQueueMonitorProvider(IOptions<AzureServiceBusOptions> options)
    {
        _options = options;
    }

    public async Task<IReadOnlyList<QueueDepthDto>> GetQueueDepthsAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        ServiceBusAdministrationClient client;
        try
        {
            client = CreateClient(options);
        }
        catch (Exception exception)
        {
            var reason = $"Could not create Azure Service Bus admin client: {exception.Message}";
            return
            [
                new QueueDepthDto(options.PipelineRunsQueue, "AzureServiceBus", 0, 0, 0, null, reason),
                new QueueDepthDto(options.WebhookIngestionQueue, "AzureServiceBus", 0, 0, 0, null, reason),
            ];
        }

        return
        [
            await GetOneAsync(client, options.PipelineRunsQueue, cancellationToken),
            await GetOneAsync(client, options.WebhookIngestionQueue, cancellationToken),
        ];
    }

    private static async Task<QueueDepthDto> GetOneAsync(
        ServiceBusAdministrationClient client, string queueName, CancellationToken cancellationToken)
    {
        try
        {
            var runtimeProperties = await client.GetQueueRuntimePropertiesAsync(queueName, cancellationToken);
            var properties = runtimeProperties.Value;

            return new QueueDepthDto(
                queueName,
                "AzureServiceBus",
                Pending: (int)properties.ActiveMessageCount,
                Processing: 0, // Service Bus doesn't expose an "in-flight/unacked" count distinct from active.
                DeadLetter: (int)properties.DeadLetterMessageCount,
                LastMessageUtc: properties.AccessedAt == default ? null : properties.AccessedAt.UtcDateTime,
                UnavailableReason: null);
        }
        catch (Exception exception)
        {
            return new QueueDepthDto(queueName, "AzureServiceBus", 0, 0, 0, null,
                $"Could not read queue runtime properties: {exception.Message}");
        }
    }

    private static ServiceBusAdministrationClient CreateClient(AzureServiceBusOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new ServiceBusAdministrationClient(options.ConnectionString);
        }

        if (!string.IsNullOrWhiteSpace(options.FullyQualifiedNamespace))
        {
            return new ServiceBusAdministrationClient(options.FullyQualifiedNamespace, new DefaultAzureCredential());
        }

        throw new InvalidOperationException(
            "Messaging:AzureServiceBus requires either a ConnectionString or a FullyQualifiedNamespace.");
    }
}
