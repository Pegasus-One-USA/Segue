using System.Text.Json;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Exceptions;
using FHIRBridge.Domain.Entities;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services;

public sealed class DeIdentificationProfileService : IDeIdentificationProfileService
{
    private readonly IDeIdentificationProfileRepository _repository;
    private readonly IDeIdentificationService _deIdentificationService;

    public DeIdentificationProfileService(
        IDeIdentificationProfileRepository repository,
        IDeIdentificationService deIdentificationService)
    {
        _repository = repository;
        _deIdentificationService = deIdentificationService;
    }

    public async Task<List<DeIdentificationProfileDto>> ListAsync(CancellationToken cancellationToken)
    {
        var profiles = await _repository.ListAsync(cancellationToken);
        return profiles.Select(ToDto).ToList();
    }

    public async Task<DeIdentificationProfileDto> CreateAsync(
        CreateDeIdentificationProfileRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["name"] = ["Name is required."]
            });
        }

        var profile = new DeIdentificationProfile(request.Name.Trim(), request.Description);
        await _repository.AddAsync(profile, cancellationToken);
        return ToDto(profile);
    }

    public async Task<DeIdentificationPreviewResult> PreviewAsync(
        Guid profileId, DeIdentificationPreviewRequest request, CancellationToken cancellationToken)
    {
        var profile = await _repository.GetByIdAsync(profileId, cancellationToken)
            ?? throw new NotFoundException(nameof(DeIdentificationProfile), profileId);

        try
        {
            JsonDocument.Parse(request.SampleJson);
        }
        catch (JsonException exception)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["sampleJson"] = [$"Sample resource is not valid JSON: {exception.Message}"]
            });
        }

        var redacted = await _deIdentificationService.DeIdentifyAsync(
            new DeIdentificationRequest(request.ResourceType, null, request.SampleJson, [], profile.Id),
            cancellationToken);

        return new DeIdentificationPreviewResult(redacted);
    }

    private static DeIdentificationProfileDto ToDto(DeIdentificationProfile profile) =>
        new(profile.Id, profile.Name, profile.Description);
}
