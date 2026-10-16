using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Application.Services.Cds;
using FHIRBridge.Governance;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Cds.Coverage;

/// <summary>
/// "Test relay" on a payer: sends a synthetic order-sign request (made-up ids, a made-up MRI order and Coverage, no
/// fhirServer and no token, whatever the payer's forwarding setting) through the real relay client, so the admin sees
/// whether the payer accepts Segue's signed call. Audited; never throws for a remote failure. Also works with the
/// relay switched off, so it can be checked before go-live.
/// </summary>
public sealed class CdsPayerRelayTestService : ICdsPayerRelayTestService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ICdsPayerRepository _payers;
    private readonly ICdsPayerRelayClient _relay;
    private readonly IGovernanceLogger _governanceLogger;
    private readonly ILogger<CdsPayerRelayTestService> _logger;

    public CdsPayerRelayTestService(
        ICdsPayerRepository payers,
        ICdsPayerRelayClient relay,
        IGovernanceLogger governanceLogger,
        ILogger<CdsPayerRelayTestService> logger)
    {
        _payers = payers;
        _relay = relay;
        _governanceLogger = governanceLogger;
        _logger = logger;
    }

    public async Task<CdsPayerRelayTestResultDto> TestAsync(Guid payerId, CancellationToken cancellationToken)
    {
        var payer = await _payers.GetByIdAsync(payerId, cancellationToken) ?? throw new NotFoundException("CdsPayer", payerId);
        CdsPayerRelayTestResultDto result;
        if (payer.RelayServiceUrl is null)
        {
            result = new CdsPayerRelayTestResultDto(payer.Id, null, false, null, 0, 0, 0, "no relay URL",
                "Enter the payer's CRD service URL first.");
        }
        else
        {
            var relayed = await _relay.SendAsync(payer, SyntheticRequest(payer.Name), Timeout, cancellationToken);
            var cards = relayed.Response?.Cards.Count ?? 0;
            var actions = relayed.Response?.SystemActions?.Count ?? 0;
            var message = relayed.Succeeded
                ? $"The payer answered in {relayed.DurationMs} ms with {cards} card(s) and {actions} systemAction(s)."
                : $"The payer's CRD service could not be used: {relayed.Outcome}.";
            if (!payer.RelayEnabled)
            {
                message += " The relay is switched off for this payer, so live calls are answered from local rules.";
            }

            result = new CdsPayerRelayTestResultDto(
                payer.Id, payer.RelayServiceUrl, relayed.Succeeded, relayed.HttpStatus, relayed.DurationMs, cards, actions, relayed.Outcome, message);
        }

        try
        {
            await _governanceLogger.LogAuditAsync(
                new AuditEntry(
                    Module: "CdsHooks",
                    Action: "TestRelay",
                    EntityType: "CdsPayer",
                    EntityId: payer.Id.ToString(),
                    EntityName: payer.Name,
                    Remarks: $"Test relay to '{payer.Name}': {result.Outcome} ({result.HttpStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}), {result.DurationMs} ms."),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError("Could not audit the CDS test relay of payer {PayerId}: {ErrorType}.", payer.Id, exception.GetType().Name);
        }

        return result;
    }

    /// <summary>A made-up order-sign request: no real patient, no fhirServer, no token.</summary>
    public static CdsRequest SyntheticRequest(string payerName)
    {
        var order = new JsonObject
        {
            ["resourceType"] = "ServiceRequest",
            ["id"] = "segue-test-order",
            ["status"] = "draft",
            ["intent"] = "order",
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject
                {
                    ["system"] = "http://www.ama-assn.org/go/cpt",
                    ["code"] = "70553",
                    ["display"] = "MRI brain without and with contrast",
                }),
            },
            ["subject"] = new JsonObject { ["reference"] = "Patient/segue-test-patient" },
        };
        var coverage = new JsonObject
        {
            ["resourceType"] = "Coverage",
            ["id"] = "segue-test-coverage",
            ["status"] = "active",
            ["beneficiary"] = new JsonObject { ["reference"] = "Patient/segue-test-patient" },
            ["payor"] = new JsonArray(new JsonObject { ["display"] = payerName }),
        };
        return new CdsRequest
        {
            Hook = CdsHookNames.OrderSign,
            HookInstance = Guid.NewGuid().ToString(),
            Context = new JsonObject
            {
                ["userId"] = "Practitioner/segue-test-practitioner",
                ["patientId"] = "segue-test-patient",
                ["draftOrders"] = new JsonObject
                {
                    ["resourceType"] = "Bundle",
                    ["type"] = "collection",
                    ["entry"] = new JsonArray(new JsonObject { ["resource"] = order }),
                },
            },
            Prefetch = new JsonObject
            {
                ["coverage"] = new JsonObject
                {
                    ["resourceType"] = "Bundle",
                    ["type"] = "searchset",
                    ["entry"] = new JsonArray(new JsonObject { ["resource"] = coverage }),
                },
            },
        };
    }
}
