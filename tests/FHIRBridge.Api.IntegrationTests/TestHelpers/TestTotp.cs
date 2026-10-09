using System.Security.Cryptography;

namespace FHIRBridge.Api.IntegrationTests.TestHelpers;

/// <summary>
/// Independent RFC 6238 code generator (HMAC-SHA1, 6 digits, 30 s step), so the tests do not depend on any member of
/// the production TotpService: they only need a code its validator also accepts for the same secret.
/// </summary>
public static class TestTotp
{
    public static string Code(string base32Secret, DateTimeOffset? at = null)
    {
        var timestep = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        var key = Base32Decode(base32Secret);
        var counterBytes = BitConverter.GetBytes(timestep);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

        var hash = HMACSHA1.HashData(key, counterBytes);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8)
                     | (hash[offset + 3] & 0xFF);

        return (binary % 1_000_000).ToString().PadLeft(6, '0');
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(input.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in input)
        {
            var value = alphabet.IndexOf(c);
            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }

        return output.ToArray();
    }
}
