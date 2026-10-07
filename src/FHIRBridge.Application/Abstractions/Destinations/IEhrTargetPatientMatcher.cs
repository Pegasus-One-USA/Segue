using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// The master patient index (MPI) EHR write-back asks for a source patient's record in a target EHR that has no
/// certain-only <c>Patient/$match</c> of its own (eClinicalWorks, athenahealth). Consulted only after the ledger and an
/// exact identifier search found no one, and never in clone mode.
///
/// <para><b>Contract.</b> A wrong-patient write is a patient-safety incident, so an implementation answers
/// <see cref="EhrPatientMatchKind.Certain"/> only for one patient it is sure of, with that patient's id in the TARGET
/// EHR (its FHIR id, or the vendor's own patient id). <see cref="EhrPatientMatchKind.None"/> means the index knows the
/// person has no record there, which is what lets a destination that opted in create one.
/// <see cref="EhrPatientMatchKind.Ambiguous"/> sends the records to manual review; <see cref="EhrPatientMatchKind.Failed"/>
/// skips them for this run. Returning null means no index is available, and the records wait as
/// <c>patient-awaiting-mpi</c>.</para>
///
/// <para>The default registration (<c>UnavailableEhrTargetPatientMatcher</c>) always returns null; the MPI replaces
/// it in DI.</para>
/// </summary>
public interface IEhrTargetPatientMatcher
{
    Task<EhrPatientMatchOutcome?> MatchAsync(EhrTargetPatientMatchRequest request, CancellationToken cancellationToken);
}

/// <param name="TargetConnectionId">The write connection, which identifies the target EHR instance.</param>
/// <param name="TargetBaseUrl">The target EHR's FHIR base URL.</param>
/// <param name="SourceBaseUrl">The FHIR server the source patient was read from.</param>
/// <param name="SourcePatientId">The patient's id in the source system.</param>
/// <param name="SourcePatientJson">The source Patient resource as read: PHI, held in memory only, never logged.</param>
public sealed record EhrTargetPatientMatchRequest(
    Guid TargetConnectionId,
    SourceSystemType TargetVendor,
    string TargetBaseUrl,
    string? SourceBaseUrl,
    string SourcePatientId,
    string SourcePatientJson);
