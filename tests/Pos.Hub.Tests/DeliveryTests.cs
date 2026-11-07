using System.Net;
using System.Net.Http.Json;
using Pos.Domain.Common;
using Pos.Domain.Delivery;
using Pos.Domain.Orders;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Delivery;
using Pos.Hub.Printing;

namespace Pos.Hub.Tests;

/// <summary>Phone delivery orders: postcode check, distance, charges, customers and printouts.</summary>
public sealed class DeliveryTests : HubTestBase
{
    // Bands from appsettings.json: 0–1 £1.00, 1–2 £1.50, 2–3 £2.00, 3–5 £3.00 (min order £15); max 5 miles; min order £12.
    private static readonly OrderContact Sam = new("Sam Patel", "07700 900123", new DeliveryAddress("12 Park Lane", null, "Leeds", "ls29jt"), "Side door");

    private static OrderLineRequest Doner => new(TestMenu.Doner, TestMenu.DonerRegular, 1, [TestMenu.Garlic]); // £8.99
    private static OrderLineRequest Pizza => new(TestMenu.Margherita, TestMenu.Margherita12);                  // £11.99

    private static CreateOrderRequest DeliveryOrder(OrderContact? contact, DeliveryRequest? delivery, params OrderLineRequest[] lines) =>
        new(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Phone, OrderType.Delivery, null, null, lines, contact, delivery);

    private async Task Menu() => await Manager.Put("/api/menu", TestMenu.State());

