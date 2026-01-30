using System.Buffers;
using System.Text;

namespace TinyProxy.Core;

/// <summary>
/// Text manipulation utilities.
/// Aligns with tinyproxy C's text.c functionality.
/// </summary>
public static class TextUtils
{
    /// <summary>
    /// Removes any newline or carriage-return characters from the end of a string.
    /// Aligns with tinyproxy C's chomp() function from text.c.
    /// </summary>
    /// <param name="buffer">The buffer to chomp</param>
    /// <param name="length">The current length of the buffer (not including null terminator)</param>
    /// <returns>The number of characters removed</returns>
    public static int Chomp(Span<byte> buffer, int length)
    {
        if (buffer.IsEmpty || length <= 0)
        {
            return 0;
        }

        int charsRemoved = 0;
        int pos = length - 1;

        while (pos >= 0)
        {
            byte b = buffer[pos];
            if (b == '\r' || b == '\n')
            {
                charsRemoved++;
                pos--;
            }
            else
            {
                break;
            }
        }

        return charsRemoved;
    }

    /// <summary>
    /// Chomps a string builder.
    /// </summary>
    public static void Chomp(StringBuilder sb)
    {
        if (sb == null || sb.Length == 0)
        {
            return;
        }

        while (sb.Length > 0)
        {
            char last = sb[sb.Length - 1];
            if (last == '\r' || last == '\n')
            {
                sb.Length--;
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// Copies a string to a destination buffer with null termination.
    /// Aligns with OpenBSD's strlcpy() function.
    /// </summary>
    /// <param name="dst">Destination buffer</param>
    /// <param name="src">Source string</param>
    /// <param name="size">Size of destination buffer</param>
    /// <returns>The total length of src (not including null terminator)</returns>
    public static int Strlcpy(Span<char> dst, ReadOnlySpan<char> src, int size)
    {
        if (size == 0)
        {
            return src.Length;
        }

        int copyLength = Math.Min(src.Length, size - 1);
        src.Slice(0, copyLength).CopyTo(dst.Slice(0, copyLength));
        dst[copyLength] = '\0';

        return src.Length;
    }

    /// <summary>
    /// Finds the first occurrence of a substring in a byte span, case-insensitive.
    /// </summary>
    public static int IndexOfIgnoreCase(ReadOnlySpan<byte> span, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return 0;
        }

        if (span.Length < value.Length)
        {
            return -1;
        }

        for (int i = 0; i <= span.Length - value.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < value.Length; j++)
            {
                if (!EqualsIgnoreCase(span[i + j], value[j]))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Compares two bytes case-insensitively.
    /// </summary>
    private static bool EqualsIgnoreCase(byte a, byte b)
    {
        if (a == b)
        {
            return true;
        }

        // Convert to uppercase for comparison
        byte upperA = ToUpper(a);
        byte upperB = ToUpper(b);

        return upperA == upperB;
    }

    /// <summary>
    /// Converts a byte to uppercase (ASCII only).
    /// </summary>
    private static byte ToUpper(byte b)
    {
        if (b >= 'a' && b <= 'z')
        {
            return (byte)(b - 32);
        }
        return b;
    }

    /// <summary>
    /// Trims whitespace from a byte span.
    /// </summary>
    public static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> span)
    {
        int start = 0;
        int end = span.Length;

        // Trim leading whitespace
        while (start < end && (span[start] == ' ' || span[start] == '\t'))
        {
            start++;
        }

        // Trim trailing whitespace
        while (end > start && (span[end - 1] == ' ' || span[end - 1] == '\t' ||
                               span[end - 1] == '\r' || span[end - 1] == '\n'))
        {
            end--;
        }

        return span.Slice(start, end - start);
    }
}
