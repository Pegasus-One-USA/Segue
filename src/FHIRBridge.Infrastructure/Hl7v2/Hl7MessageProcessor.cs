using System.Security.Cryptography;
using System.Text;
using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Messaging;
using FHIRBridge.Integration.Hl7v2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Hl7v2;

/// <summary>
/// Turns a raw HL7 v2 message into an FHIR ingestion command and returns the MLLP ACK to send back. Parses the
/// message, maps it to a FHIR Bundle, enqueues a <see cref="WebhookIngestionCommand"/> on the shared transport, and
/// returns an AA ack — or an AE ack carrying the error when parsing/mapping fails. Socket handling lives in the
/// listener; this type is transport-agnostic and unit-testable.
/// </summary>
public sealed class Hl7MessageProcessor
{
    private readonly IWebhookIngestionDispatcher _dispatcher;
    private readonly Hl7MllpOptions _options;
    private readonly ILogger<Hl7MessageProcessor> _logger;

    public Hl7MessageProcessor(
        IWebhookIngestionDispatcher dispatcher,
        IOptions<Hl7MllpOptions> options,
        ILogger<Hl7MessageProcessor>? logger = null)
    {
        _dispatcher = dispatcher;
        _options = options.Value;
        _logger = logger ?? NullLogger<Hl7MessageProcessor>.Instance;
    }

    /// <summary>Processes one HL7 v2 message and returns the HL7 ACK text to frame and send back.</summary>
    public async Task<string> ProcessAsync(string rawHl7, CancellationToken cancellationToken)
    {
        Hl7v2Message message;
        try
        {
            message = Hl7v2Parser.Parse(rawHl7);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to parse inbound HL7 v2 message.");
            // Without a parseable MSH we cannot echo control ids; return a minimal AE.
            return "MSH|^~\\&|FHIRBridge||||||ACK|ERR-ACK|P|2.5\rMSA|AE|unknown|" + Sanitize(exception.Message);
        }

        try
        {
            var bundleJson = Hl7v2ToFhirMapper.MapToFhirBundleJson(message);
            var payloadHash = ComputeHash(bundleJson);
            var messageId = $"hl7v2:{_options.WebhookConfigurationId:N}:{payloadHash}";

            await _dispatcher.EnqueueAsync(
                new WebhookIngestionCommand(
                    _options.WebhookConfigurationId,
                    bundleJson,
                    payloadHash,
                    "hl7v2-mllp",
                    message.MessageControlId,
                    messageId),
                cancellationToken);

            _logger.LogInformation(
                "Ingested HL7 v2 {MessageType}^{TriggerEvent} (control id {ControlId}).",
                message.MessageType, message.TriggerEvent, message.MessageControlId);

            return Hl7MllpProtocol.BuildAck(message, accepted: true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to ingest HL7 v2 message {ControlId}.", message.MessageControlId);
            return Hl7MllpProtocol.BuildAck(message, accepted: false, errorText: exception.Message);
        }
    }

    private static string ComputeHash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string Sanitize(string message)
        => message.ReplaceLineEndings(" ").Replace('|', ' ').Trim();
}
