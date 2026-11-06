using System.Globalization;
using Pos.Domain.Common;
using Pos.Hub.Data;

namespace Pos.Hub.Printing;

/// <summary>One printer as chosen on the till (More → Printers). File folders are not changed from the till.</summary>
/// <param name="Output">PDF file, Windows printer (picture), network thermal printer, or Windows thermal printer.</param>
/// <param name="PrinterName">Windows printer name, for the Windows outputs.</param>
/// <param name="Address">Network address, for a network thermal printer.</param>
/// <param name="PaperWidthMm">80 or 58.</param>
/// <param name="Cut">Cut after each printout.</param>
/// <param name="DrawerPin">Cash drawer socket: 2 or 5.</param>
/// <param name="Commands">Command language: ESC/POS (Epson, Xprinter, most) or Star graphic (Star TSP100).</param>
public sealed record PrinterChoice(PrintOutputType Output, string? PrinterName, string? Address, int PaperWidthMm, bool Cut, int DrawerPin, PrinterCommands Commands = PrinterCommands.EscPos)
{
    /// <summary>Checks and tidies the choice; throws <see cref="ArgumentException"/> with a clear reason.</summary>
    public PrinterChoice Checked(string which)
    {
        if (!Enum.IsDefined(Output))
        {
            throw new ArgumentException($"Choose how the {which} printer is connected.");
        }

        if (PaperWidthMm is not (58 or 80))
        {
            throw new ArgumentException("Paper width must be 80 mm or 58 mm.");
        }

        if (DrawerPin is not (2 or 5))
        {
            throw new ArgumentException("The drawer socket is 2 (usual) or 5.");
        }

        if (!Enum.IsDefined(Commands))
        {
            throw new ArgumentException($"Choose the {which} printer's type.");
        }

        if (Commands == PrinterCommands.StarGraphic && DrawerPin != 2 && Output is PrintOutputType.EscPosNetwork or PrintOutputType.EscPosWindows)
        {
            throw new ArgumentException("Star TSP100 printers open the drawer on their one drawer socket: choose pin 2.");
        }

        var name = string.IsNullOrWhiteSpace(PrinterName) ? null : PrinterName.Trim();
        if (Output is PrintOutputType.Windows or PrintOutputType.EscPosWindows && (name is null || name.Length > 200 || name.Any(char.IsControl)))
        {
            throw new ArgumentException($"Choose the {which} printer's name as Windows shows it.");
        }

        string? address = null;
        if (Output == PrintOutputType.EscPosNetwork)
        {
            var (host, port) = NetworkPrinting.Parse(Address);
            address = port == 9100 ? host : string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");
        }

        return this with { PrinterName = name, Address = address };
    }

    /// <summary>The choice as it is set now.</summary>
    public static PrinterChoice From(PrinterSettings p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.Output, p.PrinterName, p.Address, p.PaperWidthMm, p.Cut, p.DrawerPin, p.Commands);
    }

    /// <summary>Puts the choice into the printer settings in use.</summary>
    public void ApplyTo(PrinterSettings p)
    {
        ArgumentNullException.ThrowIfNull(p);
        p.Output = Output;
        p.PrinterName = PrinterName ?? p.PrinterName;
        p.Address = Address ?? string.Empty;
        p.PaperWidthMm = PaperWidthMm;
        p.Cut = Cut;
        p.DrawerPin = DrawerPin;
        p.Commands = Commands;
    }
}

/// <summary>A kitchen printer as chosen on the till: its name, connection, and what it prints.</summary>
/// <param name="Id">Id (empty for a new printer: one is made when saved).</param>
/// <param name="Name">Name printed at the top of its tickets, e.g. "Pizza".</param>
/// <param name="Printer">How it is connected.</param>
/// <param name="Mode">Everything, or only the chosen categories and items.</param>
/// <param name="CategoryIds">Categories it prints.</param>
/// <param name="ProductIds">Single items it also prints, whatever their category ("also here").</param>
/// <param name="ExcludedProductIds">Single items it does not print although their category is chosen ("not here").</param>
public sealed record KitchenPrinterChoice(Guid Id, string Name, PrinterChoice Printer, KitchenPrintMode Mode, IReadOnlyList<Guid> CategoryIds, IReadOnlyList<Guid> ProductIds, IReadOnlyList<Guid>? ExcludedProductIds = null);

