using System.Text.Json;
using System.Text.RegularExpressions;

namespace AccountOpeningTracker.Api.Models;

public static class ApplicationFormStatuses
{
    /// <summary>Being prepared by Operations (editable).</summary>
    public const string Draft = "Draft";

    /// <summary>Sent for signature; read-only from now on.</summary>
    public const string Sent = "Sent";
}

/// <summary>
/// Stage 2: the account-opening application form Operations prepares for a case.
/// Identity fields (client name, PAN, account type, channel, segment, location,
/// RM, CSE) are NOT stored here: they come from the case, so there is one source
/// of truth. Everything else the form needs is stored as JSON.
/// </summary>
public class ApplicationForm
{
    public int Id { get; set; }
    public string ReferenceId { get; set; } = string.Empty;
    public string Status { get; set; } = ApplicationFormStatuses.Draft;
    public string DataJson { get; set; } = "{}";

    /// <summary>JSON array of the field keys AI filled and nobody has changed since.</summary>
    public string AiFilledJson { get; set; } = "[]";

    public int? UpdatedByUserId { get; set; }
    public string UpdatedByName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
}

/// <summary>
/// A document uploaded on a case after it was logged: the signed form and
/// supporting documents the CSE returns (Stage 3), or corrected documents sent
/// with a discrepancy response (Stage 4).
/// </summary>
public class CaseDocument
{
    public int Id { get; set; }
    public string ReferenceId { get; set; } = string.Empty;
    public int? QueryId { get; set; }

