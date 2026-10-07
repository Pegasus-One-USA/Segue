using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack;

/// <summary>
/// A vendor's write profile that shapes a resource the way another, verified profile does. Used where a vendor's
/// write API takes the same US Core subset an existing profile already builds, so the shaping rules live in one
/// place. A vendor whose API turns out to differ gets its own profile instead; nothing else changes.
/// </summary>
public abstract class DelegatingEhrWriteProfile : IEhrWriteProfile
{
    private readonly IEhrWriteProfile _shapeLike;

    protected DelegatingEhrWriteProfile(SourceSystemType vendor, IEhrWriteProfile shapeLike)
    {
        Vendor = vendor;
        _shapeLike = shapeLike;
    }

    public SourceSystemType Vendor { get; }

    public string ResourceType => _shapeLike.ResourceType;

    public virtual string? Variant => _shapeLike.Variant;

    public virtual EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options) => _shapeLike.Shape(source, options);

    public virtual void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId) =>
        _shapeLike.BindReferences(shaped, targetPatientId, targetEncounterId);
}
