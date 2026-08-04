using FHIRBridge.Application.Abstractions.Terminology;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FHIRBridge.Infrastructure.Persistence;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>FHIR R4 terminology operations backed by the locally imported LOINC release.</summary>
[ApiController]
[Authorize]
[Route("fhir")]
public sealed class LoincTerminologyController : ControllerBase
{
    private const string LoincSystem = "http://loinc.org";
    private readonly ITerminologyLookupService _lookup;
    private readonly FHIRBridgeDbContext _db;

    public LoincTerminologyController(ITerminologyLookupService lookup, FHIRBridgeDbContext db) => (_lookup, _db) = (lookup, db);

    [HttpGet("CodeSystem/$lookup")]
    public async Task<IActionResult> Lookup([FromQuery] string system, [FromQuery] string code, [FromQuery] string? version, CancellationToken cancellationToken)
    {
        if (!IsLoinc(system)) return BadRequest(OperationOutcome("Only the LOINC code system is supported by this endpoint."));
        var concept = await _lookup.LookupAsync(system, code, cancellationToken);
        if (concept is null) return NotFound(OperationOutcome($"LOINC code '{code}' was not found or is inactive."));
        return Ok(Parameters(
            ("name", "name", "LOINC"),
            ("version", "version", concept.Version),
            ("display", "display", concept.Display),
            ("code", "code", concept.Code),
            ("system", "uri", LoincSystem)));
    }

    [HttpGet("CodeSystem/$validate-code")]
    public async Task<IActionResult> ValidateCode([FromQuery] string system, [FromQuery] string code, [FromQuery] string? display, [FromQuery] string? version, CancellationToken cancellationToken)
    {
        if (!IsLoinc(system)) return BadRequest(OperationOutcome("Only the LOINC code system is supported by this endpoint."));
        var concept = await _lookup.LookupAsync(system, code, cancellationToken);
        var displayMatches = concept is not null && (string.IsNullOrWhiteSpace(display) || string.Equals(display, concept.Display, StringComparison.OrdinalIgnoreCase));
        var message = concept is null ? $"LOINC code '{code}' was not found or is inactive."
            : displayMatches ? null : $"Display '{display}' does not match the active LOINC display '{concept.Display}'.";
        return Ok(Parameters(("result", "boolean", displayMatches), ("message", "string", message), ("display", "string", concept?.Display), ("code", "code", code), ("system", "uri", LoincSystem)));
    }

    [HttpGet("ValueSet/$expand")]
    public async Task<IActionResult> Expand([FromQuery] string? filter, [FromQuery] int offset = 0, [FromQuery] int count = 50, CancellationToken cancellationToken = default)
    {
        offset = Math.Max(0, offset); count = Math.Clamp(count, 1, 1000);
        var query = _db.LoincConcepts.AsNoTracking().Where(x => x.IsActive);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var term = filter.Trim();
            query = query.Where(x => x.Code.Contains(term) || (x.Display != null && x.Display.Contains(term)) || (x.LongCommonName != null && x.LongCommonName.Contains(term)));
        }
        var total = await query.CountAsync(cancellationToken);
        var concepts = await query.OrderBy(x => x.Code).Skip(offset).Take(count)
            .Select(x => new { system = LoincSystem, code = x.Code, display = x.Display ?? x.LongCommonName })
            .ToListAsync(cancellationToken);
        return Ok(new { resourceType = "ValueSet", url = "http://loinc.org", expansion = new { offset, total, contains = concepts } });
    }

    [HttpGet("ConceptMap/$translate")]
    public async Task<IActionResult> Translate([FromQuery] string system, [FromQuery] string code, [FromQuery] string targetSystem, CancellationToken cancellationToken)
    {
        var match = await _db.LoincConceptMaps.AsNoTracking().Where(x => x.SourceSystem == system && x.SourceCode == code && x.TargetSystem == targetSystem)
            .OrderByDescending(x => x.Version).Select(x => new { x.TargetSystem, x.TargetCode, x.Display, x.Equivalence }).FirstOrDefaultAsync(cancellationToken);
        if (match is null) return Ok(Parameters(("result", "boolean", false), ("message", "string", "No matching concept map was found.")));
        return Ok(new { resourceType = "Parameters", parameter = new object[] { new { name = "result", valueBoolean = true }, new { name = "match", part = new object[] { new { name = "equivalence", valueCode = match.Equivalence ?? "equivalent" }, new { name = "concept", valueCoding = new { system = match.TargetSystem, code = match.TargetCode, display = match.Display } } } } } });
    }

    private static bool IsLoinc(string? system) => string.Equals(system?.TrimEnd('/'), LoincSystem, StringComparison.OrdinalIgnoreCase)
        || string.Equals(system, "urn:oid:2.16.840.1.113883.6.1", StringComparison.OrdinalIgnoreCase);

    private static object Parameters(params (string Name, string Type, object? Value)[] values) => new
    {
        resourceType = "Parameters",
        parameter = values.Where(x => x.Value is not null).Select(x => new Dictionary<string, object?>
        {
            ["name"] = x.Name,
            [$"value{char.ToUpperInvariant(x.Type[0])}{x.Type[1..]}"] = x.Value,
        }),
    };

    private static object OperationOutcome(string diagnostics) => new
    {
        resourceType = "OperationOutcome",
        issue = new[] { new { severity = "error", code = "not-found", diagnostics } },
    };
}
