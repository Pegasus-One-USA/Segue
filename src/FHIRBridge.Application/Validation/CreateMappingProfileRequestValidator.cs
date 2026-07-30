using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Exceptions;
using FluentValidation;

namespace FHIRBridge.Application.Validation;

/// <summary>
/// Server-side mirror of the mapping-related checks the Angular destination wizard already enforces
/// (required Name/ResourceType/DestinationObject/field shape matching <c>MappingProfileConfiguration</c>'s
/// EF column lengths) plus the one check that today only exists client-side: <c>workflow-build-assembler
/// .service.ts</c>'s <c>buildMappingSpec()</c> refuses to save an upsert-mode destination with no field
/// mapped as the upsert key. A direct API call bypassing the wizard must be rejected the same way.
///
/// Also cross-checks each mapped field against the destination's real column metadata (already read by
/// <see cref="IDestinationSchemaService"/> for the mapping-UI column picker) so a mismatch is rejected here
/// rather than surfacing later as a raw <c>SqlException</c> mid pipeline-run (truncation, NOT NULL, identity/
/// computed-column, or type-mismatch failures).
/// </summary>
public sealed class CreateMappingProfileRequestValidator : AbstractValidator<CreateMappingProfileRequest>
{
    private const string UpsertModeSuffix = ";mode=upsert";

    private readonly IDestinationSchemaService _schemaService;

    public CreateMappingProfileRequestValidator(IDestinationSchemaService schemaService)
    {
        _schemaService = schemaService;

        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ResourceType).NotEmpty().MaximumLength(100);
        RuleFor(x => x.DestinationObject).NotEmpty().MaximumLength(300);

        RuleFor(x => x.Fields)
            .NotEmpty()
            .WithMessage("At least one field mapping is required.");

        RuleForEach(x => x.Fields).ChildRules(field =>
        {
            field.RuleFor(f => f.TargetField).NotEmpty().MaximumLength(200);
            field.RuleFor(f => f.JsonPath).NotEmpty().MaximumLength(500);

            // CorrelateByCode (e.g. picking an identifier[]/coding[] entry by its sibling "system" value, or an
            // Observation.component[] by its LOINC code) is meaningless without both the sibling JsonPath and the
            // value to match — JsonMappingEngine already rejects this at run time, but a save-time check surfaces
            // the mistake immediately instead of only on the next pipeline run.
            field.RuleFor(f => f.CorrelationCodeJsonPath)
                .NotEmpty()
                .WithMessage("CorrelateByCode requires correlationCodeJsonPath (the sibling element to match, e.g. the array's \"system\" or \"code\" field).")
                .When(f => f.ArrayPolicy == ArrayPolicy.CorrelateByCode);

            field.RuleFor(f => f.CorrelationCodeValue)
                .NotEmpty()
                .WithMessage("CorrelateByCode requires correlationCodeValue (the value the sibling element must equal to select this array item).")
                .When(f => f.ArrayPolicy == ArrayPolicy.CorrelateByCode);
        });

        RuleFor(x => x.Fields)
            .Must(HaveAnUpsertKeyField)
            .When(x => x.DestinationObject.Contains(UpsertModeSuffix, StringComparison.OrdinalIgnoreCase))
            .WithMessage(
                "This destination is set to upsert, but no field is mapped as the upsert key. " +
                "Map a field from the resource's id and mark it as the upsert key.");

        RuleFor(x => x)
            .CustomAsync(ValidateAgainstDestinationSchemaAsync);
    }

    private static bool HaveAnUpsertKeyField(IReadOnlyList<MappingFieldDto> fields) =>
        fields.Any(f => f.IsUpsertKey);

    /// <summary>
    /// Cross-checks each mapped field against the destination's live column metadata. Silently skips when the
    /// destination can't be introspected (non-relational destination, unreachable, or not found) — those cases
    /// are either not applicable or are already rejected by other rules/paths.
    /// </summary>
    private async Task ValidateAgainstDestinationSchemaAsync(
        CreateMappingProfileRequest request,
        ValidationContext<CreateMappingProfileRequest> context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DestinationObject))
        {
            return;
        }

        DestinationSchemaDto schema;
        try
        {
            schema = await _schemaService.GetSchemaAsync(request.DestinationId, cancellationToken);
        }
        catch (NotFoundException)
        {
            return;
        }
        catch (Exception)
        {
            // Destination unreachable at save time (e.g. temporary network blip) — don't block the save on it;
            // the pipeline-run write path is still the backstop for genuinely bad mappings.
            return;
        }

        if (schema.Tables.Count == 0)
        {
            return;
        }

        var tableName = DestinationObjectParser.ParseTableName(request.DestinationObject);
        var table = schema.Tables.FirstOrDefault(t =>
            string.Equals(t.FullName, tableName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.TableName, tableName, StringComparison.OrdinalIgnoreCase));

        if (table is null)
        {
            // Table not found among introspected tables isn't necessarily an error (e.g. a schema the introspection
            // query excludes) — leave table-existence enforcement to the writer's own EnsureTableAsync check.
            return;
        }

        var columnsByName = table.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < request.Fields.Count; i++)
        {
            var field = request.Fields[i];

            // A field explicitly scoped to a different destination object (e.g. a SeparateDestination child table)
            // is validated against its own table elsewhere — out of scope for this per-request schema check.
            if (!string.IsNullOrWhiteSpace(field.DestinationObject) &&
                !string.Equals(field.DestinationObject, request.DestinationObject, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!columnsByName.TryGetValue(field.TargetField, out var column))
            {
                context.AddFailure(
                    $"Fields[{i}].TargetField",
                    $"Column '{field.TargetField}' does not exist on '{table.FullName}'.");
                continue;
            }

            if (column.IsAutoGenerated)
            {
                context.AddFailure(
                    $"Fields[{i}].TargetField",
                    $"'{column.Name}' is an identity or computed column in '{table.FullName}' and cannot be a mapping write target.");
                continue;
            }

            if (!string.Equals(column.MappingValueType, field.ValueType.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                context.AddFailure(
                    $"Fields[{i}].ValueType",
                    $"'{column.Name}' is a {column.DataType} column (expects {column.MappingValueType}), " +
                    $"but this field is mapped as {field.ValueType}.");
            }

            if (!column.IsNullable && !field.IsRequired && string.IsNullOrWhiteSpace(field.DefaultValue))
            {
                context.AddFailure(
                    $"Fields[{i}].IsRequired",
                    $"'{column.Name}' does not allow NULLs — mark this field as required or supply a default value.");
            }
        }
    }

}
