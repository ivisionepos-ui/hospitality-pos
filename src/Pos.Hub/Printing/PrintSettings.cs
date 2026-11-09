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
    /// A printer installed in Windows, by name, printed as a picture through its driver.
    /// "Microsoft Print to PDF" saves PDF files into the folder without asking.
    /// </summary>
    Windows,

    /// <summary>A thermal printer on the shop network (ESC/POS on port 9100), by its address. Cuts and opens the drawer.</summary>
    EscPosNetwork,

    /// <summary>A thermal printer installed in Windows (usually USB), sent ESC/POS directly through the driver. Cuts and opens the drawer.</summary>
    EscPosWindows,
}

/// <summary>The command language a thermal printer understands.</summary>
public enum PrinterCommands
{
    /// <summary>ESC/POS: Epson (e.g. TM-m30, TM-T20), Xprinter (e.g. XP-Q801) and most other receipt printers.</summary>
    EscPos,

    /// <summary>STAR Graphic Mode: Star TSP100 / TSP143 series, which print pictures only and do not understand ESC/POS.</summary>
    StarGraphic,
}

/// <summary>One printer: what it is and where its output goes.</summary>
public sealed class PrinterSettings
{
    /// <summary>Where the output goes.</summary>
    public PrintOutputType Output { get; set; } = PrintOutputType.Pdf;

    /// <summary>Windows printer name, for Output = Windows (e.g. "Microsoft Print to PDF").</summary>
    public string PrinterName { get; set; } = "Microsoft Print to PDF";

    /// <summary>Network printer address, for Output = EscPosNetwork: "192.168.1.50" or "192.168.1.50:9100".</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>The thermal printer's command language (ESC/POS, or Star graphic for the Star TSP100 series).</summary>
    public PrinterCommands Commands { get; set; } = PrinterCommands.EscPos;

    /// <summary>Cut the paper after each printout (thermal printers with a cutter).</summary>
    public bool Cut { get; set; } = true;

    /// <summary>Cash drawer socket on the printer: 2 (usual) or 5.</summary>
    public int DrawerPin { get; set; } = 2;

    /// <summary>True for thermal printers sent ESC/POS (they can cut and open a cash drawer).</summary>
    public bool IsEscPos => Output is PrintOutputType.EscPosNetwork or PrintOutputType.EscPosWindows;

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

    /// <summary>Kitchen ticket printer from appsettings.json (the first kitchen printer until the owner sets up others).</summary>
    public PrinterSettings Kitchen { get; init; } = new();

    private IReadOnlyList<KitchenStation>? _kitchens;

    /// <summary>
    /// The kitchen printers in use, first one first (it also prints anything not chosen by another printer).
    /// Until set on the Printers screen: one printer called "Kitchen" that prints everything.
    /// </summary>
    public IReadOnlyList<KitchenStation> Kitchens
    {
        get => _kitchens ??= [new KitchenStation { Id = KitchenStation.MainId, Name = "Kitchen", Printer = Kitchen }];
        set => _kitchens = value is { Count: > 0 } ? value : null;
    }

    private IReadOnlyDictionary<Guid, PrinterSettings> _tillReceipts = new Dictionary<Guid, PrinterSettings>();

    /// <summary>
    /// Tills with their own receipt printer (and the cash drawer plugged into it), by till device id.
    /// A till not listed uses the shop receipt printer.
    /// </summary>
    public IReadOnlyDictionary<Guid, PrinterSettings> TillReceipts
    {
        get => _tillReceipts;
        set => _tillReceipts = value ?? new Dictionary<Guid, PrinterSettings>();
    }

    /// <summary>The receipt printer for a till: its own, or the shop receipt printer.</summary>
    public PrinterSettings ReceiptFor(Guid? deviceId) =>
        deviceId is { } id && _tillReceipts.TryGetValue(id, out var own) ? own : Receipt;

    /// <summary>A kitchen printer by id; the first one if it no longer exists.</summary>
    public KitchenStation KitchenPrinter(Guid? id) => Kitchens.FirstOrDefault(k => k.Id == id) ?? Kitchens[0];

    /// <summary>What appears on receipts.</summary>
    public ReceiptSettings ReceiptText { get; init; } = new();

    /// <summary>Print the receipt automatically when an order is fully paid (or completed, if never printed).</summary>
    public bool AutoPrintReceipt { get; set; } = true;

    /// <summary>Open the cash drawer (on the receipt printer) for cash payments, cash refunds and paid in/out.</summary>
    public bool OpenDrawerOnCash { get; set; } = true;

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
    public PrinterSettings For(PrintTarget target) => target == PrintTarget.Kitchen ? Kitchens[0].Printer : Receipt;
}

/// <summary>Which printer a job goes to.</summary>
public enum PrintTarget
{
    /// <summary>Customer receipt printer (also reports).</summary>
    Receipt,

    /// <summary>Kitchen printer.</summary>
    Kitchen,
}
