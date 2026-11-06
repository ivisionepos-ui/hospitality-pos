using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Live;
using Pos.Hub.Printing;

namespace Pos.Hub.Tests;

/// <summary>A stand-in for a network thermal printer: records everything sent to it, one entry per connection.</summary>
internal sealed class FakeNetworkPrinter : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<byte[]> _received = [];
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;

    public FakeNetworkPrinter()
    {
        _listener.Start();
        _ = Task.Run(Accept);
    }

    public string Address => $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    public IReadOnlyList<byte[]> Received
    {
        get
        {
            lock (_received)
            {
                return [.. _received];
            }
        }
    }

    /// <summary>Waits until at least <paramref name="count"/> printouts have arrived.</summary>
    public async Task<IReadOnlyList<byte[]>> WaitFor(int count)
    {
        for (var i = 0; i < 100 && Received.Count < count; i++)
        {
            await Task.Delay(50);
        }

        Assert.True(Received.Count >= count, $"Expected {count} printouts, got {Received.Count}.");
        return Received;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    private async Task Accept()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var data = new MemoryStream();
                await client.GetStream().CopyToAsync(data, _stop.Token);
                lock (_received)
                {
                    _received.Add(data.ToArray());
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
        }
    }
}

/// <summary>ESC/POS commands, exactly.</summary>
public sealed class EscPosTests
{
    [Fact]
    public void A_printout_is_reset_picture_then_cut()
    {
        var image = new MonoImage(2, 3, [0xFF, 0x00, 0x80, 0x01, 0x00, 0x00]);

        var bytes = EscPos.Printout(image, cut: true);

        Assert.Equal(
            [0x1B, 0x40, 0x1D, 0x76, 0x30, 0x00, 0x02, 0x00, 0x03, 0x00, 0xFF, 0x00, 0x80, 0x01, 0x00, 0x00, 0x1B, 0x64, 0x04, 0x1D, 0x56, 0x42, 0x00],
            bytes);
        Assert.Equal([0x1B, 0x64, 0x06], EscPos.Printout(image, cut: false)[^3..]); // no cutter: feed to tear off
    }

    [Fact]
    public void Long_printouts_are_sent_in_bands_the_printer_can_hold()
    {
        var image = new MonoImage(72, 300, new byte[72 * 300]);

        var bytes = EscPos.Printout(image, cut: true);

        var headers = Enumerable.Range(0, bytes.Length - 3).Where(i => bytes[i] == 0x1D && bytes[i + 1] == 0x76 && bytes[i + 2] == 0x30).ToList();
        Assert.Equal(2, headers.Count);
        Assert.Equal((255, 45), (bytes[headers[0] + 6], bytes[headers[1] + 6])); // 255 rows, then the last 45
        Assert.Equal(2 + (2 * 8) + (72 * 300) + 7, bytes.Length);
    }

    [Fact]
    public void The_drawer_kick_uses_the_chosen_socket()
    {
        Assert.Equal([0x1B, 0x70, 0x00, 0x19, 0xFA], EscPos.DrawerKick(2));
        Assert.Equal([0x1B, 0x70, 0x01, 0x19, 0xFA], EscPos.DrawerKick(5));
    }

    [Fact]
    public void A_star_tsp100_printout_is_raster_lines_then_feed_and_cut()
    {
        var image = new MonoImage(2, 2, [0xFF, 0x00, 0x0F, 0xF0]);

        var bytes = StarGraphic.Printout(image, cut: true);

        Assert.Equal(
            [
                0x1B, 0x2A, 0x72, 0x52,                   // ESC * r R  reset raster settings
                0x1B, 0x2A, 0x72, 0x41,                   // ESC * r A  enter raster mode
                0x1B, 0x2A, 0x72, 0x50, 0x30, 0x00,       // ESC * r P "0" NUL  continuous paper
                0x1B, 0x2A, 0x72, 0x45, 0x31, 0x33, 0x00, // ESC * r E "13" NUL  feed, cut feed, partial cut
                0x62, 0x02, 0x00, 0xFF, 0x00,             // b n1 n2 row 1
                0x62, 0x02, 0x00, 0x0F, 0xF0,             // b n1 n2 row 2
                0x1B, 0x0C, 0x04,                         // ESC FF EOT  end of document
                0x1B, 0x2A, 0x72, 0x42,                   // ESC * r B  leave raster mode
            ],
            bytes);
        Assert.Equal([0x1B, 0x2A, 0x72, 0x45, 0x32, 0x00], StarGraphic.EndMode(cut: false)); // "2": feed to the tear bar
        Assert.Equal([0x07], StarGraphic.DrawerKick);
    }

