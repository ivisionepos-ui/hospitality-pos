using System.Net;
using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Data;

namespace Pos.Hub.Tests;

/// <summary>End-to-end tests: real HTTP calls to a real hub with a real SQLite file.</summary>
public sealed class HubApiTests : IDisposable
{
    private static readonly Guid Cashier = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Manager = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Till1 = Guid.Parse("30000000-0000-0000-0000-000000000001");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pos-hub-tests", Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero));
    private HubTestHost _host;
    private HttpClient _client;

    public HubApiTests()
    {
        Directory.CreateDirectory(_folder);
        _host = new HubTestHost(DbPath, _clock);
        _client = _host.CreateClient();
    }

    private string DbPath => Path.Combine(_folder, "hub.db");

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Temp files; leftovers are harmless.
        }
    }

    /// <summary>Stops the hub and starts it again on the same database file, like a PC restart.</summary>
    private void Restart()
    {
        _client.Dispose();
        _host.Dispose();
        _host = new HubTestHost(DbPath, _clock);
        _client = _host.CreateClient();
    }

    private async Task ImportMenu()
    {
        var response = await _client.Put("/api/menu", new ImportMenuRequest(TestMenu.Owner, TestMenu.State()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static OrderLineRequest DonerLine => new(TestMenu.Doner, TestMenu.DonerRegular, 1, [TestMenu.Garlic]);

    private static OrderLineRequest CanLine => new(TestMenu.Can, TestMenu.CanRegular, 2);

    private static CreateOrderRequest NewOrder(params OrderLineRequest[] lines) =>
        new(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, OrderType.Takeaway, Cashier, Till1, null, null, lines);

    [Fact]
    public async Task Health_check_answers()
    {
        var response = await _client.GetAsync(new Uri("/api/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("TEST1", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Menu_starts_empty_and_can_be_imported()
    {
        var empty = await _client.GetJson<MenuState>("/api/menu");
        Assert.Empty(empty.Products);

        await ImportMenu();
        var menu = await _client.GetJson<MenuState>("/api/menu");

        Assert.Equal(4, menu.Products.Count);
        Assert.Equal("بيتزا", menu.Categories[0].Name.Ar);
        Assert.True(menu.Version > 0);
    }

    [Fact]
    public async Task Order_is_created_priced_and_numbered()
    {
        await ImportMenu();

        var response = await _client.Post("/api/orders", NewOrder(DonerLine, CanLine));
        var order = await response.Read<OrderView>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("001", order.Number);
        Assert.Equal("TEST1-261105-0001", order.Reference);
        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Equal(Money.Gbp(899 + 240), order.Total);
        Assert.Equal(Money.Gbp(190), Assert.Single(order.Vat).Vat); // £11.39 at 20% = 189.83p
        Assert.Equal("Garlic", Assert.Single(order.Lines[0].Extras).En);
    }

    [Fact]
    public async Task Sending_the_same_order_twice_stores_it_once()
    {
        await ImportMenu();
        var request = NewOrder(DonerLine);

        var first = await _client.Post("/api/orders", request);
        var retry = await _client.Post("/api/orders", request);

        var firstOrder = await first.Read<OrderView>();
        var retryOrder = await retry.Read<OrderView>();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.False(firstOrder.Duplicate);
        Assert.True(retryOrder.Duplicate);
        Assert.Equal(firstOrder.Id, retryOrder.Id);
        Assert.Single(await _client.GetJson<List<OrderView>>("/api/orders"));
    }

    [Fact]
    public async Task Reusing_an_order_key_for_different_items_is_refused()
    {
        await ImportMenu();
        var request = NewOrder(DonerLine);
        await _client.Post("/api/orders", request);

        var response = await _client.Post("/api/orders", request with { Lines = [DonerLine, CanLine] });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("DuplicateKey", (await response.Read<ErrorView>()).Code);
    }

    [Fact]
    public async Task Invalid_choices_are_refused_with_reasons_and_use_no_order_number()
    {
        await ImportMenu();

        var response = await _client.Post("/api/orders", NewOrder(CanLine, new OrderLineRequest(TestMenu.Doner, TestMenu.DonerRegular)));
        var error = await response.Read<ErrorView>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("ExtraMissing", Assert.Single(error.Problems!).Code);
        Assert.StartsWith("Line 2:", error.Problems![0].Message, StringComparison.Ordinal);

        var next = await (await _client.Post("/api/orders", NewOrder(CanLine))).Read<OrderView>();
        Assert.Equal("001", next.Number);
    }

    [Fact]
    public async Task Meal_deal_order_through_the_api()
    {
        await ImportMenu();
        var deal = new OrderLineRequest(TestMenu.Deal, TestMenu.DealRegular, 1, Choices:
        [
            new DealChoiceRequest(TestMenu.PizzaSlot, TestMenu.Margherita, TestMenu.Margherita12),
            new DealChoiceRequest(TestMenu.KebabSlot, TestMenu.Doner, TestMenu.DonerRegular, [TestMenu.Garlic]),
            new DealChoiceRequest(TestMenu.DrinkSlot, TestMenu.Can, TestMenu.CanRegular),
        ], Note: "Cut in 8");

        var order = await (await _client.Post("/api/orders", NewOrder(deal))).Read<OrderView>();

        Assert.Equal(Money.Gbp(1999), order.Total);
        Assert.Equal(["Margherita", "Doner kebab", "Can"], order.Lines[0].DealItems.Select(i => i.Name.En));
        Assert.Equal("Cut in 8", order.Lines[0].Note);
    }

    [Fact]
    public async Task Status_flow_and_refused_moves()
    {
        await ImportMenu();
        var order = await (await _client.Post("/api/orders", NewOrder(DonerLine))).Read<OrderView>();
        var url = $"/api/orders/{order.Id}/status";

        Assert.Equal(OrderStatus.Preparing, (await (await _client.Post(url, new StatusRequest(OrderStatus.Preparing, Cashier))).Read<OrderView>()).Status);
        Assert.Equal(OrderStatus.Ready, (await (await _client.Post(url, new StatusRequest(OrderStatus.Ready, Cashier))).Read<OrderView>()).Status);

        var backwards = await _client.Post(url, new StatusRequest(OrderStatus.Preparing, Cashier));
        Assert.Equal(HttpStatusCode.Conflict, backwards.StatusCode);

        var noReason = await _client.Post(url, new StatusRequest(OrderStatus.Cancelled, Cashier));
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        Assert.Equal(OrderStatus.Completed, (await (await _client.Post(url, new StatusRequest(OrderStatus.Completed, Cashier))).Read<OrderView>()).Status);

        var history = await _client.GetJson<List<OrderEvent>>($"/api/orders/{order.Id}/history");
        Assert.Equal(3, history.Count(e => e.Kind == OrderEventKind.StatusChanged));
    }

    [Fact]
    public async Task Kitchen_void_needs_a_manager_and_refused_change_is_not_saved()
    {
        await ImportMenu();
        var order = await (await _client.Post("/api/orders", NewOrder(DonerLine, CanLine))).Read<OrderView>();
        await _client.Post($"/api/orders/{order.Id}/send-to-kitchen", new ActorRequest(Cashier, Till1));
        var doner = order.Lines[0].Id;

        var refused = await _client.Post($"/api/orders/{order.Id}/lines/{doner}/void", new VoidRequest(Cashier, "Wrong item"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.False((await _client.GetJson<OrderView>($"/api/orders/{order.Id}")).Lines[0].IsVoided);

        var approved = await _client.Post($"/api/orders/{order.Id}/lines/{doner}/void", new VoidRequest(Cashier, "Wrong item", Till1, Manager));
        var after = await approved.Read<OrderView>();
        Assert.True(after.Lines[0].IsVoided);
        Assert.Equal(Money.Gbp(240), after.Total);
    }

    [Fact]
    public async Task Items_can_be_added_to_an_open_order_and_only_new_ones_go_to_the_kitchen()
    {
        await ImportMenu();
        var order = await (await _client.Post("/api/orders", NewOrder(DonerLine) with { Source = OrderSource.Table, Type = OrderType.EatIn })).Read<OrderView>();
        await _client.Post($"/api/orders/{order.Id}/send-to-kitchen", new ActorRequest(Cashier));

        var added = await (await _client.Post($"/api/orders/{order.Id}/lines", new AddLinesRequest(Cashier, Till1, [CanLine]))).Read<OrderView>();
        var sent = await (await _client.Post($"/api/orders/{order.Id}/send-to-kitchen", new ActorRequest(Cashier))).Content.ReadAsStringAsync();

        Assert.Equal(Money.Gbp(899 + 240), added.Total);
        Assert.Contains("Can", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("Doner", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Price_change_applies_to_new_orders_only_and_can_be_undone()
    {
        await ImportMenu();
        var before = await (await _client.Post("/api/orders", NewOrder(CanLine))).Read<OrderView>();

        var change = await (await _client.Post($"/api/menu/products/{TestMenu.Can}/price", new PriceRequest(TestMenu.CanRegular, 150, Manager))).Read<MenuChangeView>();
        Assert.Contains("£1.20 → £1.50", change.Summary, StringComparison.Ordinal);

        var after = await (await _client.Post("/api/orders", NewOrder(CanLine))).Read<OrderView>();
        Assert.Equal(Money.Gbp(300), after.Total);
        Assert.Equal(Money.Gbp(240), (await _client.GetJson<OrderView>($"/api/orders/{before.Id}")).Total);

        await _client.Post("/api/menu/undo", new ActorRequest(Manager));
        var menu = await _client.GetJson<MenuState>("/api/menu");
        Assert.Equal(Money.Gbp(120), menu.Products.Single(p => p.Id == TestMenu.Can).Variants[0].Price);

        var log = await _client.GetJson<List<MenuChangeRecord>>($"/api/menu/changes?since={change.Version - 1}");
        Assert.Equal(["Updated", "Undo"], log.Select(l => l.Kind));
    }

    [Fact]
    public async Task Sold_out_item_cannot_be_ordered()
    {
        await ImportMenu();
        await _client.Post($"/api/menu/products/{TestMenu.Can}/sold-out", new SoldOutRequest(true, Manager));

        var response = await _client.Post("/api/orders", NewOrder(CanLine));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("SoldOut", (await response.Read<ErrorView>()).Problems![0].Code);
    }

    [Fact]
    public async Task Unknown_order_gives_not_found()
    {
        var response = await _client.GetAsync(new Uri($"/api/orders/{Guid.NewGuid()}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Everything_survives_a_restart()
    {
        await ImportMenu();
        await _client.Post($"/api/menu/products/{TestMenu.Can}/rename", new RenameRequest(new LocalizedText("Can 330ml", "علبة"), Manager));
        var first = await (await _client.Post("/api/orders", NewOrder(DonerLine, CanLine))).Read<OrderView>();
        await _client.Post("/api/orders", NewOrder(CanLine));
        await _client.Post($"/api/orders/{first.Id}/status", new StatusRequest(OrderStatus.Ready, Cashier));
        var menuVersion = (await _client.GetJson<MenuState>("/api/menu")).Version;

        Restart();

        var reloaded = await _client.GetJson<OrderView>($"/api/orders/{first.Id}");
        Assert.Equal(OrderStatus.Ready, reloaded.Status);
        Assert.Equal(first.Total, reloaded.Total);
        Assert.Equal("001", reloaded.Number);

        var menu = await _client.GetJson<MenuState>("/api/menu");
        Assert.Equal(menuVersion, menu.Version);
        Assert.Equal("علبة", menu.Products.Single(p => p.Id == TestMenu.Can).Name.Ar);

        var third = await (await _client.Post("/api/orders", NewOrder(CanLine))).Read<OrderView>();
        Assert.Equal("003", third.Number); // numbering carries on

        Assert.Equal(3, (await _client.GetJson<List<OrderView>>("/api/orders")).Count);
    }

    [Fact]
    public async Task Order_list_follows_the_trading_day()
    {
        await ImportMenu();
        await _client.Post("/api/orders", NewOrder(CanLine));

        _clock.Now = new DateTimeOffset(2026, 11, 6, 1, 30, 0, TimeSpan.Zero); // after midnight, before 05:00
        await _client.Post("/api/orders", NewOrder(CanLine));
        Assert.Equal(2, (await _client.GetJson<List<OrderView>>("/api/orders")).Count);

        _clock.Now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        var today = await (await _client.Post("/api/orders", NewOrder(CanLine))).Read<OrderView>();
        Assert.Equal("001", today.Number);
        Assert.Single(await _client.GetJson<List<OrderView>>("/api/orders"));
        Assert.Equal(2, (await _client.GetJson<List<OrderView>>("/api/orders?date=2026-11-05")).Count);
        Assert.Empty(await _client.GetJson<List<OrderView>>("/api/orders?date=2026-11-05&status=Ready"));
    }

    [Fact]
    public void Database_is_created_with_the_current_schema()
    {
        using var db = new HubDatabase(Path.Combine(_folder, "fresh.db"));

        Assert.Equal(1, db.SchemaVersion);
        Assert.Equal("wal", db.Scalar("PRAGMA journal_mode;"));
    }
}
