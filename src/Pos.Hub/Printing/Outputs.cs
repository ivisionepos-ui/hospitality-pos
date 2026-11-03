using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
using System.Runtime.Versioning;

namespace Pos.Hub.Printing;

/// <summary>Sends a drawn printout to where the printer settings say: PDF file, PNG file, or a Windows printer.</summary>
public sealed class PrintOutputs(Renderer renderer, TimeProvider clock)
{
    /// <summary>Prints the document. Returns where it went (file path or printer name), or null when printing is off.</summary>
    public string? Send(PrintDocument document, PrinterSettings printer, Guid jobId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(printer);
        switch (printer.Output)
        {
            case PrintOutputType.None:
                return null;

            case PrintOutputType.Pdf:
            {
                var path = FilePath(printer, document, jobId, "pdf");
                WriteFile(path, renderer.RenderPdf(document, printer.WidthDots, printer.PaperWidthMm));
                return path;
            }

            case PrintOutputType.Png:
            {
                var path = FilePath(printer, document, jobId, "png");
                WriteFile(path, renderer.RenderPng(document, printer.WidthDots));
                return path;
            }

            case PrintOutputType.Windows:
            {
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("Windows printers can only be used when the hub runs on Windows.");
                }

                var isPdfPrinter = printer.PrinterName.Contains("PDF", StringComparison.OrdinalIgnoreCase);
                var saveTo = isPdfPrinter ? FilePath(printer, document, jobId, "pdf") : null;
                if (saveTo is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(saveTo)!);
                }

                WindowsPrinting.Print(renderer.RenderPng(document, printer.WidthDots), printer.PrinterName, printer.PrintableWidthMm, document.Title, saveTo);
                return saveTo ?? printer.PrinterName;
            }

            default:
                throw new NotSupportedException($"Unknown print output {printer.Output}.");
        }
    }

    private string FilePath(PrinterSettings printer, PrintDocument document, Guid jobId, string extension)
    {
        var stamp = clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var safeTitle = string.Concat(document.Title.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        return Path.GetFullPath(Path.Combine(printer.Folder, $"{stamp}-{safeTitle}-{jobId.ToString("N")[^6..]}.{extension}"));
    }

    private static void WriteFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".part";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Prints a picture of the printout on a Windows printer at the paper roll's width, over as many pages as needed.
/// For "Microsoft Print to PDF" the file name is set in advance, so Windows saves the PDF without asking.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsPrinting
{
    public static void Print(byte[] png, string printerName, double widthMm, string documentName, string? saveToFile)
    {
        using var stream = new MemoryStream(png);
        using var image = Image.FromStream(stream);
        using var document = new System.Drawing.Printing.PrintDocument();
        document.PrinterSettings.PrinterName = printerName;
        if (!document.PrinterSettings.IsValid)
        {
            throw new InvalidOperationException($"The printer \"{printerName}\" is not installed on this PC.");
        }

        document.DocumentName = documentName;
        document.PrintController = new StandardPrintController(); // no "Printing..." window
        if (saveToFile is not null)
        {
            document.PrinterSettings.PrintToFile = true;
            document.PrinterSettings.PrintFileName = saveToFile;
        }

        var totalMm = image.Height * widthMm / image.Width;
        var doneMm = 0d;
        document.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            g.PageUnit = GraphicsUnit.Millimeter;
            const float margin = 5;
            var pageMm = (e.PageSettings.PrintableArea.Height * 0.254) - (2 * margin);
            var sliceMm = Math.Min(pageMm, totalMm - doneMm);
            var sourceTop = (float)(doneMm / totalMm * image.Height);
            var sourceHeight = (float)(sliceMm / totalMm * image.Height);
            g.DrawImage(
                image,
                new RectangleF(margin, margin, (float)widthMm, (float)sliceMm),
                new RectangleF(0, sourceTop, image.Width, sourceHeight),
                GraphicsUnit.Pixel);
            doneMm += sliceMm;
            e.HasMorePages = doneMm < totalMm - 0.01;
        };

        document.Print();
    }
}
