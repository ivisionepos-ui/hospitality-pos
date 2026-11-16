using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pos.Domain.Common;
using Pos.Hub.Api;
using Pos.Hub.Live;
using Pos.Hub.Security;

namespace Pos.Hub.Tests;

/// <summary>Customer displays: they show their till's order at once, and can do nothing else.</summary>
public sealed class CustomerDisplayTests : HubTestBase
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly List<HubConnection> _connections = [];

    public override async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private async Task<Credentials> PairDevice(string name, DeviceType type)
    {
        var code = await (await Owner.Post("/api/devices/pairing-code", new { })).Read<PairingCodeView>();
        var device = await (await Anonymous.Post("/api/devices/pair", new PairRequest(code.Code, name, type))).Read<PairedDevice>();
        return new Credentials(device.DeviceId, device.DeviceKey, null);
    }

    private static DisplayState Order(params (int Qty, string Name, string Price)[] lines) =>
        new("order", null, "Takeaway", [.. lines.Select(l => new DisplayLine(l.Qty, l.Name, [], l.Price))], [new DisplayTotal("Total", "£9.00", "total")], null, null);

    private async Task Link(Credentials display, Guid tillId, string? welcome = null, string[]? messages = null)
    {
        var response = await Owner.Put("/api/settings/customer-displays", new SaveCustomerDisplaysRequest(welcome, messages ?? [], [new CustomerDisplayLink(display.DeviceId, tillId)]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Channel<DisplayState?>> Listen(Credentials device)
    {
        var ticket = await (await ClientFor(device).Post("/api/live/ticket", new { })).Read<LiveTicket>();
        var server = Host.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/live"), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.AccessTokenProvider = () => Task.FromResult<string?>(ticket.Ticket);
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.WebSocketFactory = async (context, cancel) =>
                {
                    var separator = string.IsNullOrEmpty(context.Uri.Query) ? "?" : "&";
                    var uri = new Uri($"{context.Uri}{separator}access_token={Uri.EscapeDataString(ticket.Ticket)}");
                    return await server.CreateWebSocketClient().ConnectAsync(uri, cancel);
                };
            })
            .AddJsonProtocol(o => PosJson.Configure(o.PayloadSerializerOptions))
            .Build();
        var states = Channel.CreateUnbounded<DisplayState?>();
        connection.On<DisplayState?>(CustomerDisplayService.StateMessage, s => states.Writer.TryWrite(s));
        connection.On<DisplayState?>(CustomerDisplayService.ReloadMessage, _ => states.Writer.TryWrite(null));
        await connection.StartAsync();
        _connections.Add(connection);
        return states;
    }

    private static async Task<DisplayState?> Next(Channel<DisplayState?> states)
    {
        using var timeout = new CancellationTokenSource(Wait);
        return await states.Reader.ReadAsync(timeout.Token);
    }

    [Fact]
    public async Task A_new_display_shows_the_shop_and_waits_to_be_linked_when_there_are_several_tills()
    {
        var display = await PairDevice("Counter display", DeviceType.CustomerDisplay);

        var view = await ClientFor(display).GetJson<CustomerDisplayView>("/api/display");

        Assert.Null(view.TillName); // the office PC and Till 1 can both take orders: the owner chooses
        Assert.Equal("idle", view.State.Stage);
        Assert.StartsWith("Welcome to ", view.Welcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_display_shows_what_its_till_sends_and_nothing_from_other_tills()
    {
        var display = await PairDevice("Counter display", DeviceType.CustomerDisplay);
        await Link(display, Shop.Till.DeviceId, "Welcome to Ali's", ["Try our mixed grill", "Free delivery over £20"]);

        await Owner.Post("/api/display/state", Order((1, "Office order", "£1.00"))); // another till (the office PC)
        var quiet = await ClientFor(display).GetJson<CustomerDisplayView>("/api/display");
        Assert.Equal("idle", quiet.State.Stage);
        Assert.Equal("Till 1", quiet.TillName);
        Assert.Equal("Welcome to Ali's", quiet.Welcome);
        Assert.Equal(["Try our mixed grill", "Free delivery over £20"], quiet.Messages);

        var response = await Cashier.Post("/api/display/state", Order((2, "Large Doner kebab", "£15.00")));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var view = await ClientFor(display).GetJson<CustomerDisplayView>("/api/display");
        Assert.Equal("order", view.State.Stage);
        Assert.Equal("Large Doner kebab", Assert.Single(view.State.Lines!).Name);
        Assert.Equal("£9.00", view.State.Totals![0].Value);
    }

    [Fact]
    public async Task Changes_reach_the_display_live()
    {
        var display = await PairDevice("Counter display", DeviceType.CustomerDisplay);
        await Link(display, Shop.Till.DeviceId);
        var states = await Listen(display);

        await Cashier.Post("/api/display/state", Order((1, "Chips", "£2.50")));
        Assert.Equal("Chips", (await Next(states))!.Lines![0].Name);

        await Cashier.Post("/api/display/state", new DisplayState("thanks", "015", "Takeaway", [], [], null, "£7.50"));
        var thanks = (await Next(states))!;
        Assert.Equal("thanks", thanks.Stage);
        Assert.Equal("£7.50", thanks.Change);
        Assert.Equal("015", thanks.OrderNumber);

        // The owner's changes make the display fetch its settings again.
        await Link(display, Shop.Till.DeviceId, "Hello!");
        Assert.Null(await Next(states));
    }

    [Fact]
    public async Task A_display_cannot_read_orders_customers_or_the_menu_or_take_a_log_in()
    {
        var display = ClientFor(await PairDevice("Counter display", DeviceType.CustomerDisplay));

        foreach (var url in new[] { "/api/orders", "/api/menu", "/api/customers?phone=07700900123", $"/api/orders/{Guid.NewGuid()}" })
        {
            var response = await display.GetAsync(new Uri(url, UriKind.Relative));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("DisplayOnly", (await response.Read<ErrorView>()).Code);
        }

        var login = await display.Post("/api/auth/login", new LoginRequest(TestShop.CashierPin));
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        var send = await display.Post("/api/display/state", Order((1, "Fake", "£0.01")));
        Assert.Equal(HttpStatusCode.Forbidden, send.StatusCode);
    }

    [Fact]
    public async Task Only_tills_send_and_only_displays_read()
    {
        var kitchenScreen = await PairDevice("Kitchen 1", DeviceType.KitchenScreen);
        var kitchenLogin = await Host.Login(new PairedDevice(kitchenScreen.DeviceId, kitchenScreen.DeviceKey), TestShop.ManagerPin);

        var fromKitchen = await ClientFor(kitchenLogin).Post("/api/display/state", Order((1, "Chips", "£2.50")));
        Assert.Equal(HttpStatusCode.Forbidden, fromKitchen.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.GetAsync(new Uri("/api/display", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Kitchen.Post("/api/display/state", Order((1, "Chips", "£2.50")))).StatusCode); // kitchen role cannot take orders
    }

    [Fact]
    public async Task What_a_till_sends_is_checked_and_trimmed()
    {
        var display = await PairDevice("Counter display", DeviceType.CustomerDisplay);
        await Link(display, Shop.Till.DeviceId);

        var bad = await Cashier.Post("/api/display/state", new DisplayState("party", null, null, [], [], null, null));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var many = Enumerable.Range(1, 250).Select(i => new DisplayLine(1, $"Item {i} {new string('x', 300)}", null, "£1.00")).ToList();
        await Cashier.Post("/api/display/state", new DisplayState("order", null, null, many, null, null, null));
        var view = await ClientFor(display).GetJson<CustomerDisplayView>("/api/display");
        Assert.Equal(DisplayState.MaxLines, view.State.Lines!.Count);
        Assert.StartsWith("Item 250 ", view.State.Lines[^1].Name, StringComparison.Ordinal); // the newest lines are kept
        Assert.Equal(120, view.State.Lines[^1].Name.Length);
    }

    [Fact]
    public async Task Owner_settings_are_checked_and_need_the_owner()
    {
        var display = await PairDevice("Counter display", DeviceType.CustomerDisplay);
        var kitchenScreen = await PairDevice("Kitchen 1", DeviceType.KitchenScreen);

        async Task<string> Refused(HttpClient who, SaveCustomerDisplaysRequest request)
        {
            var response = await who.Put("/api/settings/customer-displays", request);
            Assert.False(response.IsSuccessStatusCode);
            return (await response.Read<ErrorView>()).Message;
        }

        Assert.Contains("follow a till", await Refused(Owner, new(null, [], [new(display.DeviceId, kitchenScreen.DeviceId)])), StringComparison.Ordinal);
        Assert.Contains("not a customer display", await Refused(Owner, new(null, [], [new(Shop.Till.DeviceId, Shop.Till.DeviceId)])), StringComparison.Ordinal);
        Assert.Contains("At most 5", await Refused(Owner, new(null, ["1", "2", "3", "4", "5", "6"], [])), StringComparison.Ordinal);
        Assert.Contains("too long", await Refused(Owner, new(new string('W', 81), [], [])), StringComparison.Ordinal);
        await Refused(Cashier, new("Hi", [], []));

        var setup = await (await Owner.Put("/api/settings/customer-displays", new SaveCustomerDisplaysRequest("  Hi  ", ["  Fresh daily ", " "], [new(display.DeviceId, Shop.Till.DeviceId)]))).Read<CustomerDisplaysSetup>();
        Assert.Equal("Hi", setup.Welcome);
        Assert.Equal(["Fresh daily"], setup.Messages);
        Assert.Equal(Shop.Till.DeviceId, Assert.Single(setup.Displays).TillId);
        Assert.Contains(setup.Tills, t => t.Id == Shop.Till.DeviceId);
        Assert.DoesNotContain(setup.Tills, t => t.Id == kitchenScreen.DeviceId);
    }

    [Fact]
    public async Task Settings_survive_a_restart_but_the_screen_contents_do_not()
    {
        var display = await PairDevice("Counter display", DeviceType.CustomerDisplay);
        await Link(display, Shop.Till.DeviceId, "Welcome back");
        await Cashier.Post("/api/display/state", Order((1, "Chips", "£2.50")));

        Restart();

        var view = await ClientFor(display).GetJson<CustomerDisplayView>("/api/display");
        Assert.Equal("Welcome back", view.Welcome);
        Assert.Equal("idle", view.State.Stage); // what was on the till is not kept
    }
}
