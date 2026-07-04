using System.Security.Cryptography;
using System.Text;

namespace FHIRBridge.Application.Security;

/// <summary>
/// Produces stable, name-based GUIDs (RFC 4122 §4.3 UUID version 5, SHA-1) so identifiers that must stay
/// consistent across every environment — dev, CI, staging, prod, each seeded from an empty database
/// independently — never need a hand-picked literal. The same (namespace, name) pair always yields the same
/// GUID; a different name always yields a different one.
/// </summary>
public static class DeterministicGuid
{
    public static Guid Create(Guid namespaceId, string name)
    {
        Span<byte> namespaceBytes = stackalloc byte[16];
        namespaceId.TryWriteBytes(namespaceBytes);
        SwapByteOrder(namespaceBytes);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var data = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(data);
        nameBytes.CopyTo(data.AsSpan(namespaceBytes.Length));

        var hash = SHA1.HashData(data);
        var result = new byte[16];
        Array.Copy(hash, result, 16);

        result[6] = (byte)((result[6] & 0x0F) | (5 << 4)); // version 5 (name-based, SHA-1)
        result[8] = (byte)((result[8] & 0x3F) | 0x80);     // variant RFC 4122

        SwapByteOrder(result);
        return new Guid(result);
    }

    // Guid's in-memory layout stores the first three fields (time-low, time-mid, time-high-and-version) as
    // little-endian, while RFC 4122 defines the byte stream in network (big-endian) order. Swap those three
    // fields' byte order both when reading the namespace in and when constructing the final Guid, or the
    // result won't match other UUID v5 implementations for the same inputs.
    private static void SwapByteOrder(Span<byte> guidBytes)
    {
        (guidBytes[0], guidBytes[3]) = (guidBytes[3], guidBytes[0]);
        (guidBytes[1], guidBytes[2]) = (guidBytes[2], guidBytes[1]);
        (guidBytes[4], guidBytes[5]) = (guidBytes[5], guidBytes[4]);
        (guidBytes[6], guidBytes[7]) = (guidBytes[7], guidBytes[6]);
    }
}
