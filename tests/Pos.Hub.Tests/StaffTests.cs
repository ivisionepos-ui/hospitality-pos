using System.Net;
using Pos.Domain.Orders;
using Pos.Domain.Staff;
using Pos.Domain.Tax;
using Pos.Hub.Api;
using Pos.Hub.Security;

namespace Pos.Hub.Tests;

/// <summary>The staff list: adding, renaming, changing roles and PINs, removing and bringing back, and the record of changes.</summary>
public sealed class StaffTests : HubTestBase
{
    private static CreateOrderRequest EmptyOrder() =>
        new(DeviceIds.New(), Guid.NewGuid().ToString(), OrderSource.Counter, OrderType.Takeaway, null, null, []);

    private async Task<List<StaffMember>> List() =>
        await (await Owner.GetAsync(new Uri("/api/staff", UriKind.Relative))).Read<List<StaffMember>>();

    private async Task<List<StaffChange>> History(Guid? staffId = null) =>
        await (await Owner.GetAsync(new Uri(staffId is null ? "/api/staff/history" : $"/api/staff/history?staffId={staffId}", UriKind.Relative))).Read<List<StaffChange>>();

    private async Task<Guid> OwnerId() => (await List()).Single(s => s.Role == StaffRole.Owner).Id;

    [Fact]
    public async Task Owner_renames_and_promotes_a_cashier_who_must_log_in_again_and_then_has_the_new_permissions()
    {
        var response = await Owner.Put($"/api/staff/{Shop.CashierId}", new UpdateStaffRequest("  Sam   Smith ", StaffRole.Manager));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Sam Smith", (await response.Read<StaffMember>()).Name); // spaces tidied
        Assert.Equal(HttpStatusCode.Unauthorized, (await Cashier.Post("/api/orders", EmptyOrder())).StatusCode); // old login ended

        var login = await Host.Login(new PairedDevice(Shop.Till.DeviceId, Shop.Till.DeviceKey), TestShop.CashierPin);
        using var promoted = Host.Client(login);
        Assert.Equal(HttpStatusCode.OK, (await promoted.GetAsync(new Uri("/api/staff", UriKind.Relative))).StatusCode); // a manager now

        var history = await History(Shop.CashierId);
        Assert.Equal("Name Sam Cashier → Sam Smith; Role Cashier → Manager", history[0].Change);
        Assert.Equal("Olivia Owner", history[0].ChangedBy);
    }

    [Fact]
    public async Task A_rename_alone_keeps_the_person_logged_in()
    {
        await Manager.Put($"/api/staff/{Shop.CashierId}", new UpdateStaffRequest("Samantha Cashier", StaffRole.Cashier));

        Assert.Equal(HttpStatusCode.Created, (await Cashier.Post("/api/orders", EmptyOrder())).StatusCode);
    }

    [Fact]
    public async Task Nobody_changes_their_own_role_and_managers_cannot_make_managers()
    {
        var owner = await OwnerId();
        Assert.Equal(HttpStatusCode.Forbidden, (await Owner.Put($"/api/staff/{owner}", new UpdateStaffRequest("Olivia Owner", StaffRole.Manager))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Manager.Put($"/api/staff/{Shop.ManagerId}", new UpdateStaffRequest("Mia Manager", StaffRole.Owner))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Manager.Put($"/api/staff/{Shop.CashierId}", new UpdateStaffRequest("Sam Cashier", StaffRole.Manager))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Manager.Put($"/api/staff/{owner}", new UpdateStaffRequest("Someone", StaffRole.Owner))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.Put($"/api/staff/{Shop.CashierId}", new UpdateStaffRequest("Sam", StaffRole.Cashier))).StatusCode);

        // Renaming yourself is fine.
        Assert.Equal(HttpStatusCode.OK, (await Owner.Put($"/api/staff/{owner}", new UpdateStaffRequest("Olivia O", StaffRole.Owner))).StatusCode);
        Assert.Equal(StaffRole.Owner, (await List()).Single(s => s.Id == owner).Role);
    }

    [Theory]
    [InlineData("", "Enter a name.")]
    [InlineData("   ", "Enter a name.")]
    [InlineData("A name that is far too long for the till top bar", "A name can be up to 40 characters.")]
    [InlineData("mia manager", "Someone called mia manager is already on the staff list. Add a surname or initial.")]
    public async Task Bad_or_duplicate_names_are_explained(string name, string message)
    {
        var add = await Owner.Post("/api/staff", new AddStaffRequest(name, StaffRole.Waiter, "6193"));
        var rename = await Owner.Put($"/api/staff/{Shop.CashierId}", new UpdateStaffRequest(name, StaffRole.Cashier));

        Assert.Equal(HttpStatusCode.BadRequest, add.StatusCode);
        Assert.Equal(message, (await add.Read<ErrorView>()).Message);
        Assert.Equal(HttpStatusCode.BadRequest, rename.StatusCode);
    }

