namespace UrlShortener.Core;

/// <summary>
/// High-performance, zero-allocation Base62 encoder and decoder using Span<char>.
/// Uses character set [0-9a-zA-Z] (62 distinct characters).
/// 62^7 = ~3.52 Trillion distinct keys with 7 characters.
/// </summary>
public static class Base62
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private static readonly int Base = Alphabet.Length;

    /// <summary>
    /// Encodes a positive 64-bit integer into a Base62 string.
    /// Padded to minimum length (default 7 characters).
    /// </summary>
    public static string Encode(long value, int minLength = 7)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Value must be non-negative.");

        if (value == 0)
            return new string('0', minLength);

        Span<char> buffer = stackalloc char[12]; // Long max fits in 11 Base62 characters
        int index = buffer.Length;

        while (value > 0)
        {
            buffer[--index] = Alphabet[(int)(value % Base)];
            value /= Base;
        }

        // Pad left with '0' to satisfy minLength
        int encodedLength = buffer.Length - index;
        if (encodedLength < minLength)
        {
            int padCount = minLength - encodedLength;
            for (int i = 0; i < padCount; i++)
            {
                buffer[--index] = Alphabet[0];
            }
        }

        return new string(buffer.Slice(index));
    }

    /// <summary>
    /// Decodes a Base62 string back into a 64-bit integer.
    /// </summary>
    public static long Decode(ReadOnlySpan<char> input)
    {
        long result = 0;
        foreach (char c in input)
        {
            int val = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'z' => c - 'a' + 10,
                >= 'A' and <= 'Z' => c - 'A' + 36,
                _ => throw new FormatException($"Invalid Base62 character: {c}")
            };

            result = checked(result * Base + val);
        }
        return result;
    }
}
