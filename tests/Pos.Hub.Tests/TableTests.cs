using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Printing;

namespace Pos.Hub.Tests;

/// <summary>Eat-in orders with no tables, or with tables: the setting, the table on the order, moving, printing.</summary>
public sealed class TableTests : HubTestBase
{
    private static OrderLineRequest Pizza => new(TestMenu.Margherita, TestMenu.Margherita12); // £11.99

    private static CreateOrderRequest EatIn(string? table, OrderType type = OrderType.EatIn) =>
        new(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, type, null, null, [Pizza], Table: table);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        (await Owner.Put("/api/menu", TestMenu.State())).EnsureSuccessStatusCode();
    }

    private async Task UseTables(int count) =>
        (await Owner.Put("/api/settings/tables", new SaveTablesRequest(true, count))).EnsureSuccessStatusCode();

    private static async Task<string> Problem(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Read<ErrorView>()).Message;
    }

    [Fact]
    public async Task A_new_shop_has_no_tables_and_eat_in_needs_no_table()
    {
        var settings = await Cashier.GetJson<TableSettings>("/api/settings/tables");
        Assert.False(settings.UseTables);

        var order = await (await Cashier.Post("/api/orders", EatIn(null))).Read<OrderView>();
        Assert.Null(order.Table);
        Assert.Contains("does not use tables", await Problem(await Cashier.Post("/api/orders", EatIn("5"))), StringComparison.Ordinal);
        Assert.False((await Cashier.GetJson<TablesView>("/api/tables")).UseTables);
    }

    [Fact]
    public async Task With_tables_an_eat_in_order_needs_one_of_the_shops_tables()
    {
        await UseTables(12);
        Assert.Equal(TableSettings.Numbered(12), (await Cashier.GetJson<TableSettings>("/api/settings/tables")).Tables);

        Assert.Contains("Choose the table", await Problem(await Cashier.Post("/api/orders", EatIn(null))), StringComparison.Ordinal);
        Assert.Contains("no table \"13\"", await Problem(await Cashier.Post("/api/orders", EatIn("13"))), StringComparison.Ordinal);
        Assert.Contains("Only eat-in orders", await Problem(await Cashier.Post("/api/orders", EatIn("5", OrderType.Takeaway))), StringComparison.Ordinal);

        var order = await (await Cashier.Post("/api/orders", EatIn(" 5 "))).Read<OrderView>();
        Assert.Equal("5", order.Table);

        // Takeaway is not affected.
        Assert.Null((await (await Cashier.Post("/api/orders", EatIn(null, OrderType.Takeaway))).Read<OrderView>()).Table);

        // The till's table picker shows table 5 in use, with the order.
        var tables = await Cashier.GetJson<TablesView>("/api/tables");
        Assert.True(tables.UseTables);
        Assert.Equal(12, tables.Tables.Count);
        var five = tables.Tables.Single(t => t.Name == "5");
        Assert.Equal((order.Id, order.Number), (five.OrderId, five.OrderNumber));
        Assert.Equal(Money.Gbp(1199), five.Total);
        Assert.All(tables.Tables.Where(t => t.Name != "5"), t => Assert.Null(t.OrderId));

        // Once it is finished the table is free again.
        (await Manager.Post($"/api/orders/{order.Id}/status", new StatusRequest(OrderStatus.Cancelled, "Customer left"))).EnsureSuccessStatusCode();
        Assert.All((await Cashier.GetJson<TablesView>("/api/tables")).Tables, t => Assert.Null(t.OrderId));
    }

    [Fact]
    public async Task Tables_can_have_names_and_mistakes_are_refused()
    {
        var saved = await (await Owner.Put("/api/settings/tables", new SaveTablesRequest(true, Tables: [" 1", "2 ", "", "Window 1"]))).Read<TableSettings>();
        Assert.Equal(["1", "2", "Window 1"], saved.Tables);
        Assert.Equal("Window 1", (await (await Cashier.Post("/api/orders", EatIn("window 1"))).Read<OrderView>()).Table); // the shop's spelling

        Assert.Contains("listed twice", await Problem(await Owner.Put("/api/settings/tables", new SaveTablesRequest(true, Tables: ["Window 1", "window 1"]))), StringComparison.Ordinal);
        Assert.Contains("number of tables must be 1 to 200", await Problem(await Owner.Put("/api/settings/tables", new SaveTablesRequest(true, 0))), StringComparison.Ordinal);
        Assert.Contains("longer than 20", await Problem(await Owner.Put("/api/settings/tables", new SaveTablesRequest(true, Tables: [new string('x', 21)]))), StringComparison.Ordinal);

        // Switching tables off and on again keeps the list.
        (await Owner.Put("/api/settings/tables", new SaveTablesRequest(false))).EnsureSuccessStatusCode();
        var back = await (await Owner.Put("/api/settings/tables", new SaveTablesRequest(true))).Read<TableSettings>();
        Assert.Equal(["1", "2", "Window 1"], back.Tables);
    }

    [Fact]
    public async Task Only_the_owner_changes_the_setting_like_the_other_shop_settings()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.Put("/api/settings/tables", new SaveTablesRequest(true, 5))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Manager.Put("/api/settings/tables", new SaveTablesRequest(true, 5))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Owner.Put("/api/settings/tables", new SaveTablesRequest(true, 5))).StatusCode);
    }

    [Fact]
    public async Task An_order_can_move_table_and_the_move_is_in_its_history()
    {
        await UseTables(10);
        var order = await (await Cashier.Post("/api/orders", EatIn("3"))).Read<OrderView>();

        var moved = await (await Cashier.Post($"/api/orders/{order.Id}/table", new MoveTableRequest("7"))).Read<OrderView>();
        Assert.Equal("7", moved.Table);
        Assert.Contains("no table \"11\"", await Problem(await Cashier.Post($"/api/orders/{order.Id}/table", new MoveTableRequest("11"))), StringComparison.Ordinal);

        var history = Host.Services.GetRequiredService<OrderService>().Get(order.Id).History;
        Assert.Contains(history, e => e.Kind == OrderEventKind.TableChanged && e.Description == "Moved from table 3 to table 7");

        // Survives a restart of the hub.
        Restart();
        Assert.Equal("7", (await Cashier.GetJson<OrderView>($"/api/orders/{order.Id}")).Table);

        // A takeaway cannot be put on a table.
        var takeaway = await (await Cashier.Post("/api/orders", EatIn(null, OrderType.Takeaway))).Read<OrderView>();
        Assert.Contains("Only eat-in orders", await Problem(await Cashier.Post($"/api/orders/{takeaway.Id}/table", new MoveTableRequest("2"))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_table_is_on_the_kitchen_ticket_and_the_receipt()
    {
        await UseTables(10);
        var created = await (await Cashier.Post("/api/orders", EatIn("4"))).Read<OrderView>();
        var order = Host.Services.GetRequiredService<OrderService>().Get(created.Id);

        var ticket = Layouts.KitchenTicket(order, order.ActiveLines, KitchenTicketKind.New, PrintSamples.Shop, TimeZoneInfo.Utc).PlainText;
        Assert.Contains($"{order.Number.Display}\nEAT IN\nTABLE 4\n", ticket, StringComparison.Ordinal);

        var receipt = Layouts.Receipt(order, null, PrintSamples.Shop, TimeZoneInfo.Utc, "Sam", isCopy: false).PlainText;
        Assert.Contains("EAT IN\nTable 4\n", receipt, StringComparison.Ordinal);

        // No table, no table line.
        var plain = Host.Services.GetRequiredService<OrderService>().Get((await (await Cashier.Post("/api/orders", EatIn(null, OrderType.Takeaway))).Read<OrderView>()).Id);
        Assert.DoesNotContain("TABLE", Layouts.KitchenTicket(plain, plain.ActiveLines, KitchenTicketKind.New, PrintSamples.Shop, TimeZoneInfo.Utc).PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repeated_order_with_the_same_key_is_the_same_order()
    {
        await UseTables(10);
        var request = EatIn("2");
        var first = await (await Cashier.Post("/api/orders", request)).Read<OrderView>();
        var again = await (await Cashier.Post("/api/orders", request)).Read<OrderView>();
        Assert.Equal(first.Id, again.Id);
        Assert.True(again.Duplicate);

        // The same key for a different table is a different order: refused, not silently merged.
        var other = await Cashier.Post("/api/orders", request with { Table = "3" });
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
    }
}
