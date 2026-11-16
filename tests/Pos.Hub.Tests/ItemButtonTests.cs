using System.Net;
using Pos.Hub.Api;
using Pos.Hub.Security;

namespace Pos.Hub.Tests;

/// <summary>Item buttons per till: size and whether prices show.</summary>
public sealed class ItemButtonTests : HubTestBase
{
    [Fact]
    public async Task A_till_starts_with_normal_buttons_and_prices()
    {
        var settings = await Cashier.GetJson<ItemButtonSettings>("/api/settings/item-buttons");
        Assert.Equal(ItemButtonSettings.Default, settings);
    }

    [Fact]
    public async Task Each_till_keeps_its_own_choice()
    {
        var saved = await (await Manager.Put("/api/settings/item-buttons", new SaveItemButtonsRequest(false, ItemButtonSize.Small, AllTills: false))).Read<ItemButtonSettings>();
        Assert.Equal(new ItemButtonSettings(false, ItemButtonSize.Small), saved);

        Assert.Equal(saved, await Cashier.GetJson<ItemButtonSettings>("/api/settings/item-buttons")); // same till (Till 1)
        Assert.Equal(ItemButtonSettings.Default, await Owner.GetJson<ItemButtonSettings>("/api/settings/item-buttons")); // the office PC

        Restart();
        Assert.Equal(saved, await Cashier.GetJson<ItemButtonSettings>("/api/settings/item-buttons"));
    }

    [Fact]
    public async Task One_save_can_set_every_till()
    {
        var code = await (await Owner.Post("/api/devices/pairing-code", new { })).Read<PairingCodeView>();
        var till2 = await (await Anonymous.Post("/api/devices/pair", new PairRequest(code.Code, "Till 2", DeviceType.Till))).Read<PairedDevice>();
        var cashier2 = ClientFor(await Host.Login(till2, TestShop.CashierPin));

        await Owner.Put("/api/settings/item-buttons", new SaveItemButtonsRequest(false, ItemButtonSize.Large, AllTills: true));

        foreach (var who in new[] { Owner, Cashier, cashier2 })
        {
            Assert.Equal(new ItemButtonSettings(false, ItemButtonSize.Large), await who.GetJson<ItemButtonSettings>("/api/settings/item-buttons"));
        }
    }

    [Fact]
    public async Task Only_the_owner_or_a_manager_changes_them_and_the_size_is_checked()
    {
        var cashier = await Cashier.Put("/api/settings/item-buttons", new SaveItemButtonsRequest(false, ItemButtonSize.Small, false));
        Assert.Equal(HttpStatusCode.Forbidden, cashier.StatusCode);

        var bad = await Manager.Put("/api/settings/item-buttons", new { showPrices = true, size = "Tiny", allTills = false });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(ItemButtonSettings.Default, await Cashier.GetJson<ItemButtonSettings>("/api/settings/item-buttons"));
    }
}
