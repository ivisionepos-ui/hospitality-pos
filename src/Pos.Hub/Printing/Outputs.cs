using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
using System.Runtime.InteropServices;
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

            case PrintOutputType.EscPosNetwork or PrintOutputType.EscPosWindows:
                var image = renderer.RenderMono(document, printer.WidthDots);
                var bytes = printer.Commands == PrinterCommands.StarGraphic ? StarGraphic.Printout(image, printer.Cut) : EscPos.Printout(image, printer.Cut);
                return SendRaw(printer, bytes, document.Title);

            default:
                throw new NotSupportedException($"Unknown print output {printer.Output}.");
        }
    }

    /// <summary>Opens the cash drawer on a thermal printer. Returns false when the printer has no drawer connection (PDF, picture).</summary>
    public bool OpenDrawer(PrinterSettings printer)
    {
        ArgumentNullException.ThrowIfNull(printer);
        if (!printer.IsEscPos)
        {
            return false;
        }

        SendRaw(printer, printer.Commands == PrinterCommands.StarGraphic ? StarGraphic.DrawerKick : [.. EscPos.Initialise, .. EscPos.DrawerKick(printer.DrawerPin)], "Open drawer");
        return true;
    }

    /// <summary>Sends printer commands to a thermal printer. Returns where they went.</summary>
    public static string SendRaw(PrinterSettings printer, byte[] bytes, string documentName)
    {
        ArgumentNullException.ThrowIfNull(printer);
        if (printer.Output == PrintOutputType.EscPosNetwork)
        {
            NetworkPrinting.Send(printer.Address, bytes);
            return printer.Address;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows printers can only be used when the hub runs on Windows.");
        }

        RawWindowsPrinting.Send(printer.PrinterName, bytes, documentName);
        return printer.PrinterName;
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

/// <summary>Sends bytes to a network printer's raw port (9100 unless the address says otherwise).</summary>
public static class NetworkPrinting
{
    /// <summary>How long to wait for the printer to answer.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    /// <summary>Splits "host" or "host:port" (port 9100 by default). Throws <see cref="ArgumentException"/> if it is not a printer address.</summary>
    public static (string Host, int Port) Parse(string? address)
    {
        var text = (address ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new ArgumentException("Type the printer's address, e.g. 192.168.1.50 (it is on the printer's self-test page).");
        }

        var host = text;
        var port = 9100;
        var colon = text.LastIndexOf(':');
        if (colon > 0)
        {
            host = text[..colon];
            if (!int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            {
                throw new ArgumentException($"\"{text}\" is not a printer address. Use e.g. 192.168.1.50 or 192.168.1.50:9100.");
            }
        }

        if (host.Length is 0 or > 253 || host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')))
        {
            throw new ArgumentException($"\"{text}\" is not a printer address. Use e.g. 192.168.1.50 or 192.168.1.50:9100.");
        }

        return (host, port);
    }

    /// <summary>Sends the bytes; throws a clear message if the printer cannot be reached.</summary>
    public static void Send(string address, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var (host, port) = Parse(address);
        try
        {
            using var client = new System.Net.Sockets.TcpClient { SendTimeout = (int)Timeout.TotalMilliseconds, NoDelay = true };
            using (var cancel = new CancellationTokenSource(Timeout))
            {
                client.ConnectAsync(host, port, cancel.Token).AsTask().GetAwaiter().GetResult();
            }

            using var stream = client.GetStream();
            stream.Write(bytes);
            stream.Flush();
            client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or OperationCanceledException)
        {
            throw new InvalidOperationException($"Cannot reach the printer at {host}:{port}. Check it is switched on, has paper, and is on the shop network.", ex);
        }
    }
}

/// <summary>Sends printer commands straight to a Windows printer (the driver passes them on unchanged: "RAW").</summary>
[SupportedOSPlatform("windows")]
internal static class RawWindowsPrinting
{
    public static void Send(string printerName, byte[] bytes, string documentName)
    {
        if (!NativeMethods.OpenPrinter(printerName, out var handle, IntPtr.Zero))
        {
            throw new InvalidOperationException($"The printer \"{printerName}\" is not installed on this PC.");
        }

        try
        {
            var info = new NativeMethods.DocInfo1 { DocName = documentName, DataType = "RAW" };
            if (NativeMethods.StartDocPrinter(handle, 1, ref info) == 0)
            {
                throw new InvalidOperationException($"The printer \"{printerName}\" did not accept the printout (error {Marshal.GetLastPInvokeError()}).");
            }

            try
            {
                NativeMethods.StartPagePrinter(handle);
                if (!NativeMethods.WritePrinter(handle, bytes, bytes.Length, out var written) || written != bytes.Length)
                {
                    throw new InvalidOperationException($"The printer \"{printerName}\" stopped part way (error {Marshal.GetLastPInvokeError()}).");
                }

                NativeMethods.EndPagePrinter(handle);
            }
            finally
            {
                NativeMethods.EndDocPrinter(handle);
            }
        }
        finally
        {
            NativeMethods.ClosePrinter(handle);
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DocInfo1
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string DocName;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? OutputFile;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string DataType;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenPrinter(string printerName, out IntPtr printer, IntPtr defaults);

        [DllImport("winspool.drv", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClosePrinter(IntPtr printer);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int StartDocPrinter(IntPtr printer, int level, ref DocInfo1 info);

        [DllImport("winspool.drv", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EndDocPrinter(IntPtr printer);

        [DllImport("winspool.drv", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool StartPagePrinter(IntPtr printer);

        [DllImport("winspool.drv", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EndPagePrinter(IntPtr printer);

        [DllImport("winspool.drv", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WritePrinter(IntPtr printer, byte[] bytes, int count, out int written);
    }
}
