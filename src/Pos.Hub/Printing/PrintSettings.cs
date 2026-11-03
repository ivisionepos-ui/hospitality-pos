namespace Pos.Hub.Printing;

/// <summary>Where a printout goes.</summary>
public enum PrintOutputType
{
    /// <summary>Not printed.</summary>
    None,

    /// <summary>A PDF file the size of the paper roll, in a folder (works on any PC).</summary>
    Pdf,

    /// <summary>A PNG picture in a folder.</summary>
    Png,

    /// <summary>
    /// A printer installed in Windows, by name. "Microsoft Print to PDF" saves PDF files into the folder without asking.
    /// </summary>
    Windows,
}

/// <summary>One printer: what it is and where its output goes.</summary>
public sealed class PrinterSettings
{
    /// <summary>Where the output goes.</summary>
    public PrintOutputType Output { get; set; } = PrintOutputType.Pdf;

    /// <summary>Windows printer name, for Output = Windows (e.g. "Microsoft Print to PDF").</summary>
    public string PrinterName { get; set; } = "Microsoft Print to PDF";

    /// <summary>Paper roll width: 80 or 58 mm.</summary>
    public int PaperWidthMm { get; set; } = 80;

    /// <summary>Folder for PDF and PNG files (and for Microsoft Print to PDF).</summary>
    public string Folder { get; set; } = "data/prints";

    /// <summary>Printable width in printer dots (203 dpi): 576 for 80 mm paper, 384 for 58 mm.</summary>
    public int WidthDots => PaperWidthMm <= 58 ? 384 : 576;

    /// <summary>Printable width in millimetres.</summary>
    public double PrintableWidthMm => PaperWidthMm <= 58 ? 48 : 72;
}

/// <summary>What is printed on receipts.</summary>
public sealed class ReceiptSettings
{
    /// <summary>Shop name at the top.</summary>
    public string BusinessName { get; set; } = "My Takeaway";

    /// <summary>Shop name in Arabic (optional).</summary>
    public string? BusinessNameAr { get; set; }

    /// <summary>Address lines.</summary>
    public IList<string> AddressLines { get; init; } = [];

    /// <summary>Phone number.</summary>
    public string? Phone { get; set; }

    /// <summary>VAT registration number (required on UK VAT receipts when VAT registered).</summary>
    public string? VatNumber { get; set; }

    /// <summary>Message at the bottom, e.g. "Thank you".</summary>
    public string? Footer { get; set; } = "Thank you for your order";

    /// <summary>Footer in Arabic (optional).</summary>
    public string? FooterAr { get; set; }

    /// <summary>"en" (default), "ar" or "both": which language names are printed in.</summary>
    public string Language { get; set; } = "en";
}

/// <summary>The "Printing" section of appsettings.json.</summary>
public sealed class PrintSettings
{
    /// <summary>Customer receipt printer.</summary>
    public PrinterSettings Receipt { get; init; } = new();

    /// <summary>Kitchen ticket printer.</summary>
    public PrinterSettings Kitchen { get; init; } = new();

    /// <summary>What appears on receipts.</summary>
    public ReceiptSettings ReceiptText { get; init; } = new();

    /// <summary>Print the receipt automatically when an order is completed.</summary>
    public bool AutoPrintReceipt { get; set; } = true;

    /// <summary>Print kitchen tickets automatically when items are sent to the kitchen.</summary>
    public bool AutoPrintKitchen { get; set; } = true;

    /// <summary>Tries before a print is marked as failed.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Seconds between tries.</summary>
    public double RetrySeconds { get; set; } = 10;

    /// <summary>Font file for text (DejaVu Sans is bundled and includes Arabic).</summary>
    public string FontFile { get; set; } = "Printing/Fonts/DejaVuSans.ttf";

    /// <summary>Font file for bold text.</summary>
    public string BoldFontFile { get; set; } = "Printing/Fonts/DejaVuSans-Bold.ttf";

    /// <summary>The printer for a target.</summary>
    public PrinterSettings For(PrintTarget target) => target == PrintTarget.Kitchen ? Kitchen : Receipt;
}

/// <summary>Which printer a job goes to.</summary>
public enum PrintTarget
{
    /// <summary>Customer receipt printer (also reports).</summary>
    Receipt,

    /// <summary>Kitchen printer.</summary>
    Kitchen,
}
