using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// A CSV uploaded for a Tabular source node. The rows are PHI, so the content is held only encrypted
/// (<see cref="EncryptedContent"/>, AES-GCM under the application's PHI key); the other columns describe the file
/// without revealing it. Deleted for real, not soft-deleted, so removing a file removes the data. Not audited:
/// the change audit trail would snapshot the ciphertext.
/// </summary>
public sealed class TabularSourceFile : Entity<Guid>
{
    private TabularSourceFile()
    {
    }

    public TabularSourceFile(
        string fileName,
        string encryptedContent,
        string contentSha256,
        int sizeBytes,
        int rowCount,
        string columnsJson,
        string? createdBy,
        DateTime createdOnUtc)
    {
        Id = Guid.NewGuid();
        FileName = fileName.Length > 260 ? fileName[..260] : fileName;
        EncryptedContent = encryptedContent;
        ContentSha256 = contentSha256;
        SizeBytes = sizeBytes;
        RowCount = rowCount;
        ColumnsJson = columnsJson;
        CreatedBy = createdBy is { Length: > 256 } ? createdBy[..256] : createdBy;
        CreatedOnUtc = createdOnUtc;
    }

    public string FileName { get; private set; } = default!;

    public string EncryptedContent { get; private set; } = default!;

    /// <summary>SHA-256 (uppercase hex) of the plaintext, so a re-upload of the same file can be recognised.</summary>
    public string ContentSha256 { get; private set; } = default!;

    public int SizeBytes { get; private set; }

    public int RowCount { get; private set; }

    /// <summary>The header's column names as a JSON array.</summary>
    public string ColumnsJson { get; private set; } = default!;

    public string? CreatedBy { get; private set; }

    public DateTime CreatedOnUtc { get; private set; }
}