/// <summary>The receipt printer, the kitchen printers and the drawer rule.</summary>
/// <param name="Receipt">Customer receipt printer (the cash drawer plugs into it).</param>
/// <param name="Kitchens">Kitchen printers, first one first (it also prints anything no other printer chose).</param>
/// <param name="OpenDrawerOnCash">Open the drawer for cash payments, cash refunds and paid in/out.</param>
public sealed record PrinterSetup(PrinterChoice Receipt, IReadOnlyList<KitchenPrinterChoice> Kitchens, bool OpenDrawerOnCash)
{
    /// <summary>Most kitchen printers.</summary>
    public const int MaxKitchens = 10;

    /// <summary>Checks and tidies everything; throws <see cref="ArgumentException"/> with a clear reason.</summary>
    public PrinterSetup Checked()
    {
        if (Kitchens is not { Count: > 0 })
        {
            throw new ArgumentException("Keep at least one kitchen printer (it can be set to Off).");
        }

        if (Kitchens.Count > MaxKitchens)
        {
            throw new ArgumentException($"At most {MaxKitchens} kitchen printers.");
        }

        var kitchens = new List<KitchenPrinterChoice>();
        foreach (var k in Kitchens)
        {
            var name = string.Join(' ', (k.Name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (name.Length is 0 or > 30)
            {
                throw new ArgumentException("Give each kitchen printer a name of up to 30 characters, e.g. Pizza.");
            }

            if (kitchens.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"Two kitchen printers are called {name}. Give each its own name.");
            }

            if (!Enum.IsDefined(k.Mode))
            {
                throw new ArgumentException($"Choose what the {name} printer prints.");
            }

            var categories = (k.CategoryIds ?? []).Distinct().Take(500).ToList();
            var products = (k.ProductIds ?? []).Distinct().Take(2000).ToList();
            var excluded = (k.ExcludedProductIds ?? []).Distinct().Except(products).Take(2000).ToList();
            if (k.Mode == KitchenPrintMode.Chosen && categories.Count == 0 && products.Count == 0)
            {
                throw new ArgumentException($"Choose what the {name} printer prints, or set it to print everything.");
            }

            var id = k.Id == Guid.Empty || kitchens.Any(x => x.Id == k.Id) ? Guid.NewGuid() : k.Id;
            kitchens.Add(new KitchenPrinterChoice(id, name, k.Printer.Checked(name), k.Mode,
                k.Mode == KitchenPrintMode.Chosen ? categories : [], k.Mode == KitchenPrintMode.Chosen ? products : [], k.Mode == KitchenPrintMode.Chosen ? excluded : []));
        }

        return new PrinterSetup(Receipt.Checked("receipt"), kitchens, OpenDrawerOnCash);
    }
}

/// <summary>How printers were saved before there could be several kitchen printers.</summary>
/// <param name="Receipt">Receipt printer.</param>
/// <param name="Kitchen">The one kitchen printer.</param>
/// <param name="OpenDrawerOnCash">Drawer rule.</param>
internal sealed record OldPrinterSetup(PrinterChoice Receipt, PrinterChoice Kitchen, bool OpenDrawerOnCash);

/// <summary>
/// Keeps the printer choices in the hub database and uses them straight away. Until the owner saves them,
/// appsettings.json is used. Also prints test pages and opens the drawer for testing.
/// </summary>
public sealed class PrinterSetupService(HubDatabase db, PrintSettings settings, PrintOutputs outputs, TimeProvider clock)
{
    private const string Key = "printers";

    /// <summary>The printers as set now.</summary>
    public PrinterSetup Get() => new(
        PrinterChoice.From(settings.Receipt),
        [.. settings.Kitchens.Select(k => new KitchenPrinterChoice(k.Id, k.Name, PrinterChoice.From(k.Printer), k.Mode, [.. k.CategoryIds], [.. k.ProductIds], [.. k.ExcludedProductIds]))],
        settings.OpenDrawerOnCash);

    /// <summary>Saves and uses new choices (no restart).</summary>
    public PrinterSetup Save(PrinterSetup setup, Guid byUserId)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var checkedSetup = setup.Checked();
        db.Run(() => db.Execute(
            "INSERT OR REPLACE INTO settings (key, value, updated_at, updated_by) VALUES ($k, $v, $at, $by);",
            ("$k", Key), ("$v", PosJson.Serialize(checkedSetup)), ("$at", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("$by", byUserId.ToString())));
        Apply(checkedSetup);
        return checkedSetup;
    }

