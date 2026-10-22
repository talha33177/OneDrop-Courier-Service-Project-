namespace Domain.Parcels;

/// <summary>
/// Reads what a scanner sent as the code on the label. A hand scanner types the code, but may add a symbology prefix
/// (<c>]Q1</c> for a QR code), control characters, or spaces, and a phone's camera or a label made by another system may
/// hold the tracking link instead of the bare code. The answer is the code in capitals, or null when nothing is left.
/// </summary>
public static class ScanCode
{
    private const int MaxLength = 200;

    public static string? Read(string? scanned)
    {
        if (string.IsNullOrWhiteSpace(scanned))
        {
            return null;
        }

        var text = new string([.. scanned.Where(c => !char.IsControl(c))]).Trim();
        if (text.Length > MaxLength)
        {
            return null;
        }

        // Symbology identifier a scanner can be set to send: ] then a letter for the kind and a digit for the options
        if (text.Length > 3 && text[0] == ']' && char.IsLetter(text[1]) && char.IsDigit(text[2]))
        {
            text = text[3..];
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var link) && link.Scheme is "http" or "https")
        {
            text = FromLink(link);
        }

        text = text.Trim().ToUpperInvariant();

        return text.Length == 0 ? null : text;
    }

    /// <summary>The code in a tracking link: its <c>code</c> value, else the last part of its path.</summary>
    private static string FromLink(Uri link)
    {
        foreach (var pair in link.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals("code", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return Uri.UnescapeDataString(link.AbsolutePath.TrimEnd('/').Split('/')[^1]);
    }
}