    [Fact]
    public async Task Changing_your_own_pin_needs_your_current_pin_and_wrong_ones_count_towards_the_lockout()
    {
        var noCurrent = await Cashier.Post($"/api/staff/{Shop.CashierId}/pin", new ChangePinRequest("8264"));
        var someoneElses = await Cashier.Post($"/api/staff/{Shop.CashierId}/pin", new ChangePinRequest("8264", TestShop.ManagerPin));

        Assert.Equal(HttpStatusCode.Unauthorized, noCurrent.StatusCode);
        Assert.Equal("Your current PIN is wrong.", (await noCurrent.Read<ErrorView>()).Message);
        Assert.Equal(HttpStatusCode.Unauthorized, someoneElses.StatusCode);

        for (var i = 0; i < AccessService.MaxPinFailures; i++)
        {
            await Cashier.Post($"/api/staff/{Shop.CashierId}/pin", new ChangePinRequest("8264", "0000"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Cashier.Post($"/api/staff/{Shop.CashierId}/pin", new ChangePinRequest("8264", TestShop.CashierPin))).StatusCode);

        Clock.Now += AccessService.LockoutWindow + TimeSpan.FromSeconds(1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TillOnly.Post("/api/auth/login", new LoginRequest("8264"))).StatusCode); // PIN unchanged
        Assert.Equal(HttpStatusCode.OK, (await TillOnly.Post("/api/auth/login", new LoginRequest(TestShop.CashierPin))).StatusCode);
    }

    [Fact]
    public async Task A_manager_resets_a_forgotten_pin_without_knowing_it()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await Manager.Post($"/api/staff/{Shop.CashierId}/pin", new ChangePinRequest("8264"))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await TillOnly.Post("/api/auth/login", new LoginRequest("8264"))).StatusCode);
        Assert.Equal("PIN reset", (await History(Shop.CashierId))[0].Change);
        Assert.Equal(HttpStatusCode.Forbidden, (await Manager.Post($"/api/staff/{await OwnerId()}/pin", new ChangePinRequest("8264"))).StatusCode);
    }

    [Fact]
    public async Task A_removed_person_is_brought_back_with_a_new_pin_and_the_old_one_stays_dead()
    {
        await Manager.DeleteAsync(new Uri($"/api/staff/{Shop.CashierId}", UriKind.Relative));
        Assert.False((await List()).Single(s => s.Id == Shop.CashierId).IsActive);
        Assert.Equal(HttpStatusCode.Conflict, (await Manager.Post($"/api/staff/{Shop.CashierId}/pin", new ChangePinRequest("8264"))).StatusCode);

        var back = await Manager.Post($"/api/staff/{Shop.CashierId}/reactivate", new ReactivateStaffRequest("8264"));

        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.True((await back.Read<StaffMember>()).IsActive);
        Assert.Equal(HttpStatusCode.OK, (await TillOnly.Post("/api/auth/login", new LoginRequest("8264"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Manager.Post($"/api/staff/{Shop.CashierId}/reactivate", new ReactivateStaffRequest("8265"))).StatusCode); // already back
        Assert.Equal(["Brought back", "Removed"], (await History(Shop.CashierId)).Take(2).Select(h => h.Change));
    }

    [Fact]
    public async Task Someone_with_the_same_name_cannot_be_brought_back_while_a_new_person_has_it()
    {
        await Owner.DeleteAsync(new Uri($"/api/staff/{Shop.CashierId}", UriKind.Relative));
        await Owner.Post("/api/staff", new AddStaffRequest("Sam Cashier", StaffRole.Cashier, "6193"));

        var back = await Owner.Post($"/api/staff/{Shop.CashierId}/reactivate", new ReactivateStaffRequest("8264"));

        Assert.Equal(HttpStatusCode.BadRequest, back.StatusCode);
    }

    [Fact]
    public async Task Nobody_removes_themselves_so_the_shop_always_keeps_an_owner()
    {
        var response = await Owner.DeleteAsync(new Uri($"/api/staff/{await OwnerId()}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You cannot remove yourself.", (await response.Read<ErrorView>()).Message);
    }

    [Fact]
    public async Task The_history_records_every_change_but_never_a_pin_and_only_managers_see_it()
    {
        await Owner.Post("/api/staff", new AddStaffRequest("Wes Waiter", StaffRole.Waiter, "6193"));
        await Owner.Post($"/api/staff/{Shop.CashierId}/pin", new ChangePinRequest("8264"));

        var all = await History();

        Assert.Contains(all, h => h.StaffName == "Wes Waiter" && h.Change == "Added as Waiter" && h.ChangedBy == "Olivia Owner");
        Assert.Contains(all, h => h.StaffName == "Olivia Owner" && h.Change == "Shop set up: owner" && h.ChangedBy == "Set-up");
        Assert.DoesNotContain(all, h => h.Change.Contains("6193", StringComparison.Ordinal) || h.Change.Contains("8264", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Forbidden, (await Kitchen.GetAsync(new Uri("/api/staff/history", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task The_owner_cannot_remove_the_till_they_are_using()
    {
        var response = await Owner.DeleteAsync(new Uri($"/api/devices/{Shop.Owner.DeviceId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Owner.DeleteAsync(new Uri($"/api/devices/{Shop.Till.DeviceId}", UriKind.Relative))).StatusCode);
    }
}
