using System.Text.RegularExpressions;

namespace AccountOpeningTracker.Api.Security;

/// <summary>
/// Masking for users without cases.view.sensitive, and redaction of free
/// text before it is sent to an AI provider.
/// </summary>
public static class SensitiveData
{
    public static readonly Regex PanPattern =
        new(@"\b[A-Za-z]{5}[0-9]{4}[A-Za-z]\b", RegexOptions.Compiled);

    private static readonly Regex EmailPattern =
        new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

    // Phone numbers, Aadhaar-like and account-like digit runs (8+ digits, allowing spaces/dashes).
    private static readonly Regex LongNumberPattern =
        new(@"(?<!\w)\+?\d[\d\s-]{6,}\d(?!\w)", RegexOptions.Compiled);

    public static string MaskPan(string? pan)
    {
        if (string.IsNullOrWhiteSpace(pan)) return string.Empty;
        var p = pan.Trim();
        return p.Length <= 4 ? new string('•', p.Length) : new string('•', p.Length - 4) + p[^4..];
    }

    public static string MaskName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(p => p[0] + new string('•', Math.Max(2, p.Length - 1))));
    }

    public static string? MaskAccountNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var v = value.Trim();
        return v.Length <= 3 ? "•••" : new string('•', v.Length - 3) + v[^3..];
    }

    /// <summary>
    /// Removes client identifiers from free text: the client's name (full and
    /// each name part of 3+ letters), PAN, account number, e-mail addresses
    /// and long digit sequences.
    /// </summary>
    public static string Redact(
        string? text,
        string? clientName = null,
        string? pan = null,
        string? accountNumber = null,
        string clientPlaceholder = "[CLIENT]")
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var result = text;

        if (!string.IsNullOrWhiteSpace(accountNumber))
            result = result.Replace(accountNumber.Trim(), "[ACCOUNT-NO]", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(pan))
            result = result.Replace(pan.Trim(), "[PAN]", StringComparison.OrdinalIgnoreCase);

        result = PanPattern.Replace(result, "[PAN]");
        result = EmailPattern.Replace(result, "[EMAIL]");
        result = LongNumberPattern.Replace(result, "[NUMBER]");

        if (!string.IsNullOrWhiteSpace(clientName))
        {
            var full = clientName.Trim();
            result = Regex.Replace(result, Regex.Escape(full), clientPlaceholder, RegexOptions.IgnoreCase);
            foreach (var part in full.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length >= 3))
            {
                result = Regex.Replace(result, $@"\b{Regex.Escape(part)}\b", clientPlaceholder, RegexOptions.IgnoreCase);
            }
        }

        return result;
    }
}
