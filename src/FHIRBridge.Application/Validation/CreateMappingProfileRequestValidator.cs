using FHIRBridge.Application.DTOs;
using FluentValidation;

namespace FHIRBridge.Application.Validation;

/// <summary>
/// Server-side mirror of the mapping-related checks the Angular destination wizard already enforces
/// (required Name/ResourceType/DestinationObject/field shape matching <c>MappingProfileConfiguration</c>'s
/// EF column lengths) plus the one check that today only exists client-side: <c>workflow-build-assembler
/// .service.ts</c>'s <c>buildMappingSpec()</c> refuses to save an upsert-mode destination with no field
/// mapped as the upsert key. A direct API call bypassing the wizard must be rejected the same way.
/// </summary>
public sealed class CreateMappingProfileRequestValidator : AbstractValidator<CreateMappingProfileRequest>
{
    private const string UpsertModeSuffix = ";mode=upsert";

    public CreateMappingProfileRequestValidator()
    {
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
        });

        RuleFor(x => x.Fields)
            .Must(HaveAnUpsertKeyField)
            .When(x => x.DestinationObject.Contains(UpsertModeSuffix, StringComparison.OrdinalIgnoreCase))
            .WithMessage(
                "This destination is set to upsert, but no field is mapped as the upsert key. " +
                "Map a field from the resource's id and mark it as the upsert key.");
    }

    private static bool HaveAnUpsertKeyField(IReadOnlyList<MappingFieldDto> fields) =>
        fields.Any(f => f.IsUpsertKey);
}