    /// <summary>Puts the saved choices into use (at start-up).</summary>
    public void Apply()
    {
        var saved = db.Run(() => db.Scalar("SELECT value FROM settings WHERE key = $k;", ("$k", Key)) as string);
        if (saved is null)
        {
            return;
        }

        // Saved before there could be several kitchen printers: the one kitchen printer, printing everything.
        if (System.Text.Json.Nodes.JsonNode.Parse(saved)?["kitchens"] is null)
        {
            var old = PosJson.Deserialize<OldPrinterSetup>(saved);
            Apply(new PrinterSetup(old.Receipt, [new KitchenPrinterChoice(KitchenStation.MainId, "Kitchen", old.Kitchen, KitchenPrintMode.Everything, [], [])], old.OpenDrawerOnCash));
            return;
        }

        var setup = PosJson.Deserialize<PrinterSetup>(saved);
        if (System.Text.Json.Nodes.JsonNode.Parse(saved)?["kitchens"]?.AsArray().Any(k => k?["excludedProductIds"] is null) == true)
        {
            // Saved by 0.16.0, where choosing a single item moved it to that printer. Now a chosen item prints there
            // as well, so it is marked "not here" on every other printer: tickets print exactly as before.
            setup = setup with
            {
                Kitchens = [.. setup.Kitchens.Select(k => k with
                {
                    ExcludedProductIds = [.. setup.Kitchens.Where(o => o.Id != k.Id).SelectMany(o => o.ProductIds).Except(k.ProductIds).Distinct()],
                })],
            };
        }

        Apply(setup);
    }

    /// <summary>Prints a test page now on one printer. Returns where it went; throws with a clear reason if it cannot.</summary>
    public string TestPrint(PrintTarget target, string shopName, Guid? kitchenId = null)
    {
        var station = target == PrintTarget.Kitchen ? settings.KitchenPrinter(kitchenId) : null;
        var printer = station?.Printer ?? settings.Receipt;
        if (printer.Output == PrintOutputType.None)
        {
            throw new InvalidOperationException("This printer is switched off. Choose how it is connected first.");
        }

        var now = clock.GetUtcNow().UtcDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        var page = new PrintDocument($"Printer test {target}",
        [
            new PrintText(shopName, PrintAlign.Center, PrintSize.Large, Bold: true),
            new PrintText(station is null ? "RECEIPT PRINTER TEST" : $"{station.Name.ToUpperInvariant()} PRINTER TEST", PrintAlign.Center, Bold: true, Inverted: true),
            new PrintSpace(0.5),
            new PrintText($"Printed {now} (UTC)", PrintAlign.Center, PrintSize.Small),
            new PrintRule(),
            new PrintColumns("Pound sign and pence", "£12.34"),
            new PrintColumns("Large text", "£5.00", PrintSize.Large, Bold: true),
            new PrintText("If this page is cut cleanly and the edges of the line below reach both sides, the paper width is right.", Size: PrintSize.Small),
            new PrintRule(),
            new PrintText($"{printer.PaperWidthMm} mm paper · {(printer.IsEscPos ? (printer.Cut ? "cut on" : "cut off") : "picture printing")}", PrintAlign.Center, PrintSize.Small),
        ]);
        return outputs.Send(page with { Title = $"Printer test {station?.Name ?? "Receipt"}" }, printer, Guid.NewGuid()) ?? "(not printed)";
    }

    /// <summary>Opens the cash drawer now (test). Throws if the receipt printer cannot open one.</summary>
    public void TestDrawer()
    {
        if (!outputs.OpenDrawer(settings.Receipt))
        {
            throw new InvalidOperationException("The cash drawer plugs into a thermal receipt printer. Choose a network or USB thermal printer for receipts first.");
        }
    }

    /// <summary>Printers installed in Windows (empty on other systems), for choosing by name.</summary>
    public static IReadOnlyList<string> InstalledPrinters() =>
        OperatingSystem.IsWindowsVersionAtLeast(6, 1) ? [.. System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>().Order(StringComparer.OrdinalIgnoreCase)] : [];

    private void Apply(PrinterSetup setup)
    {
        setup.Receipt.ApplyTo(settings.Receipt);
        settings.Kitchens = [.. setup.Kitchens.Select(k =>
        {
            // Kitchen PDF files all go in the kitchen folder; the printer's name is in each file name.
            var printer = new PrinterSettings { Folder = settings.Kitchen.Folder };
            k.Printer.ApplyTo(printer);
            return new KitchenStation
            {
                Id = k.Id, Name = k.Name, Printer = printer, Mode = k.Mode,
                CategoryIds = k.CategoryIds.ToHashSet(), ProductIds = k.ProductIds.ToHashSet(), ExcludedProductIds = (k.ExcludedProductIds ?? []).ToHashSet(),
            };
        })];
        settings.OpenDrawerOnCash = setup.OpenDrawerOnCash;
    }
}
