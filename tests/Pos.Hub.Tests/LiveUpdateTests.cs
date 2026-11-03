using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Live;
using Pos.Hub.Security;

namespace Pos.Hub.Tests;

/// <summary>Live updates: real SignalR connections to the hub, as tills and screens would make.</summary>
public sealed class LiveUpdateTests : HubTestBase
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly List<HubConnection> _connections = [];

    /// <summary>A live connection with the events it has received.</summary>
    private sealed record Listener(HubConnection Connection, Channel<LiveEvent> Events)
    {
        public async Task<LiveEvent> Next()
        {
            using var timeout = new CancellationTokenSource(Wait);
            return await Events.Reader.ReadAsync(timeout.Token);
        }

        public async Task<LiveEvent> NextOfKind(string kind)
        {
            while (true)
            {
                var e = await Next();
                if (e.Kind == kind)
                {
                    return e;
                }
            }
        }

        /// <summary>True if nothing arrives within a short time.</summary>
        public async Task<bool> StaysQuiet()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                await Events.Reader.ReadAsync(timeout.Token);
                return false;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        }
    }

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

    private async Task<Listener> Listen(Credentials device)
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

        var events = Channel.CreateUnbounded<LiveEvent>();
        connection.On<LiveEvent>("event", e => events.Writer.TryWrite(e));
        await connection.StartAsync();
        _connections.Add(connection);
        return new Listener(connection, events);
    }

    private static CreateOrderRequest Order(OrderSource source = OrderSource.Counter) =>
        new(DeviceIds.New(), Guid.NewGuid().ToString(), source, OrderType.Takeaway, null, null,
            [new OrderLineRequest(TestMenu.Doner, TestMenu.DonerRegular, 1, [TestMenu.Garlic])]);

    [Fact]
    public async Task New_order_reaches_the_till_and_the_kitchen_at_once()
    {
        await Manager.Put("/api/menu", TestMenu.State());
        var kitchenScreen = await PairDevice("Kitchen 1", DeviceType.KitchenScreen);
        var till = await Listen(Shop.Till);
        var kitchen = await Listen(kitchenScreen);

        var order = await (await Cashier.Post("/api/orders", Order())).Read<OrderView>();

        var atTill = await till.NextOfKind(LiveEventKinds.OrderCreated);
        var atKitchen = await kitchen.NextOfKind(LiveEventKinds.OrderCreated);
        Assert.Equal(order.Id, atTill.Order!.Id);
        Assert.Equal(order.Id, atKitchen.Order!.Id);
        Assert.Equal("Doner kebab", atKitchen.Order.Lines[0].Name.En);
        Assert.Equal(atTill.Seq, atKitchen.Seq);
    }

    [Fact]
    public async Task Online_order_reaches_the_kitchen_only_once_accepted()
    {
        await Manager.Put("/api/menu", TestMenu.State());
        var till = await Listen(Shop.Till);
        var kitchen = await Listen(await PairDevice("Kitchen 1", DeviceType.KitchenScreen));

        var order = await (await Cashier.Post("/api/orders", Order(OrderSource.Website))).Read<OrderView>();
        Assert.Equal(OrderStatus.Received, (await till.NextOfKind(LiveEventKinds.OrderCreated)).Order!.Status);
        Assert.True(await kitchen.StaysQuiet());

        await Cashier.Post($"/api/orders/{order.Id}/status", new StatusRequest(OrderStatus.Accepted));
        var accepted = await kitchen.Next();
        Assert.Equal(LiveEventKinds.OrderUpdated, accepted.Kind);
        Assert.Equal(OrderStatus.Accepted, accepted.Order!.Status);
    }

    [Fact]
    public async Task Sold_out_reaches_every_screen_but_displays_get_no_orders()
    {
        await Manager.Put("/api/menu", TestMenu.State());
        var till = await Listen(Shop.Till);
        var kitchen = await Listen(await PairDevice("Kitchen 1", DeviceType.KitchenScreen));
        var display = await Listen(await PairDevice("Customer display", DeviceType.CustomerDisplay));

        await Cashier.Post($"/api/menu/products/{TestMenu.Can}/sold-out", new SoldOutRequest(true));

        foreach (var screen in new[] { till, kitchen, display })
        {
            var e = await screen.Next();
            Assert.Equal(LiveEventKinds.MenuChanged, e.Kind);
            Assert.Contains("sold out", e.Summary, StringComparison.Ordinal);
            Assert.NotNull(e.MenuVersion);
        }

        await Cashier.Post("/api/orders", Order());
        await till.NextOfKind(LiveEventKinds.OrderCreated);
        Assert.True(await display.StaysQuiet());
    }

    [Fact]
    public async Task Event_numbers_go_up_by_one()
    {
        await Manager.Put("/api/menu", TestMenu.State());
        var till = await Listen(Shop.Till);

        var order = await (await Cashier.Post("/api/orders", Order())).Read<OrderView>();
        await Cashier.Post($"/api/orders/{order.Id}/send-to-kitchen", new { });
        await Kitchen.Post($"/api/orders/{order.Id}/status", new StatusRequest(OrderStatus.Ready));

        var seqs = new[] { (await till.Next()).Seq, (await till.Next()).Seq, (await till.Next()).Seq };
        Assert.Equal([seqs[0], seqs[0] + 1, seqs[0] + 2], seqs);
    }

    [Fact]
    public async Task Screen_that_was_away_catches_up_in_order()
    {
        await Manager.Put("/api/menu", TestMenu.State());
        var before = (await (await TillOnly.Post("/api/live/ticket", new { })).Read<LiveTicket>()).LastSeq;

        var order = await (await Cashier.Post("/api/orders", Order())).Read<OrderView>();
        await Cashier.Post($"/api/orders/{order.Id}/send-to-kitchen", new { });
        await Manager.Post($"/api/menu/products/{TestMenu.Can}/price", new PriceRequest(TestMenu.CanRegular, 130));

        var missed = await TillOnly.GetJson<CatchUpResult>($"/api/live/events?since={before}");

        Assert.Equal([LiveEventKinds.OrderCreated, LiveEventKinds.OrderUpdated, LiveEventKinds.MenuChanged], missed.Events.Select(e => e.Kind));
        Assert.Equal([before + 1, before + 2, before + 3], missed.Events.Select(e => e.Seq));
        Assert.True(missed.Events[1].Order!.Lines[0].SentToKitchen);
        Assert.Equal(before + 3, missed.LastSeq);
        Assert.False(missed.More);
    }

    [Fact]
    public async Task Kitchen_catch_up_leaves_out_orders_not_yet_accepted()
    {
        await Manager.Put("/api/menu", TestMenu.State());
        var kitchenScreen = ClientFor(await PairDevice("Kitchen 1", DeviceType.KitchenScreen));
        var before = (await (await kitchenScreen.Post("/api/live/ticket", new { })).Read<LiveTicket>()).LastSeq;

        await Cashier.Post("/api/orders", Order(OrderSource.Website)); // waits to be accepted
        await Cashier.Post("/api/orders", Order());                    // counter order, accepted

        var missed = await kitchenScreen.GetJson<CatchUpResult>($"/api/live/events?since={before}");

        var only = Assert.Single(missed.Events);
        Assert.Equal(OrderStatus.Accepted, only.Order!.Status);
    }

    [Fact]
    public async Task Refused_changes_and_repeated_orders_send_no_events()
    {
        await Manager.Put("/api/menu", TestMenu.State());
        var request = Order();
        var order = await (await Cashier.Post("/api/orders", request)).Read<OrderView>();
        await Cashier.Post($"/api/orders/{order.Id}/send-to-kitchen", new { });
        var last = (await (await TillOnly.Post("/api/live/ticket", new { })).Read<LiveTicket>()).LastSeq;

        await Cashier.Post("/api/orders", request);                                                         // duplicate
        await Cashier.Post($"/api/orders/{order.Id}/lines/{order.Lines[0].Id}/void", new VoidRequest("x")); // needs a manager
        await Cashier.Post($"/api/orders/{order.Id}/status", new StatusRequest(OrderStatus.Pending));      // not allowed

        var after = await TillOnly.GetJson<CatchUpResult>($"/api/live/events?since={last}");
        Assert.Empty(after.Events);
        Assert.Equal(last, after.LastSeq);
    }

    [Fact]
    public async Task Events_are_kept_after_a_restart()
    {
        await Manager.Put("/api/menu", TestMenu.State());
        await Cashier.Post("/api/orders", Order());

        Restart();

        var all = await TillOnly.GetJson<CatchUpResult>("/api/live/events?since=0");
        Assert.Equal([LiveEventKinds.MenuChanged, LiveEventKinds.OrderCreated], all.Events.Select(e => e.Kind));
    }

    [Fact]
    public async Task Live_connection_needs_a_valid_ticket()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous.Post("/api/live/ticket", new { })).StatusCode);

        var noTicket = await Anonymous.PostAsync(new Uri("/live/negotiate?negotiateVersion=1", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Unauthorized, noTicket.StatusCode);

        var ticket = await (await TillOnly.Post("/api/live/ticket", new { })).Read<LiveTicket>();
        var good = await Anonymous.PostAsync(new Uri($"/live/negotiate?negotiateVersion=1&access_token={ticket.Ticket}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);

        Clock.Now += TimeSpan.FromSeconds(61);
        var expired = await Anonymous.PostAsync(new Uri($"/live/negotiate?negotiateVersion=1&access_token={ticket.Ticket}", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task Removed_device_is_disconnected()
    {
        var kitchenScreen = await PairDevice("Kitchen 1", DeviceType.KitchenScreen);
        var kitchen = await Listen(kitchenScreen);
        var closed = new TaskCompletionSource();
        kitchen.Connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        await Owner.DeleteAsync(new Uri($"/api/devices/{kitchenScreen.DeviceId}", UriKind.Relative));

        await closed.Task.WaitAsync(Wait);
        Assert.Equal(HubConnectionState.Disconnected, kitchen.Connection.State);
    }

    [Fact]
    public void Live_service_is_registered_once()
    {
        Assert.Same(Host.Services.GetRequiredService<LiveUpdates>(), Host.Services.GetRequiredService<LiveUpdates>());
    }
}
