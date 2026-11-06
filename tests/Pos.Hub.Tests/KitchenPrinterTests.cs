using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Printing;

namespace Pos.Hub.Tests;

/// <summary>Several kitchen printers, each printing only its own food (pizzas to the pizza printer…).</summary>
public sealed class KitchenPrinterTests : HubTestBase
{
    private static readonly Guid GrillId = Guid.NewGuid();
    private static readonly Guid PizzaId = Guid.NewGuid();
    private static readonly Guid DrinksId = Guid.NewGuid();
    private static readonly PrinterChoice Pdf = new(PrintOutputType.Pdf, null, null, 80, true, 2);

    private static OrderLineRequest Pizza => new(TestMenu.Margherita, TestMenu.Margherita12);
    private static OrderLineRequest Doner => new(TestMenu.Doner, TestMenu.DonerRegular, 1, [TestMenu.Garlic]);
    private static OrderLineRequest Can => new(TestMenu.Can, TestMenu.CanRegular);

    private static OrderLineRequest Deal => new(TestMenu.Deal, TestMenu.DealRegular, 1, Choices:
    [
        new DealChoiceRequest(TestMenu.PizzaSlot, TestMenu.Margherita, TestMenu.Margherita12),
        new DealChoiceRequest(TestMenu.KebabSlot, TestMenu.Doner, TestMenu.DonerRegular, [TestMenu.Garlic]),
        new DealChoiceRequest(TestMenu.DrinkSlot, TestMenu.Can, TestMenu.CanRegular),
    ]);

    /// <summary>Grill first (kebabs, and anything nobody chose), then Pizza, then Drinks.</summary>
    private static List<KitchenPrinterChoice> Stations(params KitchenPrinterChoice[] extra) =>
    [
        new(GrillId, "Grill", Pdf, KitchenPrintMode.Chosen, [TestMenu.Kebabs], []),
        new(PizzaId, "Pizza", Pdf, KitchenPrintMode.Chosen, [TestMenu.Pizzas], []),
        new(DrinksId, "Drinks", Pdf, KitchenPrintMode.Chosen, [TestMenu.Drinks], []),
        .. extra,
    ];

    private Task<HttpResponseMessage> Save(IReadOnlyList<KitchenPrinterChoice> kitchens) =>
        Owner.Put("/api/settings/printers", new PrinterSetup(Pdf, kitchens, true));

    private async Task<OrderView> Send(params OrderLineRequest[] lines)
    {
        await Manager.Put("/api/menu", TestMenu.State());
        var order = await (await Cashier.Post("/api/orders", new CreateOrderRequest(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, OrderType.Takeaway, null, null, lines))).Read<OrderView>();
        Assert.Equal(HttpStatusCode.OK, (await Cashier.Post($"/api/orders/{order.Id}/send-to-kitchen", new { })).StatusCode);
        return order;
    }

    private async Task<List<PrintJobView>> KitchenJobs(Guid orderId, int expected)
    {
        List<PrintJobView> jobs = [];
        for (var i = 0; i < 100; i++)
        {
            jobs = [.. (await Cashier.GetJson<List<PrintJobView>>("/api/print-jobs")).Where(j => j.Job.OrderId == orderId && j.Job.Target == PrintTarget.Kitchen)];
            if (jobs.Count >= expected && jobs.All(j => j.Status == PrintJobStatus.Printed))
            {
                break;
            }

            await Task.Delay(50);
        }

        return jobs;
    }

    /// <summary>The ticket text a kitchen printer prints for an order (as the hub would draw it now).</summary>
    private string Ticket(Guid orderId, Guid stationId)
    {
        var settings = Host.Services.GetRequiredService<PrintSettings>();
        var order = Host.Services.GetRequiredService<OrderService>().Get(orderId);
        var mine = KitchenRouting.Route(order.Lines, settings.Kitchens).Single(r => r.Station.Id == stationId).Lines;
        return Layouts.KitchenTicket(order, mine, KitchenTicketKind.New, settings.ReceiptText, TimeZoneInfo.Utc, settings.KitchenPrinter(stationId).Name).PlainText;
    }

