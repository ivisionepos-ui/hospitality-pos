using System.Net;
using Pos.Domain.Orders;
using Pos.Domain.Staff;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Security;

namespace Pos.Hub.Tests;

/// <summary>Who can reach the hub, and what each role may do.</summary>
public sealed class AccessTests : HubTestBase
{
    private static CreateOrderRequest EmptyOrder() =>
        new(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, OrderType.Takeaway, null, null, []);

    [Fact]
    public async Task Unregistered_devices_are_refused()
    {
        var response = await Anonymous.GetAsync(new Uri("/api/menu", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("DeviceNotRegistered", (await response.Read<ErrorView>()).Code);
    }

    [Fact]
    public async Task Wrong_device_key_is_refused()
    {
        using var forged = ClientFor(Shop.Till with { DeviceKey = "not-the-key" });

        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync(new Uri("/api/menu", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Device_without_a_staff_login_can_read_but_not_take_orders()
    {
        Assert.Equal(HttpStatusCode.OK, (await TillOnly.GetAsync(new Uri("/api/menu", UriKind.Relative))).StatusCode);

        var response = await TillOnly.Post("/api/orders", EmptyOrder());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("NotLoggedIn", (await response.Read<ErrorView>()).Code);
    }

    [Fact]
    public async Task A_login_only_works_on_the_device_it_was_made_on()
    {
        using var stolen = ClientFor(Shop.Owner with { Token = Shop.Cashier.Token }); // cashier's token on the office PC

        Assert.Equal(HttpStatusCode.Unauthorized, (await stolen.Post("/api/orders", EmptyOrder())).StatusCode);
    }

    [Fact]
    public async Task Roles_are_enforced()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Kitchen.Post("/api/orders", EmptyOrder())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.Put("/api/menu", TestMenu.State())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.GetAsync(new Uri("/api/staff", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Manager.Post("/api/devices/pairing-code", new { })).StatusCode); // owner only
        Assert.Equal(HttpStatusCode.Created, (await Cashier.Post("/api/orders", EmptyOrder())).StatusCode);
    }

    [Fact]
    public async Task Me_shows_who_is_logged_in_and_what_they_may_do()
    {
        var me = await Cashier.GetStringAsync(new Uri("/api/auth/me", UriKind.Relative));

        Assert.Contains("Sam Cashier", me, StringComparison.Ordinal);
        Assert.Contains("TakeOrders", me, StringComparison.Ordinal);
        Assert.DoesNotContain("EditMenu", me, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Five_wrong_pins_lock_the_device_for_five_minutes()
    {
        for (var i = 0; i < AccessService.MaxPinFailures; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await TillOnly.Post("/api/auth/login", new LoginRequest("0000"))).StatusCode);
        }

        var locked = await TillOnly.Post("/api/auth/login", new LoginRequest(TestShop.CashierPin)); // even the right PIN
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("LockedOut", (await locked.Read<ErrorView>()).Code);

        Clock.Now += TimeSpan.FromMinutes(6);
        Assert.Equal(HttpStatusCode.OK, (await TillOnly.Post("/api/auth/login", new LoginRequest(TestShop.CashierPin))).StatusCode);
    }

    [Fact]
    public async Task Logins_expire_after_twelve_hours_and_logout_ends_them()
    {
        Clock.Now += TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Cashier.Post("/api/orders", EmptyOrder())).StatusCode);

        var fresh = await Host.Login(new PairedDevice(Shop.Till.DeviceId, Shop.Till.DeviceKey), TestShop.CashierPin);
        using var client = ClientFor(fresh);
        Assert.Equal(HttpStatusCode.Created, (await client.Post("/api/orders", EmptyOrder())).StatusCode);

        await client.Post("/api/auth/logout", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.Post("/api/orders", EmptyOrder())).StatusCode);
    }

    [Fact]
    public async Task Set_up_can_only_happen_once()
    {
        var again = await Anonymous.Post("/api/setup", new SetupRequest("Intruder", "8526", "Laptop"));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("true", await Anonymous.GetStringAsync(new Uri("/api/setup", UriKind.Relative)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pairing_codes_work_once_and_expire()
    {
        var code = await (await Owner.Post("/api/devices/pairing-code", new { })).Read<PairingCodeView>();
        Assert.Matches("^[0-9]{6}$", code.Code);

        Assert.Equal(HttpStatusCode.OK, (await Anonymous.Post("/api/devices/pair", new PairRequest(code.Code, "Kitchen screen", DeviceType.KitchenScreen))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous.Post("/api/devices/pair", new PairRequest(code.Code, "Again", DeviceType.Till))).StatusCode);

        var late = await (await Owner.Post("/api/devices/pairing-code", new { })).Read<PairingCodeView>();
        Clock.Now += TimeSpan.FromMinutes(11);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous.Post("/api/devices/pair", new PairRequest(late.Code, "Late", DeviceType.Till))).StatusCode);
    }

    [Fact]
    public async Task Guessing_pairing_codes_gets_blocked()
    {
        for (var i = 0; i < 10; i++)
        {
            await Anonymous.Post("/api/devices/pair", new PairRequest($"{i:000000}", "Guess", DeviceType.Till));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Anonymous.Post("/api/devices/pair", new PairRequest("123456", "Guess", DeviceType.Till))).StatusCode);
    }

    [Fact]
    public async Task Removed_device_stops_working_at_once()
    {
        var response = await Owner.DeleteAsync(new Uri($"/api/devices/{Shop.Till.DeviceId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Cashier.Post("/api/orders", EmptyOrder())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TillOnly.GetAsync(new Uri("/api/menu", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Removed_staff_member_is_logged_out_and_cannot_log_in()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await Manager.DeleteAsync(new Uri($"/api/staff/{Shop.CashierId}", UriKind.Relative))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Cashier.Post("/api/orders", EmptyOrder())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TillOnly.Post("/api/auth/login", new LoginRequest(TestShop.CashierPin))).StatusCode);
    }

    [Fact]
    public async Task Managers_cannot_create_or_remove_managers_and_owners()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Manager.Post("/api/staff", new AddStaffRequest("Max", StaffRole.Manager, "6193"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Manager.Post("/api/staff", new AddStaffRequest("Wes Waiter", StaffRole.Waiter, "6193"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Owner.Post("/api/staff", new AddStaffRequest("Max", StaffRole.Manager, "6284"))).StatusCode);
    }

    [Theory]
    [InlineData("123")]       // too short
    [InlineData("12a4")]      // not digits
    [InlineData("1111")]      // all the same
    [InlineData("1234")]      // a run
    [InlineData("5931")]      // already the cashier's
    public async Task Weak_or_duplicate_pins_are_refused(string pin)
    {
        var response = await Owner.Post("/api/staff", new AddStaffRequest("New", StaffRole.Waiter, pin));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Staff_can_change_their_own_pin_and_old_logins_end()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await Cashier.Post($"/api/staff/{Shop.CashierId}/pin", new ChangePinRequest("8264"))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Cashier.Post("/api/orders", EmptyOrder())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TillOnly.Post("/api/auth/login", new LoginRequest(TestShop.CashierPin))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TillOnly.Post("/api/auth/login", new LoginRequest("8264"))).StatusCode);
    }

    [Fact]
    public async Task A_cashier_cannot_approve_with_their_own_pin()
    {
        await Manager.Put("/api/menu", TestMenu.State());
        var order = await (await Cashier.Post("/api/orders", EmptyOrder() with { Lines = [new OrderLineRequest(TestMenu.Can, TestMenu.CanRegular)] })).Read<OrderView>();
        await Cashier.Post($"/api/orders/{order.Id}/send-to-kitchen", new { });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{order.Id}/lines/{order.Lines[0].Id}/void")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new VoidRequest("Spilt"), options: Domain.Common.PosJson.Options),
        };
        request.Headers.Add("X-Manager-Pin", TestShop.CashierPin);
        var response = await Cashier.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("CannotApprove", (await response.Read<ErrorView>()).Code);
    }

    [Fact]
    public void Pin_key_file_is_created_next_to_the_database()
    {
        var keyFile = Path.Combine(Path.GetDirectoryName(DbPath)!, "hub.key");

        Assert.True(File.Exists(keyFile));
        Assert.Equal(32, new FileInfo(keyFile).Length);
    }
}
