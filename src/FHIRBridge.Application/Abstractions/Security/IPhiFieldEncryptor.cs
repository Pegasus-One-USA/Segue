namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Encrypts/decrypts individual PHI-bearing text fields at rest (e.g. raw fetched FHIR JSON, mapped field
/// values) — applied as an EF Core value converter on the affected columns. Distinct from
/// <see cref="ISecretProvider"/>, which resolves configuration secrets rather than encrypting row data.
/// </summary>
public interface IPhiFieldEncryptor
{
    string Encrypt(string plaintext);

    string Decrypt(string ciphertext);
}
