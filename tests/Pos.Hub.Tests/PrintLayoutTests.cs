using Pos.Domain.Common;
using Pos.Hub.Printing;
using SkiaSharp;

namespace Pos.Hub.Tests;

/// <summary>What receipts and kitchen tickets say, and that they draw correctly.</summary>
public sealed class PrintLayoutTests : IDisposable
{
    private readonly Renderer _renderer = new("Printing/Fonts/DejaVuSans.ttf", "Printing/Fonts/DejaVuSans-Bold.ttf");

    public void Dispose() => _renderer.Dispose();

    /// <summary>Saves a sample to the folder in POS_PRINT_SAMPLES, if set, so a person can look at it.</summary>
    private static void Keep(string name, byte[] bytes)
    {
        if (Environment.GetEnvironmentVariable("POS_PRINT_SAMPLES") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
        }
    }

    [Fact]
    public void Receipt_has_the_legal_details_items_in_food_order_totals_vat_and_payments()
    {
        var (order, payments) = PrintSamples.Order();

        var text = Layouts.Receipt(order, payments, PrintSamples.Shop, PrintSamples.London, "Sam", isCopy: false).PlainText;

        Assert.Contains("Olive Grill", text, StringComparison.Ordinal);
        Assert.Contains("VAT No: GB 123 4567 89", text, StringComparison.Ordinal);
        Assert.Contains("03/11/2026 18:42", text, StringComparison.Ordinal);
        Assert.Contains("Order 001", text, StringComparison.Ordinal);
        Assert.Contains("TAKEAWAY - Phone", text, StringComparison.Ordinal);
        Assert.Contains("Served by Sam", text, StringComparison.Ordinal);

        // Mains before drinks, though the cans were entered first.
        Assert.True(text.IndexOf("Doner kebab", StringComparison.Ordinal) < text.IndexOf("2 x Can 330ml", StringComparison.Ordinal));
        Assert.Contains("دونر كباب", text, StringComparison.Ordinal);
        Assert.Contains("+ Garlic sauce / صلصة الثوم", text, StringComparison.Ordinal);
        Assert.Contains("- Margherita", text, StringComparison.Ordinal);
        Assert.Contains("* No onions", text, StringComparison.Ordinal);
        Assert.Contains("Tuesday cans 99p | -£0.42", text, StringComparison.Ordinal);
        Assert.Contains("Discount (Regular customer) | -£2.00", text, StringComparison.Ordinal);

        // 8.99 + 19.99 + 1.98 - 2.00 = 28.96; cash £40, change £11.04
        Assert.Contains("TOTAL | £28.96", text, StringComparison.Ordinal);
        Assert.Contains("Cash | £40.00", text, StringComparison.Ordinal);
        Assert.Contains("Change | £11.04", text, StringComparison.Ordinal);
        Assert.Contains("S 20% |", text, StringComparison.Ordinal);
        Assert.Contains("شكراً لطلبكم", text, StringComparison.Ordinal);
        Assert.DoesNotContain("COPY", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reprint_is_marked_copy_and_english_only_leaves_out_arabic()
    {
        var (order, payments) = PrintSamples.Order();
        var shop = PrintSamples.Shop;
        shop.Language = "en";

        var text = Layouts.Receipt(order, payments, shop, PrintSamples.London, null, isCopy: true).PlainText;

        Assert.StartsWith("*** COPY ***", text, StringComparison.Ordinal);
        Assert.DoesNotContain("دونر", text, StringComparison.Ordinal);
        Assert.Contains("+ Garlic sauce", text, StringComparison.Ordinal);
    }

    [Fact]
    public void English_only_is_the_default_and_the_print_test_has_no_arabic()
    {
        var shop = new ReceiptSettings { BusinessName = "My Takeaway" };
        Assert.Equal("en", shop.Language);

        var (order, payments) = PrintSamples.Order();
        var receipt = Layouts.Receipt(order, payments, new ReceiptSettings { BusinessName = "Olive Grill", FooterAr = "شكراً لطلبكم" }, PrintSamples.London, "Sam", isCopy: false).PlainText;
        var test = PrintTest.SampleReceipt(shop).PlainText + PrintTest.SampleKitchenTicket().PlainText;

        Assert.DoesNotContain(receipt + test, Renderer.IsArabic);
        Assert.Contains("Doner kebab", receipt, StringComparison.Ordinal);
        Assert.Contains("£11.39", test, StringComparison.Ordinal);
    }

    [Fact]
    public void Kitchen_ticket_has_no_prices_and_shows_notes_and_deal_choices()
    {
        var (order, _) = PrintSamples.Order();

        var text = Layouts.KitchenTicket(order, order.ActiveLines, KitchenTicketKind.New, PrintSamples.Shop, PrintSamples.London).PlainText;

        Assert.Contains("NEW ORDER", text, StringComparison.Ordinal);
        Assert.Contains("** No onions **", text, StringComparison.Ordinal);
        Assert.Contains("- Doner kebab", text, StringComparison.Ordinal);
        Assert.DoesNotContain("£", text, StringComparison.Ordinal);
        Assert.Contains("4 item(s)", text, StringComparison.Ordinal); // 1 doner, 1 deal, 2 cans
    }

    [Fact]
    public void Receipt_draws_at_printer_width_for_80_and_58_mm()
    {
        var (order, payments) = PrintSamples.Order();
        var receipt = Layouts.Receipt(order, payments, PrintSamples.Shop, PrintSamples.London, "Sam", false);

        var wide = _renderer.RenderPng(receipt, 576);
        var narrow = _renderer.RenderPng(receipt, 384);
        Keep("receipt-80mm.png", wide);
        Keep("receipt-58mm.png", narrow);

        using var image80 = SKBitmap.Decode(wide);
        using var image58 = SKBitmap.Decode(narrow);
        Assert.Equal(576, image80.Width);
        Assert.Equal(384, image58.Width);
        Assert.True(image58.Height > image80.Height); // narrower paper wraps more
        Assert.True(InkShare(image80) is > 0.01 and < 0.5);
    }

    [Fact]
    public void Kitchen_tickets_draw_including_the_void_band()
    {
        var (order, _) = PrintSamples.Order();

        var ticket = _renderer.RenderPng(Layouts.KitchenTicket(order, order.ActiveLines, KitchenTicketKind.New, PrintSamples.Shop, PrintSamples.London), 576);
        var voidSlip = _renderer.RenderPng(Layouts.KitchenTicket(order, [order.ActiveLines[0]], KitchenTicketKind.Void, PrintSamples.Shop, PrintSamples.London), 576);
        Keep("kitchen-ticket.png", ticket);
        Keep("kitchen-void.png", voidSlip);

        using var image = SKBitmap.Decode(voidSlip);
        Assert.Equal(SKColors.Black, image.GetPixel(2, 12)); // the black band behind "VOID"
    }

    [Fact]
    public void Pdf_is_the_width_of_the_paper_roll()
    {
        var (order, payments) = PrintSamples.Order();

        var pdf = _renderer.RenderPdf(Layouts.Receipt(order, payments, PrintSamples.Shop, PrintSamples.London, "Sam", false), 576, 80);
        Keep("receipt-80mm.pdf", pdf);

        var text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF", text, StringComparison.Ordinal);
        Assert.Matches(@"/MediaBox \[0 0 22[67]", text); // 80 mm = 226.77 points
    }

    [Fact]
    public void Money_is_printed_the_uk_way()
    {
        Assert.Equal("£12.50", Layouts.Money(Money.Gbp(1250)));
        Assert.Equal("-£2.00", Layouts.Money(Money.Gbp(-200)));
        Assert.Equal("£0.05", Layouts.Money(Money.Gbp(5)));
    }

    [Fact]
    public void Arabic_is_detected()
    {
        Assert.True(Renderer.IsArabic('د'));
        Assert.False(Renderer.IsArabic('D'));
    }

    private static double InkShare(SKBitmap image)
    {
        var dark = 0;
        for (var y = 0; y < image.Height; y += 2)
        {
            for (var x = 0; x < image.Width; x += 2)
            {
                if (image.GetPixel(x, y).Red < 128)
                {
                    dark++;
                }
            }
        }

        return dark / (double)(image.Width * image.Height / 4);
    }
}

/// <summary>The --print-test command.</summary>
public sealed class PrintTestCommandTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pos-print-test", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written.
        }
    }

    [Fact]
    public void Prints_both_samples_and_reports_where_they_went()
    {
        using var renderer = new Renderer("Printing/Fonts/DejaVuSans.ttf", "Printing/Fonts/DejaVuSans-Bold.ttf");
        var settings = new PrintSettings
        {
            Receipt = { Output = PrintOutputType.Pdf, Folder = _folder },
            Kitchen = { Output = PrintOutputType.Png, Folder = _folder },
        };
        using var console = new StringWriter();

        var result = PrintTest.Run(new PrintOutputs(renderer, TimeProvider.System), settings, console);

        Assert.Equal(0, result);
        Assert.Contains("Receipt printer: OK (Pdf)", console.ToString(), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(_folder, "*.pdf"));
        Assert.Single(Directory.GetFiles(_folder, "*.png"));
    }

    [Fact]
    public void Reports_a_printer_that_cannot_print()
    {
        using var renderer = new Renderer("Printing/Fonts/DejaVuSans.ttf", "Printing/Fonts/DejaVuSans-Bold.ttf");
        var settings = new PrintSettings
        {
            Receipt = { Output = PrintOutputType.Windows, PrinterName = "No Such Printer 123", Folder = _folder },
            Kitchen = { Output = PrintOutputType.None },
        };
        using var console = new StringWriter();

        Assert.Equal(1, PrintTest.Run(new PrintOutputs(renderer, TimeProvider.System), settings, console));
        Assert.Contains("Receipt printer: FAILED", console.ToString(), StringComparison.Ordinal);
    }
}
