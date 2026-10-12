namespace AccountOpeningTracker.Api.Services;

public sealed record UploadedFile(string FileName, string ContentType, byte[] Data);

/// <summary>Validation shared by every upload: size, extension allow-list and a magic-byte check.</summary>
public static class FileUploads
{
    public const long MaxFileBytes = 4 * 1024 * 1024;
    public const int MaxFiles = 5;
    public const long MaxRequestBytes = MaxFiles * MaxFileBytes + 256 * 1024;

    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".pdf"] = "application/pdf",
    };

    /// <summary>Reads and validates the files. Returns the files, or an error message.</summary>
    public static async Task<(List<UploadedFile> Files, string? Error)> ReadAsync(IEnumerable<IFormFile>? input, CancellationToken ct)
    {
        var files = (input ?? Enumerable.Empty<IFormFile>()).Where(f => f.Length > 0).ToList();
        var result = new List<UploadedFile>();
        if (files.Count > MaxFiles) return (result, $"Attach at most {MaxFiles} files.");

        foreach (var file in files)
        {
            if (file.Length > MaxFileBytes) return (result, $"'{file.FileName}' is larger than 4 MB.");
            var ext = Path.GetExtension(file.FileName);
            if (!AllowedTypes.TryGetValue(ext, out var contentType))
                return (result, $"'{file.FileName}': allowed types are JPG, PNG, WEBP and PDF.");

            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            if (!LooksLike(bytes, contentType))
                return (result, $"'{file.FileName}': the content does not match its file type.");

            var name = Path.GetFileName(file.FileName);
            result.Add(new UploadedFile(name.Length > 200 ? $"document{ext}" : name, contentType, bytes));
        }
        return (result, null);
    }

    private static bool LooksLike(byte[] b, string contentType) => contentType switch
    {
        "image/jpeg" => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF,
        "image/png" => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47,
        "image/webp" => b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P',
        "application/pdf" => b.Length > 4 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F',
        _ => false
    };
}