    [Fact]
    public async Task Each_item_prints_on_its_own_kitchen_printer_and_unchosen_items_on_the_first()
    {
        // The grill printer chose a category that is not on this menu, so nobody chose the kebab: it goes to the first printer.
        var kitchens = Stations();
        kitchens[0] = kitchens[0] with { CategoryIds = [Guid.NewGuid()] };
        Assert.Equal(HttpStatusCode.OK, (await Save(kitchens)).StatusCode);
        var order = await Send(Pizza, Can, Doner, Pizza);

        var jobs = await KitchenJobs(order.Id, 3);

        var byStation = jobs.ToDictionary(j => j.Job.KitchenId!.Value, j => j.Job.LineIds.Select(id => order.Lines.Single(l => l.Id == id).Name.En).Order().ToList());
        Assert.Equal(["Margherita", "Margherita"], byStation[PizzaId]);
        Assert.Equal(["Can"], byStation[DrinksId]);
        Assert.Equal(["Doner kebab"], byStation[GrillId]);
        Assert.All(jobs, j => Assert.Equal(PrintJobStatus.Printed, j.Status));
        Assert.Contains("-Pizza-", jobs.Single(j => j.Job.KitchenId == PizzaId).Output, StringComparison.Ordinal); // the PDF file name says which printer

        var pizzaTicket = Ticket(order.Id, PizzaId);
        Assert.StartsWith("NEW ORDER\nPIZZA\n", pizzaTicket, StringComparison.Ordinal); // the printer's name at the top
        Assert.DoesNotContain("Can", pizzaTicket, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_meal_deal_is_split_so_each_printer_gets_only_its_part()
    {
        await Save(Stations());
        var order = await Send(Deal);

        var jobs = await KitchenJobs(order.Id, 3);

        Assert.Equal(3, jobs.Count);
        var pizza = Ticket(order.Id, PizzaId);
        var drinks = Ticket(order.Id, DrinksId);
        var grill = Ticket(order.Id, GrillId);
        Assert.Contains("1 x Pizza & Kebab Deal (part)", pizza, StringComparison.Ordinal);
        Assert.Contains("Margherita", pizza, StringComparison.Ordinal);
        Assert.DoesNotContain("Doner", pizza, StringComparison.Ordinal);
        Assert.DoesNotContain("Can", pizza, StringComparison.Ordinal);
        Assert.Contains("Can", drinks, StringComparison.Ordinal);
        Assert.DoesNotContain("Margherita", drinks, StringComparison.Ordinal);
        Assert.Contains("Doner", grill, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_printer_that_chose_the_deal_gets_it_whole_and_an_everything_printer_gets_all()
    {
        var pass = new KitchenPrinterChoice(Guid.NewGuid(), "Pass", Pdf, KitchenPrintMode.Everything, [], []);
        var deals = new KitchenPrinterChoice(Guid.NewGuid(), "Deals", Pdf, KitchenPrintMode.Chosen, [TestMenu.Deals], []);
        await Save(Stations(pass, deals));
        var order = await Send(Deal, Can);

        var jobs = await KitchenJobs(order.Id, 3);

        Assert.Equal(["Deals", "Drinks", "Pass"], jobs.Select(j => Host.Services.GetRequiredService<PrintSettings>().KitchenPrinter(j.Job.KitchenId).Name).Order());
        Assert.DoesNotContain("(part)", Ticket(order.Id, deals.Id), StringComparison.Ordinal);
        Assert.Contains("Can", Ticket(order.Id, pass.Id), StringComparison.Ordinal);
        Assert.Contains("Pizza & Kebab Deal", Ticket(order.Id, pass.Id), StringComparison.Ordinal);
    }

    private static Guid[] PrintersFor(List<PrintJobView> jobs, Guid lineId) =>
        [.. jobs.Where(j => j.Job.LineIds.Contains(lineId)).Select(j => j.Job.KitchenId!.Value).OrderBy(id => id == GrillId ? 0 : id == PizzaId ? 1 : 2)];

    [Fact]
    public async Task One_item_can_print_on_more_than_one_printer()
    {
        // The margherita is "also here" on the grill: it prints on the pizza printer (its category) and on the grill.
        var kitchens = Stations();
        kitchens[0] = kitchens[0] with { ProductIds = [TestMenu.Margherita] };
        await Save(kitchens);
        var order = await Send(Pizza, Can);

        var jobs = await KitchenJobs(order.Id, 3);

        Assert.Equal([GrillId, PizzaId], PrintersFor(jobs, order.Lines[0].Id));
        Assert.Equal([DrinksId], PrintersFor(jobs, order.Lines[1].Id));
    }

    [Fact]
    public async Task One_category_can_print_on_more_than_one_printer()
    {
        var kitchens = Stations();
        kitchens[2] = kitchens[2] with { CategoryIds = [TestMenu.Drinks, TestMenu.Pizzas] }; // drinks station also gets a copy of pizzas
        await Save(kitchens);
        var order = await Send(Pizza);

        Assert.Equal([PizzaId, DrinksId], PrintersFor(await KitchenJobs(order.Id, 2), order.Lines[0].Id));
    }

    [Fact]
    public async Task An_item_can_be_moved_with_also_here_on_one_printer_and_not_here_on_another()
    {
        var kitchens = Stations();
        kitchens[0] = kitchens[0] with { ProductIds = [TestMenu.Margherita] };          // also here (grill)
        kitchens[1] = kitchens[1] with { ExcludedProductIds = [TestMenu.Margherita] }; // not here (pizza)
        await Save(kitchens);
        var order = await Send(Pizza);

        Assert.Equal([GrillId], PrintersFor(await KitchenJobs(order.Id, 1), order.Lines[0].Id));
    }

    [Fact]
    public async Task An_item_marked_not_here_everywhere_still_prints_on_the_first_printer()
    {
        var kitchens = Stations();
        kitchens[1] = kitchens[1] with { ExcludedProductIds = [TestMenu.Margherita] };
        await Save(kitchens);
        var order = await Send(Pizza);

        Assert.Equal([GrillId], PrintersFor(await KitchenJobs(order.Id, 1), order.Lines[0].Id)); // nothing is lost
    }

    [Fact]
    public async Task A_set_up_saved_by_0_16_0_prints_exactly_as_before()
    {
        // In 0.16.0 choosing a single item moved it: the margherita chosen on the grill printed only there.
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO settings (key, value, updated_at, updated_by) VALUES ('printers', $v, '2026-11-06T00:00:00Z', 'x');";
            var pdf = """{"output":"Pdf","paperWidthMm":80,"cut":true,"drawerPin":2}""";
            command.Parameters.AddWithValue("$v", $$"""
                {"receipt":{{pdf}},"openDrawerOnCash":true,"kitchens":[
                  {"id":"{{GrillId}}","name":"Grill","printer":{{pdf}},"mode":"Chosen","categoryIds":["{{TestMenu.Kebabs}}"],"productIds":["{{TestMenu.Margherita}}"]},
                  {"id":"{{PizzaId}}","name":"Pizza","printer":{{pdf}},"mode":"Chosen","categoryIds":["{{TestMenu.Pizzas}}"],"productIds":[]}]}
                """);
            command.ExecuteNonQuery();
        }

        Restart();
        var order = await Send(Pizza);

        Assert.Equal([GrillId], PrintersFor(await KitchenJobs(order.Id, 1), order.Lines[0].Id));
        var pizza = (await Owner.GetJson<PrintersView>("/api/settings/printers")).Setup.Kitchens.Single(k => k.Name == "Pizza");
        Assert.Equal([TestMenu.Margherita], pizza.ExcludedProductIds!); // shown as "not here" on the pizza printer
    }

    [Fact]
    public async Task Removing_an_item_prints_the_void_slip_on_the_printer_that_made_it()
    {
        await Save(Stations());
        var order = await Send(Pizza, Can);

        await Manager.Post($"/api/orders/{order.Id}/lines/{order.Lines[1].Id}/void", new VoidRequest("Spilt"));

        var jobs = await KitchenJobs(order.Id, 3);
        var voidSlip = jobs.Single(j => j.Job.Kind == PrintJobKind.KitchenVoid);
        Assert.Equal(DrinksId, voidSlip.Job.KitchenId);
    }

    [Fact]
    public async Task Mistakes_in_the_printer_list_are_explained()
    {
        async Task<string> Problem(IReadOnlyList<KitchenPrinterChoice> k) => (await (await Save(k)).Read<ErrorView>()).Message;

        Assert.Equal("Two kitchen printers are called pizza. Give each its own name.",
            await Problem(Stations(new KitchenPrinterChoice(Guid.Empty, "pizza", Pdf, KitchenPrintMode.Everything, [], []))));
        Assert.Equal("Choose what the Desserts printer prints, or set it to print everything.",
            await Problem(Stations(new KitchenPrinterChoice(Guid.Empty, "Desserts", Pdf, KitchenPrintMode.Chosen, [], []))));
        Assert.Equal("Give each kitchen printer a name of up to 30 characters, e.g. Pizza.",
            await Problem(Stations(new KitchenPrinterChoice(Guid.Empty, " ", Pdf, KitchenPrintMode.Everything, [], []))));
        Assert.Equal("Keep at least one kitchen printer (it can be set to Off).", await Problem([]));
        Assert.Equal("At most 10 kitchen printers.",
            await Problem([.. Enumerable.Range(1, 11).Select(i => new KitchenPrinterChoice(Guid.Empty, $"K{i}", Pdf, KitchenPrintMode.Everything, [], []))]));
    }

    [Fact]
    public async Task New_printers_get_an_id_and_the_list_is_kept_after_a_restart()
    {
        var desserts = new KitchenPrinterChoice(Guid.Empty, "Desserts", Pdf, KitchenPrintMode.Chosen, [Guid.NewGuid()], []);
        var saved = await (await Save(Stations(desserts))).Read<PrinterSetup>();
        Assert.NotEqual(Guid.Empty, saved.Kitchens[3].Id);

        Restart();
        var after = await Owner.GetJson<PrintersView>("/api/settings/printers");

        Assert.Equal(["Grill", "Pizza", "Drinks", "Desserts"], after.Setup.Kitchens.Select(k => k.Name));
        Assert.Equal([TestMenu.Pizzas], after.Setup.Kitchens[1].CategoryIds);
    }

    [Fact]
    public async Task Printers_saved_by_the_previous_version_load_as_one_kitchen_printer()
    {
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO settings (key, value, updated_at, updated_by) VALUES ('printers', $v, '2026-11-05T00:00:00Z', 'x');";
            command.Parameters.AddWithValue("$v", """{"receipt":{"output":"Pdf","paperWidthMm":80,"cut":true,"drawerPin":2},"kitchen":{"output":"Pdf","paperWidthMm":58,"cut":false,"drawerPin":2},"openDrawerOnCash":false}""");
            command.ExecuteNonQuery();
        }

        Restart();
        var setup = (await Owner.GetJson<PrintersView>("/api/settings/printers")).Setup;

        var kitchen = Assert.Single(setup.Kitchens);
        Assert.Equal(("Kitchen", KitchenPrintMode.Everything, 58), (kitchen.Name, kitchen.Mode, kitchen.Printer.PaperWidthMm));
        Assert.False(setup.OpenDrawerOnCash);
    }

    [Fact]
    public async Task Each_kitchen_printer_has_its_own_test_page()
    {
        await Save(Stations());

        var test = await (await Owner.Post("/api/settings/printers/test", new TestPrintRequest(PrintTarget.Kitchen, DrinksId))).Read<TestPrintResult>();

        Assert.Contains("Printer-test-Drinks", test.Output, StringComparison.Ordinal);
    }

    private sealed record PrintersView(PrinterSetup Setup, IReadOnlyList<string> Installed);

    private sealed record TestPrintResult(string Output);
}
