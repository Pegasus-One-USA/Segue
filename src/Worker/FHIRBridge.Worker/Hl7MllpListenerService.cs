using System.Net;
using System.Net.Sockets;
using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Messaging;
using FHIRBridge.Infrastructure.Hl7v2;
using FHIRBridge.Integration.Hl7v2;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Worker;

/// <summary>
/// Listens for HL7 v2 messages over MLLP (TCP), hands each to the <see cref="Hl7MessageProcessor"/>, and writes back
/// the framed ACK. Disabled by default; enable via Hl7Mllp:Enabled with a Port and WebhookConfigurationId.
/// </summary>
public sealed class Hl7MllpListenerService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly Hl7MllpOptions _options;
    private readonly ILogger<Hl7MllpListenerService> _logger;

    public Hl7MllpListenerService(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<Hl7MllpOptions> options,
        ILogger<Hl7MllpListenerService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("HL7 v2 MLLP listener is disabled. Set Hl7Mllp:Enabled=true to start it.");
            return;
        }

        var listener = new TcpListener(IPAddress.Any, _options.Port);
        listener.Start();
        _logger.LogInformation("HL7 v2 MLLP listener started on port {Port}.", _options.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleConnectionAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
            _logger.LogInformation("HL7 v2 MLLP listener stopped.");
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            var buffer = new List<byte>();
            var readBuffer = new byte[4096];

            try
            {
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(readBuffer, cancellationToken)) > 0)
                {
                    buffer.AddRange(readBuffer.AsSpan(0, bytesRead).ToArray());

                    // A single connection may carry multiple framed messages back-to-back.
                    while (Hl7MllpProtocol.ContainsCompleteFrame(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer)))
                    {
                        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer);
                        var message = Hl7MllpProtocol.TryExtractMessage(span);
                        if (message is null)
                        {
                            break;
                        }

                        var ack = await ProcessAsync(message, cancellationToken);
                        var framedAck = Hl7MllpProtocol.Frame(ack);
                        await stream.WriteAsync(framedAck, cancellationToken);
                        await stream.FlushAsync(cancellationToken);

                        RemoveProcessedFrame(buffer);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "HL7 v2 MLLP connection failed.");
            }
        }
    }

    private async Task<string> ProcessAsync(string message, CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<Hl7MessageProcessor>();
        return await processor.ProcessAsync(message, cancellationToken);
    }

    // Drops everything up to and including the first end block so the next frame can be read.
    private static void RemoveProcessedFrame(List<byte> buffer)
    {
        for (var i = 0; i < buffer.Count - 1; i++)
        {
            if (buffer[i] == Hl7MllpProtocol.EndBlock1 && buffer[i + 1] == Hl7MllpProtocol.EndBlock2)
            {
                buffer.RemoveRange(0, i + 2);
                return;
            }
        }

        buffer.Clear();
    }
}
