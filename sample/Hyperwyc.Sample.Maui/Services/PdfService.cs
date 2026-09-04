using Shared;
using SkiaSharp;

namespace Hyperwyc.Sample.Maui.Services;

/// <summary>
/// PDF report service using SkiaSharp for rendering.
/// </summary>
public sealed class PdfService
{
    // Page dimensions (A4: 595 x 842 points, 1pt = 1/72 inch)
    private const float _pageWidth = 595f;
    private const float _pageHeight = 842f;
    private const float _margin = 100f;

    // Typography
    private const float _titleFontSize = 24f;
    private const float _headerFontSize = 18f;
    private const float _bodyFontSize = 11f;
    private const float _smallFontSize = 9f;

    // Colors (matching app branding)
    private static readonly SKColor _brandOrange = new(249, 115, 22); // #f97316
    private static readonly SKColor _textDark = new(17, 24, 39); // #111827
    private static readonly SKColor _textMedium = new(107, 114, 128); // #6b7280
    private static readonly SKColor _borderLight = new(229, 231, 235); // #e5e7eb

    // Typefaces (reusable font definitions)
    private static readonly SKTypeface _arialBold = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

    private static readonly SKTypeface _arialRegular = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

    // Fonts (reusable at specific sizes)
    private static readonly SKFont _titleFont = new(_arialBold, _titleFontSize);
    private static readonly SKFont _headerFont = new(_arialBold, _headerFontSize);
    private static readonly SKFont _bodyFont = new(_arialRegular, _bodyFontSize);
    private static readonly SKFont _smallFont = new(_arialRegular, _smallFontSize);

    // Reusable paints
    private static readonly SKPaint _brandOrangePaint = new() { Color = _brandOrange, IsAntialias = true };
    private static readonly SKPaint _textDarkPaint = new() { Color = _textDark, IsAntialias = true };
    private static readonly SKPaint _textMediumPaint = new() { Color = _textMedium, IsAntialias = true };

    public static async Task<Stream> GeneratePdfAsync(
        Sale sale,
        CancellationToken cancellationToken = default) =>
        // Run PDF generation on thread pool to avoid blocking UI
        await Task.Run(() => GeneratePdf(sale, cancellationToken), cancellationToken);

    private static MemoryStream GeneratePdf(
        Sale sale,
        CancellationToken cancellationToken)
    {
        var stream = new MemoryStream();

        using var document = SKDocument.CreatePdf(stream, CreateMetadata(sale));

        // Track current page and Y position
        var currentY = _margin;

        var canvas =
            // Header (first page)
            document.BeginPage(_pageWidth, _pageHeight);
        currentY = DrawHeader(canvas, currentY);


        // Draw each entry
        cancellationToken.ThrowIfCancellationRequested();

        // Start new page if needed
        if (currentY + 100 > _pageHeight - _margin)
        {
            document.EndPage();
            canvas = document.BeginPage(_pageWidth, _pageHeight);
            currentY = _margin;
        }

        canvas = DrawEntry(canvas, sale, currentY);

        // Footer on last page
        DrawFooter(canvas, "Hyperwyc demo");

        document.EndPage();
        document.Close();

        stream.Position = 0;
        return stream;
    }

    private static SKDocumentPdfMetadata CreateMetadata(Sale sale)
    {
        return new SKDocumentPdfMetadata
        {
            Title       = $"Receipt for sale #{sale.Id}",
            Author      = "Hyperwyc demo app",
            Subject     = "Sales Receipt",
            Creator     = "Hyperwyc Demo App",
            Creation    = DateTime.UtcNow
        };
    }

    private static float DrawHeader(SKCanvas canvas, float y)
    {
        // App title
        canvas.DrawText("Hyperwyc", _margin, y + _titleFontSize, SKTextAlign.Center, _titleFont, _brandOrangePaint);
        y += _titleFontSize + 5f;

        // Subtitle
        canvas.DrawText("Sales receipt", _margin, y + _bodyFontSize, SKTextAlign.Center, _bodyFont, _textMediumPaint);
        y += _bodyFontSize + 5f;

        // Generation date
        canvas.DrawText($"Generated on {DateTime.Now:MMMM d, yyyy}", _margin, y + _smallFontSize,  SKTextAlign.Center,_smallFont, _textMediumPaint);
        y += _smallFontSize + 10f;

        // Horizontal line
        using var paint = new SKPaint();
        paint.Color = _brandOrange;
        paint.StrokeWidth = 2f;
        paint.Style = SKPaintStyle.Stroke;

        canvas.DrawLine(_margin, y, _pageWidth - _margin, y, paint);
        y += 30f;

        return y;
    }

    private static SKCanvas DrawEntry(SKCanvas canvas, Sale entry, float y)
    {
        // Entry box background
        using (var paint = new SKPaint())
        {
            paint.Color = new SKColor(255, 255, 255, 255);
            paint.Style = SKPaintStyle.Fill;

            var rect = new SKRect(_margin, y, _pageWidth - _margin, y + 100);
            canvas.DrawRoundRect(rect, 8f, 8f, paint);
        }

        // Entry box border
        using (var paint = new SKPaint())
        {
            paint.Color = _borderLight;
            paint.StrokeWidth = 1f;
            paint.Style = SKPaintStyle.Stroke;

            var rect = new SKRect(_margin, y, _pageWidth - _margin, y + 100);
            canvas.DrawRoundRect(rect, 8f, 8f, paint);
        }

        y += 15f; // Top padding

        // Subject heading
        canvas.DrawText(entry.ProductName, _margin + 15f, y + _headerFontSize,  SKTextAlign.Center, _headerFont, _textDarkPaint);
        y += _headerFontSize + 5f;

        // Date
        var dateText = entry.SoldAt.ToString("dddd, MMMM d, yyyy");

        canvas.DrawText(dateText, _margin + 15f, y + _bodyFontSize, SKTextAlign.Center, _bodyFont, _textMediumPaint);

        return canvas;
    }

    private static void DrawFooter(SKCanvas canvas, string footerText) => canvas.DrawText(footerText, _margin, _pageHeight - _margin + _smallFontSize, SKTextAlign.Center, _smallFont, _textMediumPaint);
}
