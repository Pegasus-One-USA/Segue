using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Security;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// RFC 6238 TOTP implementation (HMAC-SHA1, 6 digits, 30-second period) with no external
/// dependencies. Codes are validated across a ±1 step window to absorb client clock drift.
/// </summary>
public sealed class TotpService : ITotpService
{
    private const int Digits = 6;
    private const int PeriodSeconds = 30;
    private const int SecretBytes = 20; // 160-bit secret, per RFC 4226 recommendation.

    public string GenerateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    public string BuildProvisioningUri(string secret, string accountName, string issuer)
    {
        var label = Uri.EscapeDataString($"{issuer}:{accountName}");
        var issuerEncoded = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}?secret={secret}&issuer={issuerEncoded}" +
               $"&digits={Digits}&period={PeriodSeconds}&algorithm=SHA1";
    }

    public bool ValidateCode(string secret, string code)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        code = code.Trim();
        if (code.Length != Digits || !code.All(char.IsDigit))
        {
            return false;
        }

        byte[] key;
        try
        {
            key = Base32Decode(secret);
        }
        catch (FormatException)
        {
            return false;
        }

        var timestep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / PeriodSeconds;
        for (var offset = -1; offset <= 1; offset++)
        {
            var candidate = ComputeCode(key, timestep + offset);
            // Constant-time compare to avoid leaking timing information about the expected code.
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(candidate),
                    System.Text.Encoding.ASCII.GetBytes(code)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ComputeCode(byte[] key, long counter)
    {
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counterBytes);
        }

        var hash = HMACSHA1.HashData(key, counterBytes);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8)
                     | (hash[offset + 3] & 0xFF);

        var otp = binary % (int)Math.Pow(10, Digits);
        return otp.ToString().PadLeft(Digits, '0');
    }

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string Base32Encode(byte[] data)
    {
        var result = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                result.Append(Base32Alphabet[(buffer >> bitsLeft) & 31]);
            }
        }

        if (bitsLeft > 0)
        {
            result.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        }

        return result.ToString();
    }

    private static byte[] Base32Decode(string input)
    {
        input = input.TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(input.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in input)
        {
            var value = Base32Alphabet.IndexOf(c);
            if (value < 0)
            {
                throw new FormatException($"Invalid Base32 character '{c}'.");
            }

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
