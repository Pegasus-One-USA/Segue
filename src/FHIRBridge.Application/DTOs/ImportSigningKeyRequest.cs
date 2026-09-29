using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

/// <summary>Raw PEM text of a customer-supplied RSA private key, read client-side from an uploaded file, plus the
/// vendor the wizard is configuring — used only to name the stored secret readably (e.g.
/// "athenahealth-private-key-..."), not to change how the key itself is validated or stored. Nullable, not a plain
/// SourceSystemType: SourceSystemType.Sample is 0, so an omitted property would otherwise silently deserialize to
/// Sample instead of the controller being able to tell it was never sent, and reject the request.</summary>
public sealed record ImportSigningKeyRequest(string PrivateKeyPem, SourceSystemType? SourceSystemType);
