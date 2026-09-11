using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Application.Services.Transforms;
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

    /// <summary>Every @token JsonMappingEngine actually recognizes — mirrors its IsSystemToken handling
    /// exactly (the "@default" literal-constant branch, plus ConfiguredPipelineService/TransformNodeExecutors'
    /// systemValues dictionary keys) so a save-time typo is rejected instead of silently mapping to null.</summary>
    private static readonly string[] KnownSystemTokens =
        ["@default", "@now", "@runId", "@resourceType", "@sourceResourceId"];

    private readonly IDestinationSchemaService _schemaService;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly IEffectiveRuleResolver _ruleResolver;

    public CreateMappingProfileRequestValidator(
        IDestinationSchemaService schemaService,
        IConfigurationRepository configurationRepository,
        IEffectiveRuleResolver ruleResolver)
    {
        _schemaService = schemaService;
        _configurationRepository = configurationRepository;
        _ruleResolver = ruleResolver;

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

            // A JsonPath starting with '@' is a reserved system/default token (JsonMappingEngine.IsSystemToken),
            // not a real "$…" source path — catch a typo'd token here rather than it silently resolving to
            // null on every pipeline run. '@default' additionally requires a literal DefaultValue to write
            // (see field-mapping-default-value-modal.component.ts) — with none, the column would always be
            // written null, which the "set default value" feature is never meant to produce.
            field.RuleFor(f => f.JsonPath)
                .Must(path => KnownSystemTokens.Contains(path))
                .WithMessage(f => $"'{f.JsonPath}' is not a recognized @token. Expected one of: {string.Join(", ", KnownSystemTokens)}.")
                .When(f => f.JsonPath.StartsWith('@'));

            field.RuleFor(f => f.DefaultValue)
                .NotEmpty()
                .WithMessage("A field set to the literal default value ('@default') must supply a DefaultValue to write.")
                .When(f => string.Equals(f.JsonPath, "@default", StringComparison.OrdinalIgnoreCase));

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

        // Needed to resolve applicable TransformationRules below — a rule can be scoped to a DestinationType
        // (e.g. "every SqlServer destination"), not just a specific field/resource type.
        var destination = await _configurationRepository.GetDestinationAsync(request.DestinationId, cancellationToken);
        var destinationType = destination?.DestinationType;

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

            var rules = destinationType is null
                ? (IReadOnlyList<Domain.Entities.TransformationRule>)[]
                : await _ruleResolver.ResolveAsync(
                    destinationType.Value,
                    request.ResourceType,
                    field.TargetField,
                    request.ResourcePipelineRouteId,
                    sourceSystem: null,
                    // Must be normalized the same way the UI persists it, or the SourceField equality below the
                    // resolver matches nothing and every rule keyed to a source path goes unseen here.
                    RuleSourceFieldFormat.FromJsonPath(request.ResourceType, field.JsonPath),
                    cancellationToken,
                    // Workflow-scoped rules the builder wrote before this pipeline was first saved are still
                    // unattached, and the mapping profile is saved BEFORE the workflow that would attach them —
                    // so without this the save gate can never see the rule the user just authored.
                    includePendingWorkflowRules: true);

            // A transformation rule chain (e.g. DateMathAge turning a Date into an Integer) can legitimately
            // change the value's shape between the raw JsonPath extraction and what actually reaches the
            // column — field.ValueType only describes the former, so comparing it against the column here
            // would reject a perfectly valid rule-backed mapping. Once a rule declares an output type,
            // ValidateApplicableRules below is the sole authority on whether that type fits the column.
            var ruleDeclaresOutputType = rules.Any(r => r.ExpectedValueType is not null);

            if (!ruleDeclaresOutputType &&
                !string.Equals(column.MappingValueType, field.ValueType.ToString(), StringComparison.OrdinalIgnoreCase) &&
                !IsJsonSafeForColumn(field.ValueType, column))
            {
                context.AddFailure(
                    $"Fields[{i}].ValueType",
                    $"'{column.Name}' is a {column.DataType} column (expects {column.MappingValueType}), " +
                    $"but this field is mapped as {field.ValueType}.");
            }

            // A pipeline/runtime @token (other than "@default", already covered by DefaultValue below) always
            // resolves to a real value at run time — see JsonMappingEngine's systemValues dictionary — so it
            // never risks a NULL write the way an ordinary unmapped/no-default field would.
            var isNonNullSystemToken = field.JsonPath.StartsWith('@') &&
                !string.Equals(field.JsonPath, "@default", StringComparison.OrdinalIgnoreCase);

            if (!column.IsNullable && !field.IsRequired && !isNonNullSystemToken && string.IsNullOrWhiteSpace(field.DefaultValue))
            {
                context.AddFailure(
                    $"Fields[{i}].IsRequired",
                    $"'{column.Name}' does not allow NULLs — mark this field as required or supply a default value.");
            }

            if (destinationType is not null)
            {
                ValidateApplicableRules(i, column, rules, context);
            }
        }
    }

    /// <summary>
    /// A field can pass the check above (its declared type matches the column) and still fail at run time,
    /// because a Global/ResourceType/DestinationType-scoped <see cref="Domain.Entities.TransformationRule"/>
    /// applies to it and expects a different type than the column actually is — e.g. a Global NumberCast rule
    /// hitting a text column.
    ///
    /// Only the LAST rule in the chain that declares an output type is compared against the column: the rules
    /// run in order, each one feeding the next, so the intermediate types are none of the column's business —
    /// a StringNormalization feeding a NumberCast into an int column is correct, and checking every rule would
    /// reject it for the String step alone.
    /// </summary>
    private static void ValidateApplicableRules(
        int fieldIndex,
        DestinationColumnSchemaDto column,
        IReadOnlyList<Domain.Entities.TransformationRule> rules,
        ValidationContext<CreateMappingProfileRequest> context)
    {
        var lastTypedRule = rules.LastOrDefault(r => r.ExpectedValueType is not null);
        foreach (var rule in rules)
        {
            if (rule == lastTypedRule &&
                rule.ExpectedValueType is { } expectedValueType &&
                !string.Equals(expectedValueType.ToString(), column.MappingValueType, StringComparison.OrdinalIgnoreCase) &&
                !IsJsonSafeForColumn(expectedValueType, column))
            {
                var failure = new FluentValidation.Results.ValidationFailure(
                    $"Fields[{fieldIndex}].TargetField",
                    $"A {rule.Scope} rule ({rule.NodeType}) expects '{column.Name}' to be {expectedValueType}, " +
                    $"but it's a {column.DataType} column ({column.MappingValueType}). Add a workflow-level " +
                    "override for this field, or update the rule's expected type.")
                {
                    CustomState = new TransformationRuleTypeConflict(
                        rule.Id, rule.Scope, rule.NodeType, rule.DestinationField, expectedValueType, column.MappingValueType),
                };
                context.AddFailure(failure);
            }

            CheckStructuredOutputFitsColumn(rule, column, fieldIndex, context);
        }
    }

    /// <summary>
    /// True when a Json-shaped value (<paramref name="effectiveType"/> is <see cref="MappingValueType.Json"/>
    /// — a childJson field, or a rule declaring Json output) is safe to write into <paramref name="column"/>
    /// even though its own <see cref="DestinationColumnSchemaDto.MappingValueType"/> is the generic "String"
    /// every character-string SQL type collapses to (see SqlDestinationSchemaService.MapSqlServerType/
    /// MapPostgresType/MapMySqlType — none of them ever produce "Json" for any real column, including a
    /// native Postgres jsonb) — an exact string-equality check would otherwise reject EVERY Json mapping
    /// onto EVERY destination, including one deliberately sized to hold it (e.g. SQL Server nvarchar(max)).
    ///
    /// Scoped narrowly to an UNBOUNDED string column (<see cref="DestinationColumnSchemaDto.MaxLength"/> is
    /// null) — the same signal <see cref="CheckStructuredOutputFitsColumn"/> just below already uses to mean
    /// "no truncation risk here" — OR a column whose DATA TYPE NAME is itself a fixed-capacity large-blob
    /// type: MySQL's TEXT/MEDIUMTEXT/LONGTEXT and SQL Server's legacy NTEXT never report a null MaxLength at
    /// all (unlike SQL Server nvarchar(max)'s -1, or PostgreSQL's genuinely NULL character_maximum_length for
    /// `text`) — MySQL in particular always reports a real, if enormous, number (65,535 / 16,777,215 /
    /// 4,294,967,296) for these, because capacity is fixed by the keyword itself, not an independently
    /// configurable length the way varchar(n) is. Recognized by name for exactly that reason, regardless of
    /// whatever MaxLength SqlDestinationSchemaService happens to report for them (this is what "the actual
    /// column metadata/type is handled correctly instead of assuming only one exact type name" means in
    /// practice — MaxLength alone is not a reliable cross-engine "unbounded" signal). Deliberately excludes
    /// MySQL's much smaller TINYTEXT (255 chars) — genuinely too small for arbitrary JSON, so that one (and
    /// any other length-BOUNDED string column: nvarchar(50), varchar(200), …) keeps failing exactly as
    /// before — a real truncation risk there, not a false positive.
    /// </summary>
    private static bool IsJsonSafeForColumn(MappingValueType effectiveType, DestinationColumnSchemaDto column)
    {
        if (effectiveType != MappingValueType.Json ||
            !string.Equals(column.MappingValueType, "String", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (column.MaxLength is null)
        {
            return true;
        }

        var family = column.DataType.Trim().ToLowerInvariant().Split('(')[0];
        return family is "text" or "mediumtext" or "longtext" or "ntext";
    }

    /// <summary>
    /// The type-category check above (String vs Integer vs ...) is blind to actual size — a rule whose
    /// declared <see cref="Domain.Entities.TransformationRule.ExpectedValueType"/> is String and a String
    /// column both say "String" even when the rule emits a serialized JSON object (a Coding, a full
    /// CodeableConcept) into a narrow column sized for a short plain value. That combination passes the
    /// category check and only fails at pipeline-run time as a SQL truncation error — surfaced here instead,
    /// for the two node types whose config can turn a short scalar output into a JSON object: ValueCodeMapping
    /// with emitCoding on, and CodeableConceptBuilder with outputShape "object" (its default). The threshold
    /// (200 chars) is a deliberately generous floor for "a single Coding or small CodeableConcept" — this is a
    /// heuristic warning, not an exact prediction of the rendered JSON's length.
    /// </summary>
    private static void CheckStructuredOutputFitsColumn(
        Domain.Entities.TransformationRule rule,
        DestinationColumnSchemaDto column,
        int fieldIndex,
        ValidationContext<CreateMappingProfileRequest> context)
    {
        const int MinSafeLengthForStructuredOutput = 200;

        if (!string.Equals(column.MappingValueType, "String", StringComparison.OrdinalIgnoreCase) ||
            column.MaxLength is null || column.MaxLength >= MinSafeLengthForStructuredOutput)
        {
            return;
        }

        var configJson = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(rule.ConfigJson) ?? [];

        var emitsStructuredOutput = rule.NodeType switch
        {
            TransformNodeType.ValueCodeMapping => configJson.TryGetValue("emitCoding", out var emitCoding) &&
                string.Equals(emitCoding, "true", StringComparison.OrdinalIgnoreCase),
            TransformNodeType.CodeableConceptBuilder => !configJson.TryGetValue("outputShape", out var shape) ||
                string.Equals(shape, "object", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

        if (!emitsStructuredOutput)
        {
            return;
        }

        context.AddFailure(new FluentValidation.Results.ValidationFailure(
            $"Fields[{fieldIndex}].TargetField",
            $"A {rule.Scope} rule ({rule.NodeType}) can emit a serialized JSON object (a Coding/CodeableConcept), " +
            $"but '{column.Name}' only allows {column.MaxLength} characters — this will truncate at run time for " +
            "any real code+system+display combination. Widen the column, switch the rule to a plain-value output " +
            "(e.g. turn off emitCoding / set outputShape to \"displayTextOnly\"), or add a workflow-level override.")
        {
            CustomState = new TransformationRuleTypeConflict(
                rule.Id, rule.Scope, rule.NodeType, rule.DestinationField,
                MappingValueType.String, column.MappingValueType),
        });
    }
}
