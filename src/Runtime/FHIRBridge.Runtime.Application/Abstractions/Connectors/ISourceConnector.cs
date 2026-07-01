namespace FHIRBridge.Runtime.Application.Abstractions.Connectors;

/// <summary>
/// Root abstraction for any data source FHIRBridge can pull from — FHIR REST servers today; HL7v2, flat-file,
/// and database sources in future. Specialized contracts such as <see cref="IFhirSourceClient"/> layer the read
/// protocol on top.
/// <para>
/// This is the "vendor" (inheritance) axis of the Bridge model: <c>ISourceConnector → IFhirSourceClient →
/// EpicFhirSourceClient</c>. The orthogonal application-type axis (Backend, EHR-launch, Standalone, Patient) is
/// <em>composed</em> via the access-token-provider strategy, never subclassed — that keeps the vendor × app-type
/// space from exploding combinatorially.
/// </para>
/// </summary>
public interface ISourceConnector
{
    /// <summary>The transport/protocol family this connector speaks.</summary>
    SourceConnectorKind Kind { get; }
}

/// <summary>The protocol family a <see cref="ISourceConnector"/> implements.</summary>
public enum SourceConnectorKind
{
    FhirRest = 0,
    Hl7v2 = 1,
    FlatFile = 2,
    Database = 3
}
