using FHIRBridge.Runtime.Domain.Fhir;
using FluentValidation;

namespace FHIRBridge.Runtime.Application.Pipeline.Commands;

public sealed class StartPipelineRunCommandValidator : AbstractValidator<StartPipelineRunCommand>
{
    public StartPipelineRunCommandValidator()
    {
        RuleFor(x => x.Request.Source).NotNull().WithMessage("A source is required.");
        RuleFor(x => x.Request.Destination).NotNull().WithMessage("A destination is required.");
        RuleFor(x => x.Request.ResourceTypes)
            .NotEmpty()
            .Must(resourceTypes => resourceTypes.Any(SupportedFhirResourceTypes.IsSupported))
            .WithMessage("At least one supported FHIR resource type is required.");

        RuleForEach(x => x.Request.ResourceTypes)
            .Must(SupportedFhirResourceTypes.IsSupported)
            .WithMessage("FHIR resource type '{PropertyValue}' is not supported.");

        When(x => x.Request.Source.SourceType == Domain.Enums.RuntimeSourceType.Epic, () =>
        {
            RuleFor(x => x.Request.Source.BaseUrl).NotEmpty().WithMessage("The source's FHIR base URL is required.");
            RuleFor(x => x.Request.Source.TokenEndpoint).NotEmpty().WithMessage("The source's token endpoint is required.");
            RuleFor(x => x.Request.Source.ClientId).NotEmpty().WithMessage("The source's client ID is required.");
            RuleFor(x => x.Request.Source.PrivateKeyPem).NotEmpty().WithMessage("The source's private key is required.");
        });

        When(x => x.Request.Destination.DestinationType == Domain.Enums.RuntimeDestinationType.SqlServer, () =>
        {
            RuleFor(x => x.Request.Destination.ConnectionString).NotEmpty().WithMessage("The destination's connection string is required.");
        });
    }
}
