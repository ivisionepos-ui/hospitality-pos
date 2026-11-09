using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Online;

namespace Pos.Hub.Tests;

/// <summary>A stand-in for the online ordering service: records what the hub sends, and hands out queued orders.</summary>
internal sealed class FakeOnlineService : HttpMessageHandler
{
    public const string Key = "test-shop-key-0123456789abcdef0123456789";

    public List<JsonNode> Published { get; } = [];

    public List<JsonNode> Polls { get; } = [];

    /// <summary>Orders the website still has for the hub (it keeps sending them until told they arrived).</summary>
    public List<JsonObject> Waiting { get; } = [];

    public HttpStatusCode? FailWith { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (FailWith is { } code)
        {
            return Reply(code, new { message = "Wrong shop key." });
        }

        Assert.Equal($"Bearer {Key}", request.Headers.Authorization?.ToString());
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
        switch (request.RequestUri!.AbsolutePath)
        {
            case "/hub/publish":
                Published.Add(body);
                return Reply(HttpStatusCode.OK, new { publishedAt = "now" });
            case "/hub/poll":
                Polls.Add(body);
                foreach (var update in body["updates"]!.AsArray())
                {
                    Waiting.RemoveAll(o => (string?)o["id"] == (string?)update!["id"]);
                }

                return Reply(HttpStatusCode.OK, new { serverTime = "now", orders = Waiting });
            default:
                return Reply(HttpStatusCode.NotFound, new { message = "Not found." });
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode code, object body) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}

/// <summary>Website orders: the hub sends the menu, collects orders, puts them on the tills and reports back.</summary>
public sealed class OnlineOrderTests : HubTestBase, IDisposable
{
    private readonly FakeOnlineService _cloud = new();

    public void Dispose() => _cloud.Dispose();

    private OnlineOrderService Online => Host.Services.GetRequiredService<OnlineOrderService>();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Host.Services.GetRequiredService<OnlineHttp>().Handler = _cloud;
        await Manager.Put("/api/menu", TestMenu.State());
    }

    private Task<HttpResponseMessage> Connect(bool takeOrders = true) =>
        Owner.Put("/api/settings/online", OnlineSettings.Default with { Enabled = true, ServiceUrl = "https://order.example.co.uk", ShopKey = FakeOnlineService.Key, TakeOrders = takeOrders });

    private static JsonObject WebOrder(string type = "Collection", params object[] lines) => new()
    {
        ["id"] = Guid.NewGuid().ToString(),
        ["createdAt"] = "2026-11-09T18:00:00Z",
        ["type"] = type,
        ["customer"] = JsonSerializer.SerializeToNode(new { name = "Sam Patel", phone = "07700900123", notes = "Ring the bell", address = "12 Long Road", town = "Leeds", postcode = "LS2 9JT" }),
        ["lines"] = JsonSerializer.SerializeToNode(lines.Length > 0 ? lines : [new { productId = TestMenu.Margherita, variantId = TestMenu.Margherita12, quantity = 2, optionIds = Array.Empty<Guid>(), note = "well done" }]),
        ["deliveryMiles"] = 1.3,
    };

    private async Task<OrderView> HubOrder(JsonObject web) => await Owner.GetJson<OrderView>($"/api/orders/{(string)web["id"]!}");

    [Fact]
    public async Task Nothing_is_sent_until_the_owner_connects_the_website()
    {
        Assert.Equal(0, await Online.SyncAsync(default));
        Assert.Empty(_cloud.Published);
        Assert.Empty(_cloud.Polls);
    }

    [Fact]
    public async Task The_menu_and_shop_details_are_sent_and_sent_again_only_when_they_change()
    {
        (await Connect()).EnsureSuccessStatusCode();
        await Online.SyncAsync(default);
        await Online.SyncAsync(default);

        var published = Assert.Single(_cloud.Published);
        var products = published["menu"]!["products"]!.AsArray();
        Assert.Contains(products, p => (string?)p!["name"] == "Margherita");
        Assert.DoesNotContain(products, p => (string?)p!["name"] == "Pizza & Kebab Deal"); // meal deals: not on the website yet
        Assert.Equal("17:00", (string?)published["info"]!["hours"]!["fri"]![0]!["open"]);
        Assert.True((bool)published["info"]!["delivery"]!["enabled"]!);
        Assert.Equal(5, (decimal)published["info"]!["delivery"]!["table"]!["maximumMiles"]!);
        Assert.Equal(2, _cloud.Polls.Count);
        Assert.True((bool)_cloud.Polls[0]["accepting"]!);

        // A price change goes to the website on the next check-in.
        (await Manager.Post($"/api/menu/products/{TestMenu.Can}/price", new PriceRequest(TestMenu.CanRegular, 130))).EnsureSuccessStatusCode();
        await Online.SyncAsync(default);
        Assert.Equal(2, _cloud.Published.Count);
        Assert.Contains(_cloud.Published[1]["menu"]!["products"]!.AsArray(), p => (string?)p!["name"] == "Can" && (int)p!["variants"]![0]!["pricePence"]! == 130);
    }

