using System.Security.Cryptography;
using System.Text;
using AccessControl.Abstractions;

namespace Knight.Infrastructure.ControlPlane.Security;

/// <summary>
/// RFC 6238 time-based one-time passwords, with the parameters every common
/// authenticator app assumes: HMAC-SHA1, six digits, a thirty-second step.
/// Implemented directly rather than pulled in as a dependency — the algorithm is
/// twenty lines, and the second factor for platform administrators is not a
/// place to inherit an unaudited package.
/// </summary>
public sealed class TotpService : ITotpService
{
    private const int SecretSizeBytes = 20;
    private const int Digits = 6;
    private const int StepSeconds = 30;

    /// <summary>
    /// How many steps either side of "now" are accepted. One step covers the
    /// ordinary case of a user typing a code as it rolls over, plus modest clock
    /// drift on their device, without widening the window a guesser gets.
    /// </summary>
    private const int AllowedDrift = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public string GenerateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(SecretSizeBytes));

    public bool Verify(string secret, string code, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var candidate = code.Trim();
        if (candidate.Length != Digits || !candidate.All(char.IsAsciiDigit))
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

        var currentStep = now.ToUnixTimeSeconds() / StepSeconds;

        for (var offset = -AllowedDrift; offset <= AllowedDrift; offset++)
        {
            var expected = Compute(key, currentStep + offset);

            // Fixed-time comparison: a timing difference between "first digit
            // wrong" and "last digit wrong" would leak the code one digit at a time.
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected),
                    Encoding.ASCII.GetBytes(candidate)))
            {
                return true;
            }
        }

        return false;
    }

    public string BuildEnrollmentUri(string secret, string accountEmail, string issuer)
    {
        var encodedIssuer = Uri.EscapeDataString(issuer);
        var label = Uri.EscapeDataString($"{issuer}:{accountEmail}");

        return $"otpauth://totp/{label}?secret={secret}&issuer={encodedIssuer}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    private static string Compute(byte[] key, long step)
    {
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        var hash = HMACSHA1.HashData(key, counter);

        // Dynamic truncation, RFC 4226 section 5.3.
        var offset = hash[^1] & 0x0F;
        var binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        return (binary % (int)Math.Pow(10, Digits)).ToString(new string('0', Digits));
    }

    private static string Base32Encode(byte[] data)
    {
        var builder = new StringBuilder((data.Length + 4) / 5 * 8);
        for (var i = 0; i < data.Length; i += 5)
        {
            var chunk = new byte[5];
            var length = Math.Min(5, data.Length - i);
            Array.Copy(data, i, chunk, 0, length);

            var bits = 0UL;
            for (var b = 0; b < 5; b++)
            {
                bits = (bits << 8) | chunk[b];
            }

            var characters = length switch { 1 => 2, 2 => 4, 3 => 5, 4 => 7, _ => 8 };
            for (var c = 0; c < 8; c++)
            {
                var index = (int)((bits >> (35 - (c * 5))) & 0x1F);
                builder.Append(c < characters ? Base32Alphabet[index] : '=');
            }
        }

        return builder.ToString();
    }

    private static byte[] Base32Decode(string value)
    {
        var trimmed = value.TrimEnd('=').ToUpperInvariant();
        var bits = 0;
        var accumulator = 0;
        var output = new List<byte>(trimmed.Length * 5 / 8);

        foreach (var character in trimmed)
        {
            var index = Base32Alphabet.IndexOf(character);
            if (index < 0)
            {
                throw new FormatException($"'{character}' is not a base32 character.");
            }

            accumulator = (accumulator << 5) | index;
            bits += 5;

            if (bits < 8)
            {
                continue;
            }

            bits -= 8;
            output.Add((byte)((accumulator >> bits) & 0xFF));
        }

        return [.. output];
    }
}
