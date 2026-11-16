using System.Net;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Printing;

namespace Pos.Hub.Tests;

/// <summary>Receipt layouts: parts on or off, their order, item size, language, own texts, and the VAT rules.</summary>
public sealed class ReceiptLayoutTests : HubTestBase
{
    private static ReceiptLayout With(ReceiptKind kind, Func<List<LayoutPart>, List<LayoutPart>> change, PrintSize? size = null, string? language = null, string? top = null, string? bottom = null)
    {
        var standard = ReceiptParts.Standard(kind);
        return standard with { Parts = change([.. standard.Parts]), ItemSize = size ?? standard.ItemSize, Language = language, TopText = top, BottomText = bottom };
    }

    private static List<LayoutPart> Hide(List<LayoutPart> parts, params string[] ids) =>
        [.. parts.Select(p => ids.Contains(p.Id) ? p with { Show = false } : p)];

    private static List<LayoutPart> Show(List<LayoutPart> parts, params string[] ids) =>
        [.. parts.Select(p => ids.Contains(p.Id) ? p with { Show = true } : p)];

    [Fact]
    public void Parts_can_be_hidden_moved_and_given_own_text()
    {
        var (order, payments) = PrintSamples.Order();
        var layout = With(ReceiptKind.Receipt,
            parts =>
            {
                var moved = Show(Hide(parts, "phone", "servedBy"), "topText", "bottomText");
                var number = moved.Single(p => p.Id == "orderNumber");
                moved.Remove(number);
                moved.Insert(0, number); // order number first
                return moved;
            },
            top: "Order online at olivegrill.co.uk",
            bottom: "10% off your next order\nShow this receipt");

        var doc = Layouts.Receipt(order, payments, PrintSamples.Shop, PrintSamples.London, "Sam", isCopy: false, layout);
        var text = doc.PlainText;

        Assert.StartsWith("Order 001\n----\nOlive Grill", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Tel: 0113", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Served by", text, StringComparison.Ordinal);
        Assert.Contains("Order online at olivegrill.co.uk", text, StringComparison.Ordinal);
        Assert.EndsWith("10% off your next order\nShow this receipt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("----\n----", text, StringComparison.Ordinal); // never two dashed lines together
    }

    [Fact]
    public void A_VAT_receipt_keeps_its_legal_parts_even_if_a_saved_layout_hides_them()
    {
        var (order, payments) = PrintSamples.Order();
        var layout = With(ReceiptKind.Receipt, parts => Hide(parts, "vatNumber", "vatSummary", "address"));

        var text = Layouts.Receipt(order, payments, PrintSamples.Shop, PrintSamples.London, null, false, layout).PlainText;

        Assert.Contains("VAT No: GB 123 4567 89", text, StringComparison.Ordinal);
        Assert.Contains("12 High Street", text, StringComparison.Ordinal);
        Assert.Contains("VAT rate | Net / VAT", text, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ReceiptParts.Checked(ReceiptKind.Receipt, layout, vatRegistered: true));
        Assert.False(ReceiptParts.Checked(ReceiptKind.Receipt, layout, vatRegistered: false).Shows("vatSummary")); // not VAT registered: may go
    }

    [Fact]
    public void Item_size_and_language_change_the_item_lines()
    {
        var (order, payments) = PrintSamples.Order();
        var big = With(ReceiptKind.Receipt, p => p, size: PrintSize.Large, language: "en");

        var doc = Layouts.Receipt(order, payments, PrintSamples.Shop, PrintSamples.London, null, false, big);

        var doner = doc.Blocks.OfType<PrintColumns>().First(c => c.Left.Contains("Doner kebab", StringComparison.Ordinal));
        Assert.Equal(PrintSize.Large, doner.Size);
        Assert.DoesNotContain("دونر كباب", doc.PlainText, StringComparison.Ordinal); // English only, though the shop prints both
    }

    [Fact]
    public void Kitchen_ticket_parts_and_huge_items()
    {
        var (order, _) = PrintSamples.Order(OrderType.Delivery);
        var layout = With(ReceiptKind.Kitchen, parts => Hide(parts, "customer", "itemCount", "dateTime"), size: PrintSize.Huge);

        var doc = Layouts.KitchenTicket(order, [.. order.Lines], KitchenTicketKind.New, PrintSamples.Shop, PrintSamples.London, layout: layout);

        Assert.DoesNotContain("Ordered", doc.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("item(s)", doc.PlainText, StringComparison.Ordinal);
        Assert.All(doc.Blocks.OfType<PrintText>().Where(t => t.Text.Contains(" x ", StringComparison.Ordinal)), t => Assert.Equal(PrintSize.Huge, t.Size));
        Assert.Throws<ArgumentException>(() => ReceiptParts.Checked(ReceiptKind.Kitchen, With(ReceiptKind.Kitchen, p => Hide(p, "items")), false));
    }

    [Fact]
    public void Checking_fills_missing_parts_and_refuses_unknown_or_bad_ones()
    {
        var shortList = ReceiptParts.Standard(ReceiptKind.Kitchen) with { Parts = [new LayoutPart("items", true), new LayoutPart("ticketType", true)] };
        var checkedLayout = ReceiptParts.Checked(ReceiptKind.Kitchen, shortList, false);
        Assert.Equal(ReceiptParts.Kitchen.Count, checkedLayout.Parts.Count);
        Assert.Equal("items", checkedLayout.Parts[0].Id);
        Assert.False(checkedLayout.Shows("orderNumber")); // added hidden

        Assert.Throws<ArgumentException>(() => ReceiptParts.Checked(ReceiptKind.Kitchen, shortList with { Parts = [.. shortList.Parts, new LayoutPart("logo", true)] }, false));
        Assert.Throws<ArgumentException>(() => ReceiptParts.Checked(ReceiptKind.Kitchen, shortList with { Parts = [.. shortList.Parts, new LayoutPart("items", true)] }, false));
        Assert.Throws<ArgumentException>(() => ReceiptParts.Checked(ReceiptKind.Kitchen, shortList with { ItemSize = PrintSize.Small }, false));
        Assert.Throws<ArgumentException>(() => ReceiptParts.Checked(ReceiptKind.Kitchen, shortList with { Language = "fr" }, false));
        Assert.Throws<ArgumentException>(() => ReceiptParts.Checked(ReceiptKind.Kitchen, shortList with { TopText = "1\n2\n3\n4\n5" }, false));
    }

    [Fact]
    public async Task The_owner_saves_layouts_and_new_printouts_use_them()
    {
        var setup = await Owner.GetJson<LayoutSetup>("/api/settings/receipt-layouts");
        Assert.Equal(ReceiptParts.StandardSet.Kitchen.ItemSize, setup.Layouts.Kitchen.ItemSize);

        var kitchen = With(ReceiptKind.Kitchen, p => Hide(p, "itemCount"), size: PrintSize.Huge, bottom: "Check allergies!");
        var saved = await (await Owner.Put("/api/settings/receipt-layouts", setup.Layouts with { Kitchen = kitchen })).Read<ReceiptLayoutSet>();
        Assert.Equal(PrintSize.Huge, saved.Kitchen.ItemSize);

        Restart();
        var again = await Owner.GetJson<LayoutSetup>("/api/settings/receipt-layouts");
        Assert.Equal("Check allergies!", again.Layouts.Kitchen.BottomText);
        Assert.False(again.Layouts.Kitchen.Shows("itemCount"));
    }

    [Fact]
    public async Task Only_the_owner_and_saving_a_VAT_receipt_without_its_VAT_parts_is_refused()
    {
        var setup = await Owner.GetJson<LayoutSetup>("/api/settings/receipt-layouts");
        Assert.Equal(HttpStatusCode.Forbidden, (await Manager.Put("/api/settings/receipt-layouts", setup.Layouts)).StatusCode);

        await Owner.Put("/api/settings/shop", new ShopDetails("Olive Grill", "12 High Street", null, "Birmingham", "B12 9QX", "0121 000 0000", "GB123456789", null));
        var refused = await Owner.Put("/api/settings/receipt-layouts", setup.Layouts with { Delivery = With(ReceiptKind.Delivery, p => Hide(p, "vatSummary")) });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.StartsWith("Delivery receipt: ", (await refused.Read<ErrorView>()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_preview_draws_the_newest_order_with_the_layout_being_edited()
    {
        var none = await Owner.Post("/api/settings/receipt-layouts/preview?kind=Receipt", ReceiptParts.Standard(ReceiptKind.Receipt));
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        await Manager.Put("/api/menu", TestMenu.State());
        await Cashier.Post("/api/orders", new CreateOrderRequest(Pos.Domain.Orders.DeviceIds.New(), Guid.NewGuid().ToString(),
            Pos.Domain.Orders.OrderSource.Counter, OrderType.Takeaway, null, null, [new(TestMenu.Doner, TestMenu.DonerRegular, 1, [TestMenu.Garlic])], null));

        var plain = await (await Owner.Post("/api/settings/receipt-layouts/preview?kind=Receipt", ReceiptParts.Standard(ReceiptKind.Receipt))).Content.ReadAsByteArrayAsync();
        var bigger = await Owner.Post("/api/settings/receipt-layouts/preview?kind=Receipt", With(ReceiptKind.Receipt, p => Show(p, "topText", "bottomText"), size: PrintSize.Huge, top: "Hello", bottom: "Bye"));
        Assert.Equal("image/png", bigger.Content.Headers.ContentType?.MediaType);
        var png = await bigger.Content.ReadAsByteArrayAsync();
        Assert.Equal(0x89, png[0]);
        Assert.True(PngHeight(png) > PngHeight(plain)); // more text, larger items: a longer receipt

        Assert.Equal(HttpStatusCode.OK, (await Owner.Post("/api/settings/receipt-layouts/preview?kind=Kitchen", ReceiptParts.Standard(ReceiptKind.Kitchen))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.Post("/api/settings/receipt-layouts/preview?kind=Kitchen", ReceiptParts.Standard(ReceiptKind.Kitchen))).StatusCode);
    }

    private static int PngHeight(byte[] png) => (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];

    private sealed record LayoutSetup(ReceiptLayoutSet Layouts, ReceiptLayoutSet Standard, IReadOnlyList<LayoutPartInfo> ReceiptParts, IReadOnlyList<LayoutPartInfo> KitchenParts, bool VatRegistered);
}
