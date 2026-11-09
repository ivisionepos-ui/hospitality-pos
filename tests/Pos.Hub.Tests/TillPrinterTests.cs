using System.Net;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Printing;

namespace Pos.Hub.Tests;

/// <summary>
/// Tills with their own receipt printer and cash drawer: receipts, drawer openings and the Z report from that till go
/// to its printer; other tills use the shop receipt printer.
/// </summary>
public sealed class TillPrinterTests : HubTestBase, IDisposable
{
    private static readonly byte[] Kick = [0x1B, 0x40, 0x1B, 0x70, 0x00, 0x19, 0xFA];
    private readonly FakeNetworkPrinter _shopPrinter = new();
    private readonly FakeNetworkPrinter _till2Printer = new();
    private HttpClient _till2 = null!;
    private Security.PairedDevice _till2Device = null!;
    private Guid _till2Id;

    public void Dispose()
    {
        _shopPrinter.Dispose();
        _till2Printer.Dispose();
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        var code = await (await Owner.Post("/api/devices/pairing-code", new { })).Read<PairingCodeView>();
        var till2 = await (await Anonymous.Post("/api/devices/pair", new PairRequest(code.Code, "Till 2", Security.DeviceType.Till))).Read<Security.PairedDevice>();
        _till2Device = till2;
        _till2Id = till2.DeviceId;
        _till2 = ClientFor(await Host.Login(till2, TestShop.CashierPin));
        await Manager.Put("/api/menu", TestMenu.State());
    }

    private static PrinterChoice Network(string address, int pin = 2) => new(PrintOutputType.EscPosNetwork, null, address, 80, true, pin);

    private static IReadOnlyList<KitchenPrinterChoice> OneKitchen => [new KitchenPrinterChoice(KitchenStation.MainId, "Kitchen", new PrinterChoice(PrintOutputType.Pdf, null, null, 80, true, 2), KitchenPrintMode.Everything, [], [])];

    private PrinterSetup Setup(params TillPrinterChoice[] tills) => new(Network(_shopPrinter.Address), OneKitchen, true, tills);

    private Task<HttpResponseMessage> GiveTill2ItsOwnPrinter(int pin = 2) =>
        Owner.Put("/api/settings/printers", Setup(new TillPrinterChoice(_till2Id, Network(_till2Printer.Address, pin))));

    private static async Task<OrderView> Order(HttpClient till)
    {
        await till.Post("/api/shifts/open", new OpenShiftRequest(5000));
        return await (await till.Post("/api/orders", new CreateOrderRequest(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, OrderType.Takeaway, null, null,
            [new OrderLineRequest(TestMenu.Can, TestMenu.CanRegular)]))).Read<OrderView>();
    }

    private static bool IsReceipt(byte[] b) => b.Length > 1000 && b.AsSpan(b.Length - 7).SequenceEqual(EscPos.FeedAndCut);

    [Fact]
    public async Task Cash_on_a_till_with_its_own_printer_opens_its_drawer_and_prints_its_receipt()
    {
        Assert.Equal(HttpStatusCode.OK, (await GiveTill2ItsOwnPrinter()).StatusCode);

        var order = await Order(_till2);
        await _till2.Post($"/api/orders/{order.Id}/payments/cash", new CashPaymentRequest(500));

        var received = await _till2Printer.WaitFor(2);
        Assert.Equal(Kick, received[0]);
        Assert.True(IsReceipt(received[1]));
        await Task.Delay(300);
        Assert.Empty(_shopPrinter.Received); // nothing on the shop's printer
    }

    [Fact]
    public async Task A_till_without_its_own_printer_uses_the_shop_receipt_printer()
    {
        await GiveTill2ItsOwnPrinter();

        var order = await Order(Cashier); // Till 1
        await Cashier.Post($"/api/orders/{order.Id}/payments/cash", new CashPaymentRequest(500));

        var received = await _shopPrinter.WaitFor(2);
        Assert.Equal(Kick, received[0]);
        Assert.True(IsReceipt(received[1]));
        await Task.Delay(300);
        Assert.Empty(_till2Printer.Received);
    }

