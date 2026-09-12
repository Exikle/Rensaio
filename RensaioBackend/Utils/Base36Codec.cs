namespace RensaioBackend.Utils;

/// <summary>
/// Base36 codec used by MangaUpdates: the API identifies series by a decimal <c>series_id</c>
/// (a long), but the public site and third-party cross-links use the base36-encoded slug
/// (e.g. <c>njeqwry</c>). We store the base36 string in the DB/frontend and translate to the
/// decimal long before calling the API (and back when parsing API payloads).
/// Base36 alphabet: 0-9 a-z (lowercase, no separators).
/// </summary>
public static class Base36Codec
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>Encodes a non-negative long into its base36 string (lowercase).</summary>
    public static string Encode(long value)
    {
        if (value < 0)
            throw new ArgumentException("Base36 does not encode negative values");
        if (value == 0)
            return "0";
        var chars = new System.Text.StringBuilder();
        long v = value;
        while (v > 0)
        {
            chars.Insert(0, Alphabet[(int)(v % 36)]);
            v /= 36;
        }
        return chars.ToString();
    }

    /// <summary>
    /// Decodes a base36 string into a long. Returns -1 when the input is empty or invalid.
    /// Accepts lowercase/uppercase; ignores leading/trailing whitespace.
    /// </summary>
    public static long Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return -1;
        var s = value.Trim().ToLowerInvariant();
        if (s.Length == 0)
            return -1;
        long result = 0;
        foreach (var c in s)
        {
            int digit = Alphabet.IndexOf(c);
            if (digit < 0)
                return -1;
            result = result * 36 + digit;
        }
        return result;
    }

    /// <summary>True when the string is a valid base36 slug (non-empty, all chars in alphabet).</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return Decode(value) >= 0;
    }
}