    [Theory]
    [InlineData("192.168.1.50", "192.168.1.50", 9100)]
    [InlineData(" 192.168.1.50:9101 ", "192.168.1.50", 9101)]
    [InlineData("kitchen-printer", "kitchen-printer", 9100)]
    public void Printer_addresses_are_understood(string text, string host, int port)
    {
        Assert.Equal((host, port), NetworkPrinting.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.50:99999")]
    [InlineData("http://192.168.1.50")]
    [InlineData("a b")]
    public void Wrong_addresses_are_refused(string text)
    {
        Assert.Throws<ArgumentException>(() => NetworkPrinting.Parse(text));
    }

    [Fact]
    public void Printouts_are_drawn_in_black_and_white_at_the_printer_width()
    {
        using var renderer = new Renderer(Path.Combine(AppContext.BaseDirectory, "Printing/Fonts/DejaVuSans.ttf"), Path.Combine(AppContext.BaseDirectory, "Printing/Fonts/DejaVuSans-Bold.ttf"));

        var image = renderer.RenderMono(PrintTest.SampleKitchenTicket(), 576);

        Assert.Equal(72, image.WidthBytes);
        Assert.True(image.Height > 100);
        Assert.Contains(image.Bits, b => b != 0); // there is ink
        Assert.Equal(72 * image.Height, image.Bits.Length);
    }
}

/// <summary>Thermal printers on the network, the cash drawer, "No sale", and the Printers screen.</summary>
public sealed class ThermalPrinterTests : HubTestBase, IDisposable
{
    private static readonly byte[] Kick = [0x1B, 0x40, 0x1B, 0x70, 0x00, 0x19, 0xFA];
    private readonly FakeNetworkPrinter _printer = new();

    public void Dispose() => _printer.Dispose();

    private static PrinterChoice Pdf => new(PrintOutputType.Pdf, null, null, 80, true, 2);

    /// <summary>One kitchen printer, printing everything.</summary>
    private static IReadOnlyList<KitchenPrinterChoice> OneKitchen(PrinterChoice printer) =>
        [new KitchenPrinterChoice(KitchenStation.MainId, "Kitchen", printer, KitchenPrintMode.Everything, [], [])];

    private Task<HttpResponseMessage> UseNetworkReceiptPrinter(string? address = null, bool openDrawerOnCash = true) =>
        Owner.Put("/api/settings/printers", new PrinterSetup(new PrinterChoice(PrintOutputType.EscPosNetwork, null, address ?? _printer.Address, 80, true, 2), OneKitchen(Pdf), openDrawerOnCash));

    private async Task<OrderView> Order()
    {
        await Manager.Put("/api/menu", TestMenu.State());
        await Cashier.Post("/api/shifts/open", new OpenShiftRequest(5000));
        return await (await Cashier.Post("/api/orders", new CreateOrderRequest(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, OrderType.Takeaway, null, null,
            [new OrderLineRequest(TestMenu.Can, TestMenu.CanRegular)]))).Read<OrderView>();
    }

    private static bool IsReceipt(byte[] b) =>
        b.Length > 1000 && b.AsSpan(0, 6).SequenceEqual(new byte[] { 0x1B, 0x40, 0x1D, 0x76, 0x30, 0x00 }) && b[6] == 72 && b.AsSpan(b.Length - 7).SequenceEqual(EscPos.FeedAndCut);

    [Fact]
    public async Task Cash_opens_the_drawer_then_the_receipt_prints_and_is_cut()
    {
        Assert.Equal(HttpStatusCode.OK, (await UseNetworkReceiptPrinter()).StatusCode);
        var order = await Order();

        await Cashier.Post($"/api/orders/{order.Id}/payments/cash", new CashPaymentRequest(500));

        var received = await _printer.WaitFor(2);
        Assert.Equal(Kick, received[0]);
        Assert.True(IsReceipt(received[1]));
    }

    [Fact]
    public async Task Card_payments_do_not_open_the_drawer()
    {
        await UseNetworkReceiptPrinter();
        var order = await Order();

        await Cashier.Post($"/api/orders/{order.Id}/payments/card", new CardPaymentRequest(120));

        var received = await _printer.WaitFor(1);
        await Task.Delay(300);
        Assert.Single(_printer.Received);
        Assert.True(IsReceipt(received[0]));
    }

    [Fact]
    public async Task The_owner_can_turn_off_opening_the_drawer_for_cash()
    {
        await UseNetworkReceiptPrinter(openDrawerOnCash: false);
        var order = await Order();

        await Cashier.Post($"/api/orders/{order.Id}/payments/cash", new CashPaymentRequest(120));

        await _printer.WaitFor(1);
        await Task.Delay(300);
        Assert.True(IsReceipt(Assert.Single(_printer.Received)));
    }

    [Fact]
    public async Task A_star_tsp100_gets_star_graphic_commands_and_its_drawer_opens_with_bel()
    {
        var star = new PrinterChoice(PrintOutputType.EscPosNetwork, null, _printer.Address, 80, true, 2, PrinterCommands.StarGraphic);
        Assert.Equal(HttpStatusCode.OK, (await Owner.Put("/api/settings/printers", new PrinterSetup(star, OneKitchen(Pdf), true))).StatusCode);
        var order = await Order();

        await Cashier.Post($"/api/orders/{order.Id}/payments/cash", new CashPaymentRequest(500));

        var received = await _printer.WaitFor(2);
        Assert.Equal([0x07], received[0]);
        var receipt = received[1];
        Assert.True(receipt.AsSpan(0, 14).SequenceEqual(StarGraphic.Start));
        Assert.True(receipt.AsSpan(14, 7).SequenceEqual(StarGraphic.EndMode(cut: true)));
        Assert.Equal((0x62, 72, 0), (receipt[21], receipt[22], receipt[23])); // 72 bytes (576 dots) per line
        Assert.True(receipt.AsSpan(receipt.Length - 7).SequenceEqual(StarGraphic.End));
        Assert.Equal(0, (receipt.Length - 21 - 7) % 75); // whole lines only

        var pin5 = await Owner.Put("/api/settings/printers", new PrinterSetup(star with { DrawerPin = 5 }, OneKitchen(Pdf), true));
        Assert.Equal("Star TSP100 printers open the drawer on their one drawer socket: choose pin 2.", (await pin5.Read<ErrorView>()).Message);
    }

    [Fact]
    public async Task No_sale_needs_a_manager_and_is_recorded()
    {
        await UseNetworkReceiptPrinter();

        var alone = await Cashier.Post("/api/drawer/open", new NoSaleRequest("Change for a £20"));
        Assert.Equal(HttpStatusCode.Forbidden, alone.StatusCode);
        Assert.Equal("ApprovalRequired", (await alone.Read<ErrorView>()).Code);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/drawer/open") { Content = JsonContent.Create(new NoSaleRequest("Change for a £20"), options: PosJson.Options) };
        request.Headers.Add("X-Manager-Pin", TestShop.ManagerPin);
        Assert.Equal(HttpStatusCode.NoContent, (await Cashier.SendAsync(request)).StatusCode);

        Assert.Equal(Kick, (await _printer.WaitFor(1))[0]);
        var opens = await Manager.GetJson<List<DrawerOpen>>("/api/drawer/opens");
        Assert.Equal(("Sam Cashier", "Mia Manager", "Change for a £20"), (opens[0].Staff, opens[0].ApprovedBy, opens[0].Reason));
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.GetAsync(new Uri("/api/drawer/opens", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task No_sale_without_a_thermal_receipt_printer_is_explained_and_not_recorded()
    {
        var response = await Manager.Post("/api/drawer/open", new NoSaleRequest("Float check"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("More → Printers", (await response.Read<ErrorView>()).Message, StringComparison.Ordinal);
        Assert.Empty(await Manager.GetJson<List<DrawerOpen>>("/api/drawer/opens"));
    }

    [Fact]
    public async Task Paid_out_opens_the_drawer()
    {
        await UseNetworkReceiptPrinter();
        await Order();

        await Manager.Post("/api/shifts/paid-out", new CashMovementRequest(500, "Milk"));

        Assert.Equal(Kick, (await _printer.WaitFor(1))[0]);
    }

    [Fact]
    public async Task A_printer_that_cannot_be_reached_is_retried_then_reported_and_the_drawer_once()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop(); // nothing listens there now
        await UseNetworkReceiptPrinter($"127.0.0.1:{port}");
        var order = await Order();

        await Cashier.Post($"/api/orders/{order.Id}/payments/cash", new CashPaymentRequest(120));

        PrintJobView[] failed = [];
        for (var i = 0; i < 100 && failed.Length < 2; i++)
        {
            await Task.Delay(100);
            failed = [.. await Cashier.GetJson<List<PrintJobView>>("/api/print-jobs?status=Failed")];
        }

        var drawer = failed.Single(j => j.Job.Kind == PrintJobKind.DrawerKick);
        var receipt = failed.Single(j => j.Job.Kind == PrintJobKind.Receipt);
        Assert.Equal(1, drawer.Attempts);  // never opened late
        Assert.Equal(2, receipt.Attempts); // tried again first
        Assert.Equal($"Cannot reach the printer at 127.0.0.1:{port}. Check it is switched on, has paper, and is on the shop network.", receipt.LastError);
        var events = await TillOnly.GetJson<CatchUpResult>("/api/live/events?since=0");
        Assert.Contains(events.Events, e => e.Summary == $"Could not open the cash drawer: {drawer.LastError}");
    }

    [Fact]
    public async Task The_printers_screen_saves_checks_tests_and_keeps_choices_after_a_restart()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Manager.Put("/api/settings/printers", new PrinterSetup(Pdf, OneKitchen(Pdf), true))).StatusCode);

        var badAddress = await Owner.Put("/api/settings/printers", new PrinterSetup(new PrinterChoice(PrintOutputType.EscPosNetwork, null, "http://printer", 80, true, 2), OneKitchen(Pdf), true));
        Assert.Equal("\"http://printer\" is not a printer address. Use e.g. 192.168.1.50 or 192.168.1.50:9100.", (await badAddress.Read<ErrorView>()).Message);
        var noName = await Owner.Put("/api/settings/printers", new PrinterSetup(Pdf, OneKitchen(new PrinterChoice(PrintOutputType.EscPosWindows, " ", null, 80, true, 2)), true));
        Assert.Equal("Choose the Kitchen printer's name as Windows shows it.", (await noName.Read<ErrorView>()).Message);
        Assert.Equal(HttpStatusCode.BadRequest, (await Owner.Put("/api/settings/printers", new PrinterSetup(Pdf with { PaperWidthMm = 76 }, OneKitchen(Pdf), true))).StatusCode);

        // Test drawer before a thermal printer is chosen: explained.
        Assert.Equal(HttpStatusCode.Conflict, (await Owner.Post("/api/settings/printers/test-drawer", new { })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await UseNetworkReceiptPrinter()).StatusCode);
        var test = await Owner.Post("/api/settings/printers/test", new TestPrintRequest(PrintTarget.Receipt));
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        Assert.True(IsReceipt((await _printer.WaitFor(1))[0]));
        Assert.Equal(HttpStatusCode.NoContent, (await Owner.Post("/api/settings/printers/test-drawer", new { })).StatusCode);
        Assert.Equal(Kick, (await _printer.WaitFor(2))[1]);

        Restart();
        var after = await Owner.GetJson<PrintersView>("/api/settings/printers");
        Assert.Equal((PrintOutputType.EscPosNetwork, _printer.Address), (after.Setup.Receipt.Output, after.Setup.Receipt.Address));
    }

    private sealed record PrintersView(PrinterSetup Setup, IReadOnlyList<string> Installed);
}