    [Fact]
    public async Task Reprints_no_sale_paid_out_and_the_z_report_go_to_the_till_that_asked()
    {
        await GiveTill2ItsOwnPrinter(pin: 5);
        var order = await Order(_till2);
        await _till2.Post($"/api/orders/{order.Id}/payments/card", new CardPaymentRequest(120));
        await _till2Printer.WaitFor(1); // the receipt when paid

        (await _till2.Post($"/api/orders/{order.Id}/print-receipt", new { })).EnsureSuccessStatusCode();
        await _till2Printer.WaitFor(2);

        var managerOnTill2 = ClientFor(await Host.Login(_till2Device, TestShop.ManagerPin));
        var noSale = new HttpRequestMessage(HttpMethod.Post, "/api/drawer/open") { Content = JsonContent(new NoSaleRequest("Change for a £20")) };
        noSale.Headers.Add("X-Manager-Pin", TestShop.ManagerPin);
        (await _till2.SendAsync(noSale)).EnsureSuccessStatusCode();
        var received = await _till2Printer.WaitFor(3);
        Assert.Equal([0x1B, 0x40, 0x1B, 0x70, 0x01, 0x19, 0xFA], received[2]); // pin 5, as set for this till

        (await managerOnTill2.Post("/api/shifts/paid-out", new CashMovementRequest(300, "Milk"))).EnsureSuccessStatusCode();
        await _till2Printer.WaitFor(4);

        await Task.Delay(300);
        Assert.Empty(_shopPrinter.Received);
    }

    [Fact]
    public async Task Till_printers_are_kept_after_a_restart_and_tests_use_the_tills_printer()
    {
        await GiveTill2ItsOwnPrinter();
        Restart();

        var setup = await Owner.GetJson<PrintersResponse>("/api/settings/printers");
        Assert.Equal(_till2Id, Assert.Single(setup.Setup.Tills!).DeviceId);

        (await Owner.Post("/api/settings/printers/test", new TestPrintRequest(PrintTarget.Receipt, TillId: _till2Id))).EnsureSuccessStatusCode();
        (await Owner.Post("/api/settings/printers/test-drawer", new TestDrawerRequest(_till2Id))).EnsureSuccessStatusCode();
        var received = await _till2Printer.WaitFor(2);
        Assert.Equal(Kick, received[1]);

        (await Owner.Post("/api/settings/printers/test-drawer", new TestDrawerRequest())).EnsureSuccessStatusCode();
        Assert.Equal(Kick, (await _shopPrinter.WaitFor(1))[0]); // no till: the shop's drawer
    }

    [Fact]
    public async Task Mistakes_are_refused_and_removing_a_till_printer_goes_back_to_the_shop_printer()
    {
        var twice = await Owner.Put("/api/settings/printers", Setup(
            new TillPrinterChoice(_till2Id, Network(_till2Printer.Address)), new TillPrinterChoice(_till2Id, Network(_till2Printer.Address))));
        Assert.Equal("A till is listed twice.", (await twice.Read<ErrorView>()).Message);

        var noName = await Owner.Put("/api/settings/printers", Setup(new TillPrinterChoice(_till2Id, new PrinterChoice(PrintOutputType.EscPosWindows, " ", null, 80, true, 2))));
        Assert.Contains("printer's name as Windows shows it", (await noName.Read<ErrorView>()).Message, StringComparison.Ordinal);

        await GiveTill2ItsOwnPrinter();
        (await Owner.Put("/api/settings/printers", Setup())).EnsureSuccessStatusCode();
        var order = await Order(_till2);
        await _till2.Post($"/api/orders/{order.Id}/payments/cash", new CashPaymentRequest(500));
        await _shopPrinter.WaitFor(2);
        Assert.Empty(_till2Printer.Received);
    }

    [Fact]
    public async Task Saved_printers_from_before_till_printers_still_load()
    {
        // As 0.18.0 saved them: no "tills".
        (await Owner.Put("/api/settings/printers", new { receipt = Network(_shopPrinter.Address), kitchens = OneKitchen, openDrawerOnCash = true })).EnsureSuccessStatusCode();
        Restart();
        var setup = await Owner.GetJson<PrintersResponse>("/api/settings/printers");
        Assert.Empty(setup.Setup.Tills ?? []);
        Assert.Equal(PrintOutputType.EscPosNetwork, setup.Setup.Receipt.Output);
    }

    private static StringContent JsonContent(object value) =>
        new(System.Text.Json.JsonSerializer.Serialize(value, PosJson.Options), System.Text.Encoding.UTF8, "application/json");

    private sealed record PrintersResponse(PrinterSetup Setup, IReadOnlyList<string> Installed);
}
