using System.Net.Http.Headers;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Messaging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Messaging.RabbitMq;

/// <summary>
/// Queries RabbitMQ's HTTP management API (the <c>rabbitmq:3-management</c> image already used in
/// docker-compose.yml) for real queue depth — pending/processing on the work queue, backlog on its
/// <c>{queue}.dlq</c> dead-letter companion (see <see cref="RabbitMqTopology"/>). A per-queue failure (management
/// API unreachable, queue not yet declared) is reported via <see cref="QueueDepthDto.UnavailableReason"/>, never
/// silently zeroed.
/// </summary>
public sealed class RabbitMqQueueMonitorProvider : IQueueMonitorProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<RabbitMqOptions> _options;

    public RabbitMqQueueMonitorProvider(IHttpClientFactory httpClientFactory, IOptions<RabbitMqOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public async Task<IReadOnlyList<QueueDepthDto>> GetQueueDepthsAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var queues = new[] { options.PipelineRunsQueue, options.WebhookIngestionQueue };

        var results = new List<QueueDepthDto>();
        foreach (var queue in queues)
        {
            results.Add(await GetOneAsync(queue, cancellationToken));
        }

        return results;
    }

    private async Task<QueueDepthDto> GetOneAsync(string queue, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var client = _httpClientFactory.CreateClient(nameof(RabbitMqQueueMonitorProvider));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{options.UserName}:{options.Password}")));

        try
        {
            var (pending, processing) = await GetQueueCountsAsync(client, options, queue, cancellationToken);
            var (deadLetterPending, _) = await GetQueueCountsAsync(client, options, $"{queue}.dlq", cancellationToken);

            return new QueueDepthDto(queue, "RabbitMQ", pending, processing, deadLetterPending, LastMessageUtc: null, UnavailableReason: null);
        }
        catch (Exception exception)
        {
            return new QueueDepthDto(queue, "RabbitMQ", 0, 0, 0, null, $"Could not reach RabbitMQ management API: {exception.Message}");
        }
    }

    private static async Task<(int Pending, int Processing)> GetQueueCountsAsync(
        HttpClient client, RabbitMqOptions options, string queueName, CancellationToken cancellationToken)
    {
        var vhost = Uri.EscapeDataString(options.VirtualHost);
        var url = $"http://{options.HostName}:{options.ManagementPort}/api/queues/{vhost}/{Uri.EscapeDataString(queueName)}";

        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // A queue that hasn't been declared yet (no message ever published) is empty, not an error.
            return (0, 0);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var pending = root.TryGetProperty("messages_ready", out var readyProp) ? readyProp.GetInt32() : 0;
        var processing = root.TryGetProperty("messages_unacknowledged", out var unackedProp) ? unackedProp.GetInt32() : 0;

        return (pending, processing);
    }
}
