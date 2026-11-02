using System.Net;
using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Data;

namespace Pos.Hub.Tests;

/// <summary>End-to-end tests: real HTTP calls to a real hub with a real SQLite file, as logged-in staff.</summary>
public sealed class HubApiTests : HubTestBase
{
    private async Task ImportMenu()
    {
        var response = await Manager.Put("/api/menu", TestMenu.State());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static OrderLineRequest DonerLine => new(TestMenu.Doner, TestMenu.DonerRegular, 1, [TestMenu.Garlic]);

    private static OrderLineRequest CanLine => new(TestMenu.Can, TestMenu.CanRegular, 2);

    private static CreateOrderRequest NewOrder(params OrderLineRequest[] lines) =>
        new(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, OrderType.Takeaway, null, null, lines);

    private static StatusRequest To(OrderStatus status, string? reason = null) => new(status, reason);

    [Fact]
    public async Task Health_check_answers_without_login()
    {
        var response = await Anonymous.GetAsync(new Uri("/api/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("TEST1", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Menu_starts_empty_and_can_be_imported()
    {
        var empty = await TillOnly.GetJson<MenuState>("/api/menu");
        Assert.Empty(empty.Products);

        await ImportMenu();
        var menu = await TillOnly.GetJson<MenuState>("/api/menu");

        Assert.Equal(4, menu.Products.Count);
        Assert.Equal("بيتزا", menu.Categories[0].Name.Ar);
        Assert.True(menu.Version > 0);
    }

    [Fact]
    public async Task Order_is_created_priced_numbered_and_recorded_against_the_logged_in_cashier()
    {
        await ImportMenu();

        var response = await Cashier.Post("/api/orders", NewOrder(DonerLine, CanLine));
        var order = await response.Read<OrderView>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("001", order.Number);
        Assert.Equal("TEST1-261105-0001", order.Reference);
        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Equal(Money.Gbp(899 + 240), order.Total);
        Assert.Equal(Money.Gbp(190), Assert.Single(order.Vat).Vat); // £11.39 at 20% = 189.83p
        Assert.Equal("Garlic", Assert.Single(order.Lines[0].Extras).En);

        var history = await Manager.GetJson<List<OrderEvent>>($"/api/orders/{order.Id}/history");
        Assert.All(history, e => Assert.Equal(Shop.CashierId, e.UserId));
        Assert.All(history, e => Assert.Equal(Shop.Till.DeviceId, e.DeviceId));
    }

    [Fact]
    public async Task Sending_the_same_order_twice_stores_it_once()
    {
        await ImportMenu();
        var request = NewOrder(DonerLine);

        var first = await Cashier.Post("/api/orders", request);
        var retry = await Cashier.Post("/api/orders", request);
        var firstOrder = await first.Read<OrderView>();
        var retryOrder = await retry.Read<OrderView>();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.False(firstOrder.Duplicate);
        Assert.True(retryOrder.Duplicate);
        Assert.Equal(firstOrder.Id, retryOrder.Id);
        Assert.Single(await TillOnly.GetJson<List<OrderView>>("/api/orders"));
    }

    [Fact]
    public async Task Reusing_an_order_key_for_different_items_is_refused()
    {
        await ImportMenu();
        var request = NewOrder(DonerLine);
        await Cashier.Post("/api/orders", request);

        var response = await Cashier.Post("/api/orders", request with { Lines = [DonerLine, CanLine] });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("DuplicateKey", (await response.Read<ErrorView>()).Code);
    }

    [Fact]
    public async Task Invalid_choices_are_refused_with_reasons_and_use_no_order_number()
    {
        await ImportMenu();

        var response = await Cashier.Post("/api/orders", NewOrder(CanLine, new OrderLineRequest(TestMenu.Doner, TestMenu.DonerRegular)));
        var error = await response.Read<ErrorView>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("ExtraMissing", Assert.Single(error.Problems!).Code);
        Assert.StartsWith("Line 2:", error.Problems![0].Message, StringComparison.Ordinal);

        var next = await (await Cashier.Post("/api/orders", NewOrder(CanLine))).Read<OrderView>();
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

        var order = await (await Cashier.Post("/api/orders", NewOrder(deal))).Read<OrderView>();

        Assert.Equal(Money.Gbp(1999), order.Total);
        Assert.Equal(["Margherita", "Doner kebab", "Can"], order.Lines[0].DealItems.Select(i => i.Name.En));
        Assert.Equal("Cut in 8", order.Lines[0].Note);
    }

    [Fact]
    public async Task Kitchen_marks_preparing_and_ready_and_the_cashier_completes()
    {
        await ImportMenu();
        var order = await (await Cashier.Post("/api/orders", NewOrder(DonerLine))).Read<OrderView>();
        var url = $"/api/orders/{order.Id}/status";

        Assert.Equal(OrderStatus.Preparing, (await (await Kitchen.Post(url, To(OrderStatus.Preparing))).Read<OrderView>()).Status);
        Assert.Equal(OrderStatus.Ready, (await (await Kitchen.Post(url, To(OrderStatus.Ready))).Read<OrderView>()).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Cashier.Post(url, To(OrderStatus.Preparing))).StatusCode); // no going back
        Assert.Equal(HttpStatusCode.Forbidden, (await Kitchen.Post(url, To(OrderStatus.Completed))).StatusCode); // kitchen cannot hand over
        Assert.Equal(OrderStatus.Completed, (await (await Cashier.Post(url, To(OrderStatus.Completed))).Read<OrderView>()).Status);

        var history = await Manager.GetJson<List<OrderEvent>>($"/api/orders/{order.Id}/history");
        Assert.Equal(3, history.Count(e => e.Kind == OrderEventKind.StatusChanged));
    }

    [Fact]
    public async Task Kitchen_void_needs_a_manager_pin_and_a_refused_change_is_not_saved()
    {
        await ImportMenu();
        var order = await (await Cashier.Post("/api/orders", NewOrder(DonerLine, CanLine))).Read<OrderView>();
        await Cashier.Post($"/api/orders/{order.Id}/send-to-kitchen", new { });
        var url = $"/api/orders/{order.Id}/lines/{order.Lines[0].Id}/void";

        var refused = await Cashier.Post(url, new VoidRequest("Wrong item"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("ApprovalRequired", (await refused.Read<ErrorView>()).Code);
        Assert.False((await TillOnly.GetJson<OrderView>($"/api/orders/{order.Id}")).Lines[0].IsVoided);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = System.Net.Http.Json.JsonContent.Create(new VoidRequest("Wrong item"), options: PosJson.Options) };
        request.Headers.Add("X-Manager-Pin", TestShop.ManagerPin);
        var approved = await (await Cashier.SendAsync(request)).Read<OrderView>();

        Assert.True(approved.Lines[0].IsVoided);
        Assert.Equal(Money.Gbp(240), approved.Total);
        var voidEvent = (await Manager.GetJson<List<OrderEvent>>($"/api/orders/{order.Id}/history")).Last();
        Assert.Equal(Shop.CashierId, voidEvent.UserId);
        Assert.Equal(Shop.ManagerId, voidEvent.ApprovedByUserId);
    }

    [Fact]
    public async Task Cashier_can_remove_an_item_before_it_goes_to_the_kitchen()
    {
        await ImportMenu();
        var order = await (await Cashier.Post("/api/orders", NewOrder(DonerLine, CanLine))).Read<OrderView>();

        var after = await (await Cashier.Post($"/api/orders/{order.Id}/lines/{order.Lines[1].Id}/void", new VoidRequest("Changed mind"))).Read<OrderView>();

        Assert.True(after.Lines[1].IsVoided);
        Assert.Equal(Money.Gbp(899), after.Total);
    }

    [Fact]
    public async Task Cancelling_needs_a_manager_and_a_reason()
    {
        await ImportMenu();
        var order = await (await Cashier.Post("/api/orders", NewOrder(DonerLine))).Read<OrderView>();
        var url = $"/api/orders/{order.Id}/status";

        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.Post(url, To(OrderStatus.Cancelled, "Customer left"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Manager.Post(url, To(OrderStatus.Cancelled))).StatusCode);
        Assert.Equal(OrderStatus.Cancelled, (await (await Manager.Post(url, To(OrderStatus.Cancelled, "Customer left"))).Read<OrderView>()).Status);
    }

    [Fact]
    public async Task Items_can_be_added_to_an_open_order_and_only_new_ones_go_to_the_kitchen()
    {
        await ImportMenu();
        var order = await (await Cashier.Post("/api/orders", NewOrder(DonerLine) with { Source = OrderSource.Table, Type = OrderType.EatIn })).Read<OrderView>();
        await Cashier.Post($"/api/orders/{order.Id}/send-to-kitchen", new { });

        var added = await (await Cashier.Post($"/api/orders/{order.Id}/lines", new AddLinesRequest([CanLine]))).Read<OrderView>();
        var sent = await (await Cashier.Post($"/api/orders/{order.Id}/send-to-kitchen", new { })).Content.ReadAsStringAsync();

        Assert.Equal(Money.Gbp(899 + 240), added.Total);
        Assert.Contains("Can", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("Doner", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Price_change_applies_to_new_orders_only_and_can_be_undone()
    {
        await ImportMenu();
        var before = await (await Cashier.Post("/api/orders", NewOrder(CanLine))).Read<OrderView>();

        var change = await (await Manager.Post($"/api/menu/products/{TestMenu.Can}/price", new PriceRequest(TestMenu.CanRegular, 150))).Read<MenuChangeView>();
        Assert.Contains("£1.20 → £1.50", change.Summary, StringComparison.Ordinal);

        var after = await (await Cashier.Post("/api/orders", NewOrder(CanLine))).Read<OrderView>();
        Assert.Equal(Money.Gbp(300), after.Total);
        Assert.Equal(Money.Gbp(240), (await TillOnly.GetJson<OrderView>($"/api/orders/{before.Id}")).Total);

        await Manager.Post("/api/menu/undo", new { });
        var menu = await TillOnly.GetJson<MenuState>("/api/menu");
        Assert.Equal(Money.Gbp(120), menu.Products.Single(p => p.Id == TestMenu.Can).Variants[0].Price);

        var log = await TillOnly.GetJson<List<MenuChangeRecord>>($"/api/menu/changes?since={change.Version - 1}");
        Assert.Equal(["Updated", "Undo"], log.Select(l => l.Kind));
        Assert.All(log, l => Assert.Equal(Shop.ManagerId, l.UserId));
    }

    [Fact]
    public async Task Cashier_can_mark_sold_out_but_not_change_prices()
    {
        await ImportMenu();

        Assert.Equal(HttpStatusCode.OK, (await Cashier.Post($"/api/menu/products/{TestMenu.Can}/sold-out", new SoldOutRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.Post($"/api/menu/products/{TestMenu.Can}/price", new PriceRequest(TestMenu.CanRegular, 1))).StatusCode);

        var response = await Cashier.Post("/api/orders", NewOrder(CanLine));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("SoldOut", (await response.Read<ErrorView>()).Problems![0].Code);
    }

    [Fact]
    public async Task Unknown_order_gives_not_found()
    {
        var response = await TillOnly.GetAsync(new Uri($"/api/orders/{Guid.NewGuid()}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Everything_survives_a_restart_including_logins()
    {
        await ImportMenu();
        await Manager.Post($"/api/menu/products/{TestMenu.Can}/rename", new RenameRequest(new LocalizedText("Can 330ml", "علبة")));
        var first = await (await Cashier.Post("/api/orders", NewOrder(DonerLine, CanLine))).Read<OrderView>();
        await Cashier.Post("/api/orders", NewOrder(CanLine));
        await Cashier.Post($"/api/orders/{first.Id}/status", To(OrderStatus.Ready));
        var menuVersion = (await TillOnly.GetJson<MenuState>("/api/menu")).Version;

        Restart();

        var reloaded = await TillOnly.GetJson<OrderView>($"/api/orders/{first.Id}");
        Assert.Equal(OrderStatus.Ready, reloaded.Status);
        Assert.Equal(first.Total, reloaded.Total);
        Assert.Equal("001", reloaded.Number);

        var menu = await TillOnly.GetJson<MenuState>("/api/menu");
        Assert.Equal(menuVersion, menu.Version);
        Assert.Equal("علبة", menu.Products.Single(p => p.Id == TestMenu.Can).Name.Ar);

        var third = await (await Cashier.Post("/api/orders", NewOrder(CanLine))).Read<OrderView>();
        Assert.Equal("003", third.Number); // numbering carries on, and the cashier is still logged in

        Assert.Equal(3, (await TillOnly.GetJson<List<OrderView>>("/api/orders")).Count);
    }

    [Fact]
    public async Task Order_list_follows_the_trading_day()
    {
        await ImportMenu();
        await Cashier.Post("/api/orders", NewOrder(CanLine));

        Clock.Now = new DateTimeOffset(2026, 11, 6, 1, 30, 0, TimeSpan.Zero); // after midnight, before 05:00
        await Cashier.Post("/api/orders", NewOrder(CanLine));
        Assert.Equal(2, (await TillOnly.GetJson<List<OrderView>>("/api/orders")).Count);

        Clock.Now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Cashier.Post("/api/orders", NewOrder(CanLine))).StatusCode); // yesterday's login has expired
        using var nextShift = ClientFor(await Host.Login(new Security.PairedDevice(Shop.Till.DeviceId, Shop.Till.DeviceKey), TestShop.CashierPin));
        var today = await (await nextShift.Post("/api/orders", NewOrder(CanLine))).Read<OrderView>();
        Assert.Equal("001", today.Number);
        Assert.Single(await TillOnly.GetJson<List<OrderView>>("/api/orders"));
        Assert.Equal(2, (await TillOnly.GetJson<List<OrderView>>("/api/orders?date=2026-11-05")).Count);
        Assert.Empty(await TillOnly.GetJson<List<OrderView>>("/api/orders?date=2026-11-05&status=Ready"));
    }

    [Fact]
    public void Database_is_created_with_the_current_schema()
    {
        using var db = new HubDatabase(Path.Combine(Path.GetDirectoryName(DbPath)!, "fresh.db"));

        Assert.Equal(2, db.SchemaVersion);
        Assert.Equal("wal", db.Scalar("PRAGMA journal_mode;"));
    }
}
