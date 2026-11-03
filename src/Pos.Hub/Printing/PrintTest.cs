namespace Pos.Hub.Printing;

/// <summary>
/// "Pos.Hub --print-test": prints a sample receipt and kitchen ticket on the configured printers and stops.
/// Used to check a printer (or Microsoft Print to PDF) before the shop opens.
/// </summary>
public static class PrintTest
{
    /// <summary>A sample receipt with English and Arabic text.</summary>
    public static PrintDocument SampleReceipt(ReceiptSettings shop)
    {
        ArgumentNullException.ThrowIfNull(shop);
        return new PrintDocument("Print test receipt",
        [
            new PrintText("PRINT TEST", PrintAlign.Center, PrintSize.Large, Bold: true, Inverted: true),
            new PrintText(shop.BusinessName, PrintAlign.Center, PrintSize.Large, Bold: true),
            new PrintText(string.IsNullOrWhiteSpace(shop.BusinessNameAr) ? "مطعم تجريبي" : shop.BusinessNameAr, PrintAlign.Center, PrintSize.Large, Bold: true),
            new PrintRule(),
            new PrintText("Order 001", PrintAlign.Center, PrintSize.Huge, Bold: true),
            new PrintColumns("1 x Doner kebab", "£8.99 S", Bold: true),
            new PrintText("دونر كباب", Indent: 1),
            new PrintText("+ Garlic sauce / صلصة الثوم", Size: PrintSize.Small, Indent: 1),
            new PrintColumns("2 x Can 330ml", "£2.40 S", Bold: true),
            new PrintText("مشروب غازي", Indent: 1),
            new PrintRule(),
            new PrintColumns("TOTAL", "£11.39", PrintSize.Large, Bold: true),
            new PrintColumns("Cash", "£20.00"),
            new PrintColumns("Change", "£8.61", Bold: true),
            new PrintRule(),
            new PrintColumns("S 20%", "£9.49 / £1.90", PrintSize.Small),
            new PrintText("If you can read the Arabic and the £ signs, printing works.", PrintAlign.Center, PrintSize.Small),
            new PrintText("شكراً لطلبكم", PrintAlign.Center),
        ]);
    }

    /// <summary>A sample kitchen ticket.</summary>
    public static PrintDocument SampleKitchenTicket() => new("Print test kitchen",
    [
        new PrintText("PRINT TEST - KITCHEN", PrintAlign.Center, PrintSize.Large, Bold: true, Inverted: true),
        new PrintText("001", PrintAlign.Center, PrintSize.Huge, Bold: true),
        new PrintText("TAKEAWAY", PrintAlign.Center, PrintSize.Large, Bold: true),
        new PrintRule(),
        new PrintText("1 x Doner kebab", Size: PrintSize.Large, Bold: true),
        new PrintText("دونر كباب", Size: PrintSize.Large, Indent: 1),
        new PrintText("+ Garlic sauce", Indent: 1),
        new PrintText("** No onions **", Size: PrintSize.Large, Bold: true, Indent: 1),
        new PrintRule(),
    ]);

    /// <summary>Prints both samples and writes what happened to the console. Returns 0 if both printed.</summary>
    public static int Run(PrintOutputs outputs, PrintSettings settings, TextWriter console)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(console);
        var failures = 0;
        foreach (var (name, document, printer) in new[]
        {
            ("Receipt printer", SampleReceipt(settings.ReceiptText), settings.Receipt),
            ("Kitchen printer", SampleKitchenTicket(), settings.Kitchen),
        })
        {
            try
            {
                var where = outputs.Send(document, printer, Guid.NewGuid());
                console.WriteLine($"{name}: OK ({printer.Output}) -> {where ?? "printing is off"}");
            }
#pragma warning disable CA1031 // Report any printer problem to the person running the test.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures++;
                console.WriteLine($"{name}: FAILED ({printer.Output} \"{printer.PrinterName}\"): {ex.Message}");
            }
        }

        return failures == 0 ? 0 : 1;
    }
}