    [Fact]
    public async Task A_website_order_arrives_on_the_tills_waiting_to_be_accepted_and_the_customer_is_told_each_step()
    {
        (await Connect()).EnsureSuccessStatusCode();
        var web = WebOrder();
        _cloud.Waiting.Add(web);

        Assert.Equal(1, await Online.SyncAsync(default));
        var order = await HubOrder(web);
        Assert.Equal((OrderSource.Website, OrderType.Takeaway, OrderStatus.Received), (order.Source, order.Type, order.Status));
        Assert.Equal(("Sam Patel", "07700900123", "Ring the bell"), (order.Contact!.Name, order.Contact.Phone, order.Contact.Notes));
        Assert.Equal(2, order.Lines.Single().Quantity);
        Assert.Equal("well done", order.Lines.Single().Note);

        // The customer hears it arrived (straight away, in the same round), with the till's order number.
        var received = _cloud.Polls[^1]["updates"]!.AsArray().Single()!;
        Assert.Equal(("Received", order.Number), ((string?)received["status"], (string?)received["number"]));

        // The website sends it again before it heard: still one order.
        _cloud.Waiting.Add(web);
        Assert.Equal(0, await Online.SyncAsync(default));
        Assert.Single(await Owner.GetJson<List<OrderView>>("/api/orders"), o => o.Source == OrderSource.Website);

        // Staff accept it with a ready time: the customer sees when.
        Clock.Now += TimeSpan.FromMinutes(2);
        var acceptedAt = Clock.Now;
        (await Cashier.Post($"/api/orders/{order.Id}/status", new StatusRequest(OrderStatus.Accepted, "Ready in 25 minutes"))).EnsureSuccessStatusCode();
        await Online.SyncAsync(default);
        var accepted = _cloud.Polls[^1]["updates"]!.AsArray().Single()!;
        Assert.Equal("Accepted", (string?)accepted["status"]);
        Assert.Equal(acceptedAt.AddMinutes(25), DateTimeOffset.Parse((string)accepted["readyAt"]!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(order.Total.MinorUnits, (long)accepted["totalPence"]!);

        // Nothing new to say: nothing sent.
        await Online.SyncAsync(default);
        Assert.Empty(_cloud.Polls[^1]["updates"]!.AsArray());

        // The history shows it came from the website.
        Assert.Equal("Website", Host.Services.GetRequiredService<Security.AccessService>().StaffName(OnlineOrderService.WebsiteUserId));
    }

    [Fact]
    public async Task A_rejected_order_tells_the_customer_why()
    {
        (await Connect()).EnsureSuccessStatusCode();
        var web = WebOrder();
        _cloud.Waiting.Add(web);
        await Online.SyncAsync(default);

        var order = await HubOrder(web);
        (await Cashier.Post($"/api/orders/{order.Id}/status", new StatusRequest(OrderStatus.Rejected, "Too busy tonight"))).EnsureSuccessStatusCode();
        await Online.SyncAsync(default);
        var update = _cloud.Polls[^1]["updates"]!.AsArray().Single()!;
        Assert.Equal(("Rejected", "Too busy tonight"), ((string?)update["status"], (string?)update["reason"]));
    }

    [Fact]
    public async Task An_order_the_till_cannot_take_is_turned_down_at_once_with_the_reason()
    {
        (await Connect()).EnsureSuccessStatusCode();
        (await Manager.Post($"/api/menu/products/{TestMenu.Margherita}/sold-out", new SoldOutRequest(true))).EnsureSuccessStatusCode();
        var web = WebOrder();
        _cloud.Waiting.Add(web);

        Assert.Equal(0, await Online.SyncAsync(default));
        Assert.Equal(HttpStatusCode.NotFound, (await Owner.GetAsync(new Uri($"/api/orders/{(string)web["id"]!}", UriKind.Relative))).StatusCode);
        await Online.SyncAsync(default);
        var update = _cloud.Polls.SelectMany(p => p["updates"]!.AsArray()).Single()!;
        Assert.Equal("Rejected", (string?)update["status"]);
        Assert.Contains("sold out", (string?)update["reason"], StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_cloud.Waiting);
    }

    [Fact]
    public async Task A_delivery_order_gets_the_shops_own_distance_and_charge()
    {
        (await Connect()).EnsureSuccessStatusCode();
        var web = WebOrder("Delivery", new { productId = TestMenu.Margherita, variantId = TestMenu.Margherita12, quantity = 2, optionIds = Array.Empty<Guid>() });
        _cloud.Waiting.Add(web);
        await Online.SyncAsync(default);

        var order = await HubOrder(web);
        Assert.Equal(OrderType.Delivery, order.Type);
        Assert.Equal(1.3m, order.Delivery!.DistanceMiles);  // LS2 9JT: 1 mile straight, 1.3 by road
        Assert.Equal(150, order.Delivery.Fee.MinorUnits);   // the 1–2 mile band
        Assert.Equal("LS2 9JT", order.Contact!.Address!.Postcode);

        // With the postcode service down, the website's distance is used.
        Host.Postcodes.Down = true;
        var second = WebOrder("Delivery", new { productId = TestMenu.Margherita, variantId = TestMenu.Margherita12, quantity = 2, optionIds = Array.Empty<Guid>() });
        second["customer"]!["postcode"] = "LS7 3AA";
        second["deliveryMiles"] = 3.9;
        _cloud.Waiting.Add(second);
        await Online.SyncAsync(default);
        Assert.Equal(3.9m, (await HubOrder(second)).Delivery!.DistanceMiles);
    }

    [Fact]
    public async Task A_wrong_shop_key_is_reported_on_the_website_screen()
    {
        (await Connect()).EnsureSuccessStatusCode();
        _cloud.FailWith = HttpStatusCode.Unauthorized;
        await Online.SyncAsync(default);

        var view = await Owner.GetJson<OnlineSettingsView>("/api/settings/online");
        Assert.False(view.Status.Connected);
        Assert.Contains("refused the shop key", view.Status.Problem, StringComparison.Ordinal);

        _cloud.FailWith = null;
        await Online.SyncAsync(default);
        view = await Owner.GetJson<OnlineSettingsView>("/api/settings/online");
        Assert.True(view.Status.Connected);
        Assert.Null(view.Status.Problem);
    }

    [Fact]
    public async Task Settings_are_checked_and_the_key_is_never_sent_back()
    {
        var view = await (await Connect()).Read<OnlineSettingsView>();
        Assert.True(view.HasKey);
        Assert.Null(view.Settings.ShopKey);
        Assert.DoesNotContain(FakeOnlineService.Key, await (await Owner.GetAsync(new Uri("/api/settings/online", UriKind.Relative))).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Saving without a key keeps the saved one.
        (await Owner.Put("/api/settings/online", view.Settings with { TakeOrders = false })).EnsureSuccessStatusCode();
        Assert.Equal(FakeOnlineService.Key, Online.Settings().ShopKey);

        async Task<string> Refused(OnlineSettings s) => (await (await Owner.Put("/api/settings/online", s)).Read<ErrorView>()).Message;
        Assert.Contains("https://", await Refused(view.Settings with { ServiceUrl = "http://order.example.co.uk" }), StringComparison.Ordinal);
        Assert.Contains("at least 32 characters", await Refused(view.Settings with { ShopKey = "short" }), StringComparison.Ordinal);
        Assert.Contains("is not a time", await Refused(view.Settings with { Hours = new Dictionary<string, IReadOnlyList<OpeningSlot>> { ["mon"] = [new("5pm", "11pm")] } }), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await Manager.Put("/api/settings/online", view.Settings)).StatusCode);

        var key = (await (await Owner.Post("/api/settings/online/new-key", new { })).Read<JsonObject>())["key"]!.GetValue<string>();
        Assert.True(key.Length >= 40);
    }

    [Fact]
    public async Task The_website_hears_when_the_shop_stops_taking_orders()
    {
        (await Connect(takeOrders: false)).EnsureSuccessStatusCode();
        await Online.SyncAsync(default);
        Assert.False((bool)_cloud.Polls[^1]["accepting"]!);
    }
}
