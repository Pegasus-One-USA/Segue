using Azure.Identity;
using Azure.Messaging.ServiceBus;
using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Infrastructure.Messaging.AzureServiceBus;
using FHIRBridge.Infrastructure.Messaging.RabbitMq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Messaging;

/// <summary>
/// Registers the messaging transport selected by <c>Messaging:Provider</c>. Phase 0 implements the InMemory
/// provider (the default); RabbitMq (Phase 3) and AzureServiceBus (Phase 4) are registered here as they land.
/// </summary>
public static class MessagingServiceCollectionExtensions
{
    public static IServiceCollection AddMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Command handlers + retry policy are transport-agnostic — registered regardless of the selected provider.
        var processingSection = configuration.GetSection("Messaging:Processing");
        services.AddSingleton(Options.Create(new MessageProcessingOptions
        {
            MaxAttempts = int.TryParse(processingSection["MaxAttempts"], out var attempts) ? attempts : 3,
            BaseDelayMilliseconds = int.TryParse(processingSection["BaseDelayMilliseconds"], out var delay) ? delay : 500
        }));
        services.AddScoped<IPipelineRunCommandHandler, PipelineRunCommandHandler>();
        services.AddScoped<IWebhookIngestionCommandHandler, WebhookIngestionCommandHandler>();
        services.AddScoped<ILineageCaptureCommandHandler, LineageCaptureCommandHandler>();

        var provider = configuration["Messaging:Provider"];

        if (string.IsNullOrWhiteSpace(provider) ||
            string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            return AddInMemoryMessaging(services);
        }

        if (string.Equals(provider, "RabbitMq", StringComparison.OrdinalIgnoreCase))
        {
            return AddRabbitMqMessaging(services, configuration);
        }

        if (string.Equals(provider, "AzureServiceBus", StringComparison.OrdinalIgnoreCase))
        {
            return AddAzureServiceBusMessaging(services, configuration);
        }

        throw new NotSupportedException(
            $"Messaging provider '{provider}' is not available. Use 'InMemory', 'RabbitMq', or 'AzureServiceBus'.");
    }

    private static IServiceCollection AddAzureServiceBusMessaging(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Messaging:AzureServiceBus");
        var options = new AzureServiceBusOptions
        {
            ConnectionString = section["ConnectionString"],
            FullyQualifiedNamespace = section["FullyQualifiedNamespace"],
            PipelineRunsQueue = section["PipelineRunsQueue"] ?? "pipeline-runs",
            WebhookIngestionQueue = section["WebhookIngestionQueue"] ?? "webhook-ingestion",
            MaxConcurrentCalls = int.TryParse(section["MaxConcurrentCalls"], out var calls) ? calls : 5
        };

        services.AddSingleton(Options.Create(options));
        services.AddSingleton(serviceProvider =>
        {
            var resolved = serviceProvider.GetRequiredService<IOptions<AzureServiceBusOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(resolved.ConnectionString))
            {
                return new ServiceBusClient(resolved.ConnectionString);
            }

            if (!string.IsNullOrWhiteSpace(resolved.FullyQualifiedNamespace))
            {
                return new ServiceBusClient(resolved.FullyQualifiedNamespace, new DefaultAzureCredential());
            }

            throw new InvalidOperationException(
                "Messaging:AzureServiceBus requires either a ConnectionString or a FullyQualifiedNamespace.");
        });

        services.AddSingleton<AzureServiceBusPublisher>();
        services.AddSingleton<IPipelineRunDispatcher, AzureServiceBusPipelineRunDispatcher>();
        services.AddSingleton<IWebhookIngestionDispatcher, AzureServiceBusWebhookIngestionDispatcher>();
        services.AddSingleton<ILineageCaptureDispatcher, AzureServiceBusLineageCaptureDispatcher>();
        services.AddSingleton(typeof(IMessageConsumer<>), typeof(AzureServiceBusMessageConsumer<>));
        services.AddSingleton<IQueueMonitorProvider, AzureServiceBusQueueMonitorProvider>();

        return services;
    }

    private static IServiceCollection AddRabbitMqMessaging(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Messaging:RabbitMq");
        var options = new RabbitMqOptions
        {
            HostName = section["HostName"] ?? "localhost",
            Port = int.TryParse(section["Port"], out var port) ? port : 5672,
            ManagementPort = int.TryParse(section["ManagementPort"], out var managementPort) ? managementPort : 15672,
            UserName = section["UserName"] ?? "fhirbridge",
            Password = section["Password"] ?? "fhirbridge",
            VirtualHost = section["VirtualHost"] ?? "/",
            PipelineRunsQueue = section["PipelineRunsQueue"] ?? "pipeline-runs",
            WebhookIngestionQueue = section["WebhookIngestionQueue"] ?? "webhook-ingestion",
            PrefetchCount = ushort.TryParse(section["PrefetchCount"], out var prefetch) ? prefetch : (ushort)10
        };

        services.AddSingleton(Options.Create(options));
        services.AddSingleton<RabbitMqConnection>();
        services.AddSingleton<RabbitMqPublisher>();
        services.AddSingleton<IPipelineRunDispatcher, RabbitMqPipelineRunDispatcher>();
        services.AddSingleton<IWebhookIngestionDispatcher, RabbitMqWebhookIngestionDispatcher>();
        services.AddSingleton<ILineageCaptureDispatcher, RabbitMqLineageCaptureDispatcher>();
        services.AddSingleton(typeof(IMessageConsumer<>), typeof(RabbitMqMessageConsumer<>));
        services.AddSingleton<IQueueMonitorProvider, RabbitMqQueueMonitorProvider>();

        return services;
    }

    private static IServiceCollection AddInMemoryMessaging(IServiceCollection services)
    {
        services.AddSingleton(typeof(InMemoryMessageChannel<>));
        services.AddSingleton<IPipelineRunDispatcher, InMemoryPipelineRunDispatcher>();
        services.AddSingleton<IWebhookIngestionDispatcher, InMemoryWebhookIngestionDispatcher>();
        services.AddSingleton<ILineageCaptureDispatcher, InMemoryLineageCaptureDispatcher>();
        services.AddSingleton(typeof(IMessageConsumer<>), typeof(InMemoryMessageConsumer<>));
        services.AddSingleton<IQueueMonitorProvider, NullQueueMonitorProvider>();

        return services;
    }
}