    /// <summary>The workflow action the file was sent with, e.g. "Send signed form to Operations".</summary>
    public string Purpose { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public int? UploadedByUserId { get; set; }
    public string UploadedByName { get; set; } = string.Empty;
    public string Team { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

public sealed record FormFieldDefinition(
    string Key, string Label, string Section, bool Required, string Type = "text",
    string[]? Options = null, string? Help = null, bool MinorOnly = false);

/// <summary>Field catalogue and validation for the application form (modelled on a broker e-KYC form).</summary>
public static class ApplicationFormRules
{
    public static readonly string[] Genders = { "Male", "Female", "Other" };
    public static readonly string[] Occupations = { "Salaried", "Self-employed", "Business", "Professional", "Student", "Retired", "Homemaker", "Other" };
    public static readonly string[] IncomeRanges = { "Below 1 lakh", "1-5 lakh", "5-10 lakh", "10-25 lakh", "Above 25 lakh" };
    public static readonly string[] BankAccountTypes = { "Savings", "Current", "NRE", "NRO" };
    public static readonly string[] SegmentOptions = { "Equity", "F&O", "Commodity", "Currency", "Mutual Fund" };
    public static readonly string[] Relationships = { "Spouse", "Father", "Mother", "Son", "Daughter", "Brother", "Sister", "Other" };

    // No field is required for now: Operations can save and send the form with
    // whatever the CSE has provided. Values that ARE entered are still format-checked.
    public static readonly FormFieldDefinition[] Fields =
    {
        new("dateOfBirth", "Date of birth", "Personal details", false, "date"),
        new("gender", "Gender", "Personal details", false, "select", Genders),
        new("fatherOrSpouseName", "Father's / spouse's name", "Personal details", false),
        new("occupation", "Occupation", "Personal details", false, "select", Occupations),
        new("annualIncome", "Annual income", "Personal details", false, "select", IncomeRanges),

        new("mobile", "Mobile number", "Contact details", false, "tel", Help: "10 digits"),
        new("email", "Email", "Contact details", false, "email"),

        new("addressLine1", "Address line 1", "Address", false),
        new("addressLine2", "Address line 2", "Address", false),
        new("city", "City", "Address", false),
        new("state", "State", "Address", false),
        new("pinCode", "PIN code", "Address", false, Help: "6 digits"),

        new("bankName", "Bank name", "Bank details", false),
        new("bankAccountNumber", "Bank account number", "Bank details", false),
        new("ifsc", "IFSC", "Bank details", false, Help: "e.g. HDFC0001234"),
        new("bankAccountType", "Bank account type", "Bank details", false, "select", BankAccountTypes),

        new("segments", "Trading segments", "Trading preferences", false, "multi", SegmentOptions),

        new("guardianName", "Guardian name", "Guardian (minor accounts)", false, MinorOnly: true),
        new("guardianPan", "Guardian PAN", "Guardian (minor accounts)", false, MinorOnly: true),
        new("guardianRelationship", "Relationship with minor", "Guardian (minor accounts)", false, "select", new[] { "Father", "Mother", "Court-appointed guardian" }, MinorOnly: true),

        new("nomineeName", "Nominee name", "Nominee (optional)", false),
        new("nomineeRelationship", "Nominee relationship", "Nominee (optional)", false, "select", Relationships),
    };

    public static readonly HashSet<string> Keys = Fields.Select(f => f.Key).ToHashSet();

    private static readonly Regex Mobile = new("^[6-9][0-9]{9}$", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly Regex Pin = new("^[1-9][0-9]{5}$", RegexOptions.Compiled);
    private static readonly Regex Ifsc = new("^[A-Z]{4}0[A-Z0-9]{6}$", RegexOptions.Compiled);
    private static readonly Regex BankAccount = new("^[0-9]{9,18}$", RegexOptions.Compiled);
    private static readonly Regex Pan = new("^[A-Z]{5}[0-9]{4}[A-Z]$", RegexOptions.Compiled);

    public static bool IsMinor(string? accountType) => string.Equals(accountType, "Minor", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<FormFieldDefinition> FieldsFor(string? accountType) =>
        Fields.Where(f => !f.MinorOnly || IsMinor(accountType));

    /// <summary>Trims values, normalises case, and drops unknown keys and empty values.</summary>
    public static Dictionary<string, string> Normalize(IDictionary<string, string?>? input)
    {
        var result = new Dictionary<string, string>();
        if (input == null) return result;
        foreach (var (key, raw) in input)
        {
            if (!Keys.Contains(key) || string.IsNullOrWhiteSpace(raw)) continue;
            var v = raw.Trim();
            if (v.Length > 300) v = v[..300];
            if (key is "ifsc" or "guardianPan") v = v.ToUpperInvariant().Replace(" ", "");
            if (key is "mobile" or "pinCode" or "bankAccountNumber") v = v.Replace(" ", "");
            if (key == "email") v = v.ToLowerInvariant();
            result[key] = v;
        }
        return result;
    }

    /// <summary>Format errors in the values that are present (missing values are not errors here).</summary>
    public static List<string> FormatErrors(IReadOnlyDictionary<string, string> data, DateTime today)
    {
        var errors = new List<string>();
        string? V(string k) => data.TryGetValue(k, out var v) ? v : null;
        void Check(string key, string label, Func<string, bool> ok, string hint)
        {
            var v = V(key);
            if (v != null && !ok(v)) errors.Add($"{label}: {hint}");
        }

        foreach (var f in Fields.Where(f => f.Type == "select" && f.Options != null))
            Check(f.Key, f.Label, v => f.Options!.Contains(v), $"choose one of {string.Join(", ", f.Options!)}.");
        Check("segments", "Trading segments", v => v.Split(',').Select(s => s.Trim()).All(s => SegmentOptions.Contains(s)),
            $"choose from {string.Join(", ", SegmentOptions)}.");
        Check("dateOfBirth", "Date of birth",
            v => DateTime.TryParse(v, out var d) && d.Date < today && d.Date > today.AddYears(-120), "enter a valid past date.");
        Check("mobile", "Mobile number", v => Mobile.IsMatch(v), "10 digits starting with 6-9.");
        Check("email", "Email", v => Email.IsMatch(v), "enter a valid email address.");
        Check("pinCode", "PIN code", v => Pin.IsMatch(v), "6 digits.");
        Check("ifsc", "IFSC", v => Ifsc.IsMatch(v), "11 characters like HDFC0001234.");
        Check("bankAccountNumber", "Bank account number", v => BankAccount.IsMatch(v), "9 to 18 digits.");
        Check("guardianPan", "Guardian PAN", v => Pan.IsMatch(v), "format AAAAA9999A.");
        return errors;
    }

    /// <summary>Required fields still empty (none are required at the moment).</summary>
    public static List<string> Missing(IReadOnlyDictionary<string, string> data, string? accountType, DateTime today) =>
        FieldsFor(accountType)
            .Where(f => f.Required && !data.ContainsKey(f.Key))
            .Select(f => f.Label)
            .ToList();

    public static Dictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static List<string> ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>
    /// Why the form cannot be sent for signature yet, or null when it can.
    /// Used as a workflow precondition for "send form".
    /// </summary>
    public static string? NotReadyReason(ApplicationForm? form, string? accountType, DateTime today)
    {
        if (form == null) return "Prepare and save the application form first.";
        var data = Parse(form.DataJson);
        var errors = FormatErrors(data, today);
        if (errors.Count > 0) return "Fix the application form first: " + string.Join(" ", errors);
        var missing = Missing(data, accountType, today);
        return missing.Count > 0 ? $"Complete the application form first (missing: {string.Join(", ", missing)})." : null;
    }
}
