namespace FHIRBridge.SharedKernel.Enums;

/// <summary>
/// The SMART-on-FHIR application type a source connection is configured as. This axis composes the authorization
/// flow (grant, PKCE, redirect, launch context, scope prefix) independently of the vendor axis: one Epic connector
/// can be configured as any of these. Behaviour that varies by type is encapsulated in a matching source application
/// strategy resolved from a registry — never a switch on this enum (enforced by an architecture test), so a new type
/// is a new strategy plus one registration.
/// </summary>
/// <remarks>
/// Lives in the shared kernel because it is a concept of both the configuration domain (persisted on a source
/// connection) and the runtime domain (drives the access-token grant).
/// </remarks>
public enum ApplicationType
{
    /// <summary>SMART Backend Services — machine-to-machine, client_credentials + private_key_jwt, system/ scopes, no user.</summary>
    Backend = 0,

    /// <summary>Provider EHR launch — app launched from inside the EHR with an opaque launch token; authorization_code + PKCE, trusted-iss allow-list.</summary>
    EhrLaunch = 1,

    /// <summary>Provider standalone — a clinician launches the app directly; authorization_code + PKCE, user/ scopes, launch/patient picker.</summary>
    Standalone = 2,

    /// <summary>Patient / member standalone — the patient signs in with their own portal credentials; authorization_code + PKCE (public client), patient/ scopes.</summary>
    Patient = 3
}
