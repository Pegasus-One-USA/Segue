namespace FHIRBridge.SharedKernel.Abstractions;

/// <summary>
/// Marks a property of an <see cref="IAuditableEntity"/> whose value must never be copied into the audit trail.
/// The snapshot keeps the property but replaces a non-null value with a placeholder, so the trail still shows
/// <em>that</em> it was set, rotated or cleared, by whom and when, without the material itself.
/// <para>Use for credentials whose change is itself worth auditing (a password set, an MFA secret enrolled).
/// <c>AuditLogs</c> is append-only and never purged, so anything written there is effectively permanent.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class AuditRedactAttribute : Attribute
{
}

/// <summary>
/// Marks a property of an <see cref="IAuditableEntity"/> that is left out of the audit snapshot entirely. A save that
/// changes nothing but ignored properties (and the Modified provenance stamps) writes no audit row at all.
/// <para>Use for session and login bookkeeping (refresh-token rotation, MFA challenge, last login), which is recorded
/// in the authentication log and would otherwise add a row to the audit trail on every sign-in.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class AuditIgnoreAttribute : Attribute
{
}