    private static async Task<HttpResponseMessage> PostWithPin<T>(HttpClient client, string url, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: PosJson.Options) };
        request.Headers.Add("X-Manager-Pin", TestShop.ManagerPin);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Postcode_check_gives_the_road_distance_and_is_remembered_for_when_the_internet_is_down()
    {
        var check = await Cashier.GetJson<PostcodeCheck>("/api/delivery/check?postcode=ls29jt");

        Assert.Equal("LS2 9JT", check.Postcode);
        Assert.Equal("Leeds", check.Town);
        Assert.Equal(1.3m, check.DistanceMiles); // 1 mile straight x 1.3

        Host.Postcodes.Down = true;
        var again = await Cashier.GetJson<PostcodeCheck>("/api/delivery/check?postcode=LS2 9JT");
        Assert.Equal(1.3m, again.DistanceMiles); // from the hub's own memory

        var down = await Cashier.GetAsync(new Uri("/api/delivery/check?postcode=LS7 3AA", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal("LookupUnavailable", (await down.Read<ErrorView>()).Code);
    }

    [Fact]
    public async Task Bad_and_unknown_postcodes_are_explained()
    {
        var bad = await Cashier.GetAsync(new Uri("/api/delivery/check?postcode=12345", UriKind.Relative));
        var unknown = await Cashier.GetAsync(new Uri("/api/delivery/check?postcode=LS99 9ZZ", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("not a UK postcode", (await bad.Read<ErrorView>()).Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("Check it with the customer", (await unknown.Read<ErrorView>()).Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await Kitchen.GetAsync(new Uri("/api/delivery/check?postcode=LS2 9JT", UriKind.Relative))).StatusCode);
    }

    private static CreateOrderRequest CollectionOrder(OrderContact? contact) =>
        new(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Phone, OrderType.Takeaway, null, null, [Pizza], contact, null);

    [Theory]
    [InlineData(null, null, "name and phone number")]
    [InlineData(null, "07700 900123", "the customer's name.")]
    [InlineData("Sam Patel", null, "the customer's phone number.")]
    [InlineData("  ", "07700 900123", "the customer's name.")]
    public async Task Collection_orders_need_the_customers_name_and_phone(string? name, string? phone, string message)
    {
        await Menu();
        var contact = name is null && phone is null ? null : new OrderContact(name, phone, null, null);

        var response = await Cashier.Post("/api/orders", CollectionOrder(contact));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, (await response.Read<ErrorView>()).Message, StringComparison.Ordinal);
        Assert.Empty(await Cashier.GetJson<List<OrderView>>("/api/orders"));
    }

    [Fact]
    public async Task Collection_order_with_name_and_phone_is_taken_and_the_details_cannot_be_removed_later()
    {
        await Menu();
        var created = await Cashier.Post("/api/orders", CollectionOrder(new OrderContact("Amira", "07700 900456", null, null)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var order = await created.Read<OrderView>();
        Assert.Equal(("Amira", "07700900456"), (order.Contact!.Name, order.Contact.Phone));

        // A correction must still have both.
        var noPhone = await Cashier.Post($"/api/orders/{order.Id}/contact", new OrderContact("Amira K", null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, noPhone.StatusCode);
        (await Cashier.Post($"/api/orders/{order.Id}/contact", new OrderContact("Amira K", "07700 900456", null, null))).EnsureSuccessStatusCode();

        // Counter takeaway and eat-in still need no customer details.
        var counter = new CreateOrderRequest(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, OrderType.Takeaway, null, null, [Pizza], null, null);
        Assert.Equal(HttpStatusCode.Created, (await Cashier.Post("/api/orders", counter)).StatusCode);
    }

    [Fact]
    public async Task Delivery_order_charges_by_distance_keeps_the_address_and_remembers_the_customer()
    {
        await Menu();

        var response = await Cashier.Post("/api/orders", DeliveryOrder(Sam, new DeliveryRequest(1.3m), Doner, Pizza));
        var order = await response.Read<OrderView>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(Money.Gbp(899 + 1199 + 150), order.Total); // 1–2 mile band: £1.50
        Assert.Equal(DeliveryQuoteStatus.Ok, order.Delivery!.Status);
        Assert.Equal("12 Park Lane, Leeds, LS2 9JT", order.Contact!.Address!.ToString());
        Assert.Equal("07700900123", order.Contact.Phone);

        // Next time, typing part of the number finds Sam with the address.
        var found = Assert.Single(await Cashier.GetJson<List<CustomerRecord>>("/api/customers?phone=900123"));
        Assert.Equal("Sam Patel", found.Name);
        Assert.Equal("Side door", found.Notes);
        Assert.Equal(1, found.OrderCount);
        // A delivery without the customer's name is refused (phone orders need name and phone).
        var noName = await Cashier.Post("/api/orders", DeliveryOrder(Sam with { Name = null }, new DeliveryRequest(1.3m), Pizza, Pizza));
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        Assert.Contains("need the customer's name", (await noName.Read<ErrorView>()).Message, StringComparison.Ordinal);
        await Cashier.Post("/api/orders", DeliveryOrder(Sam, new DeliveryRequest(1.3m), Pizza, Pizza));
        found = Assert.Single(await Cashier.GetJson<List<CustomerRecord>>("/api/customers?phone=+44 7700 900123"));
        Assert.Equal(2, found.OrderCount);
        Assert.Equal("Sam Patel", found.Name);
    }

    [Fact]
    public async Task Minimum_order_and_maximum_distance_are_reported_for_the_till_to_block()
    {
        await Menu();

        var small = await (await Cashier.Post("/api/orders/quote", DeliveryOrder(null, new DeliveryRequest(1.3m), Doner))).Read<OrderView>();
        var far = await (await Cashier.Post("/api/orders/quote", DeliveryOrder(null, new DeliveryRequest(9m), Doner, Pizza))).Read<OrderView>();
        var bandMinimum = await (await Cashier.Post("/api/orders/quote", DeliveryOrder(null, new DeliveryRequest(3.9m), Doner, new OrderLineRequest(TestMenu.Can, TestMenu.CanRegular, 3)))).Read<OrderView>();

        Assert.Equal(DeliveryQuoteStatus.BelowMinimumOrder, small.Delivery!.Status);
        Assert.Equal(Money.Gbp(1200), small.Delivery.MinimumOrder);
        Assert.Equal(DeliveryQuoteStatus.BeyondMaximumDistance, far.Delivery!.Status);
        Assert.Equal(DeliveryQuoteStatus.BelowMinimumOrder, bandMinimum.Delivery!.Status); // £12.59 < £15 for 3–5 miles
        Assert.Equal(Money.Gbp(1500), bandMinimum.Delivery.MinimumOrder);
    }

    [Fact]
    public async Task Delivery_needs_an_address_and_distance()
    {
        await Menu();

        var noAddress = await Cashier.Post("/api/orders", DeliveryOrder(new OrderContact("Sam", "07700900123", null, null), new DeliveryRequest(1m), Pizza));
        var noDistance = await Cashier.Post("/api/orders", DeliveryOrder(Sam, null, Pizza));
        var badPostcode = await Cashier.Post("/api/orders", DeliveryOrder(Sam with { Address = Sam.Address! with { Postcode = "nowhere" } }, new DeliveryRequest(1m), Pizza));

        Assert.Equal(HttpStatusCode.BadRequest, noAddress.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noDistance.StatusCode);
        Assert.Contains("not a UK postcode", (await badPostcode.Read<ErrorView>()).Message, StringComparison.Ordinal);
        Assert.Empty(await TillOnly.GetJson<List<OrderView>>("/api/orders?date=2026-11-05")); // nothing half-saved
    }

    [Fact]
    public async Task Changing_the_delivery_fee_needs_a_manager_and_is_recorded()
    {
        await Menu();
        var request = DeliveryOrder(Sam, new DeliveryRequest(1.3m, 0, "Regular customer"), Pizza, Doner);

        var refused = await Cashier.Post("/api/orders", request);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("ApprovalRequired", (await refused.Read<ErrorView>()).Code);
        Assert.Empty(await TillOnly.GetJson<List<OrderView>>("/api/orders?date=2026-11-05"));
        Assert.Empty(await Cashier.GetJson<List<CustomerRecord>>("/api/customers?phone=07700900123")); // rolled back too

        var order = await (await PostWithPin(Cashier, "/api/orders", request)).Read<OrderView>();
        Assert.Equal(Money.Gbp(1199 + 899), order.Total);
        Assert.True(order.Delivery!.Overridden);
        var history = await Manager.GetJson<List<OrderEvent>>($"/api/orders/{order.Id}/history");
        var change = Assert.Single(history, e => e.Kind == OrderEventKind.DeliveryChanged);
        Assert.Equal("Regular customer", change.Reason);
        Assert.Equal(Shop.ManagerId, change.ApprovedByUserId);
    }

    [Fact]
    public async Task Address_and_distance_can_be_corrected_on_a_saved_order()
    {
        await Menu();
        var order = await (await Cashier.Post("/api/orders", DeliveryOrder(Sam, new DeliveryRequest(1.3m), Pizza, Doner))).Read<OrderView>();

        var moved = await (await Cashier.Post($"/api/orders/{order.Id}/contact", Sam with { Address = new DeliveryAddress("4 Chapel St", null, null, "LS7 3AA") })).Read<OrderView>();
        var further = await (await Cashier.Post($"/api/orders/{order.Id}/delivery", new DeliveryRequest(3.9m))).Read<OrderView>();

        Assert.Equal("4 Chapel St, LS7 3AA", moved.Contact!.Address!.ToString());
        Assert.Equal(Money.Gbp(1199 + 899 + 300), further.Total);
        Assert.Equal("4 Chapel St, LS7 3AA", Assert.Single(await Cashier.GetJson<List<CustomerRecord>>("/api/customers?phone=07700900123")).Address!.ToString());
    }

    [Fact]
    public async Task Sending_a_delivery_prints_the_kitchen_ticket_and_the_receipt_for_the_bag()
    {
        await Menu();
        var order = await (await Cashier.Post("/api/orders", DeliveryOrder(Sam, new DeliveryRequest(1.3m), Pizza, Doner))).Read<OrderView>();

        await Cashier.Post($"/api/orders/{order.Id}/send-to-kitchen", new { });

        for (var i = 0; i < 50 && (await Cashier.GetJson<List<PrintJobView>>("/api/print-jobs")).Count(j => j.Status == PrintJobStatus.Printed) < 2; i++)
        {
            await Task.Delay(100);
        }

        var kinds = (await Cashier.GetJson<List<PrintJobView>>("/api/print-jobs")).Select(j => j.Job.Kind).ToHashSet();
        Assert.Contains(PrintJobKind.KitchenNew, kinds);
        Assert.Contains(PrintJobKind.Receipt, kinds);
    }
}
