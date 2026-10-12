using PDFtoImage;
using SkiaSharp;

namespace AccountOpeningTracker.Api.Services;

/// <summary>
/// Renders PDF pages to JPEG so the vision model can read scanned forms.
/// PDFium is not thread-safe, so rendering is serialised.
/// </summary>
public static class PdfPages
{
    private static readonly SemaphoreSlim Lock = new(1, 1);

    /// <summary>~A4 at 120 dpi (about 1000 x 1400 px): readable for the model, small to send.</summary>
    public const int Dpi = 120;

#pragma warning disable CA1416 // PDFtoImage supports Windows, Linux and macOS.
    public static async Task<List<byte[]>> ToJpegsAsync(byte[] pdf, int maxPages, CancellationToken ct)
    {
        var result = new List<byte[]>();
        if (maxPages <= 0) return result;
        await Lock.WaitAsync(ct);
        try
        {
            using var stream = new MemoryStream(pdf, writable: false);
            foreach (var bitmap in Conversion.ToImages(pdfStream: stream, leaveOpen: true, options: new RenderOptions(Dpi: Dpi, WithAnnotations: true, WithFormFill: true)))
            {
                using (bitmap)
                {
                    using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
                    result.Add(data.ToArray());
                }
                if (result.Count >= maxPages) break;
            }
        }
        finally
        {
            Lock.Release();
        }
        return result;
    }
#pragma warning restore CA1416
}
