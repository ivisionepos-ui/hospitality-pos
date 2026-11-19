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

    [Fact]
    public async Task Item_and_category_buttons_can_have_exact_pixel_sizes()
    {
        var saved = await (await Manager.Put("/api/settings/item-buttons", new SaveItemButtonsRequest(true, ItemButtonSize.Custom, false, 120, 70, true, 110, 44))).Read<ItemButtonSettings>();
        Assert.Equal(new ItemButtonSettings(true, ItemButtonSize.Custom, 120, 70, true, 110, 44), saved);

        Restart();
        Assert.Equal(saved, await Cashier.GetJson<ItemButtonSettings>("/api/settings/item-buttons"));

        // Pixel sizes are kept only where used: back to automatic forgets them.
        var auto = await (await Manager.Put("/api/settings/item-buttons", new SaveItemButtonsRequest(true, ItemButtonSize.Small, false, 120, 70, false, 110, 44))).Read<ItemButtonSettings>();
        Assert.Equal(new ItemButtonSettings(true, ItemButtonSize.Small), auto);
    }

    [Fact]
    public async Task Right_hand_layout_is_kept_per_till_and_across_restarts()
    {
        var saved = await (await Manager.Put("/api/settings/item-buttons", new SaveItemButtonsRequest(true, ItemButtonSize.Normal, false, OrderOnRight: true))).Read<ItemButtonSettings>();
        Assert.True(saved.OrderOnRight);

        Restart();
        Assert.True((await Cashier.GetJson<ItemButtonSettings>("/api/settings/item-buttons")).OrderOnRight);

        // Older saves without the setting keep the usual layout (order on the left).
        await Manager.Put("/api/settings/item-buttons", new { showPrices = true, size = "Normal", allTills = false });
        Assert.False((await Cashier.GetJson<ItemButtonSettings>("/api/settings/item-buttons")).OrderOnRight);
    }

    [Theory]
    [InlineData(59, 70, false, null, null, "Item button width must be between 60 and 400 pixels.")]
    [InlineData(120, 301, false, null, null, "Item button height must be between 40 and 300 pixels.")]
    [InlineData(null, 70, false, null, null, "Item button width must be between 60 and 400 pixels.")]
    [InlineData(120, 70, true, 401, 44, "Category button width must be between 50 and 400 pixels.")]
    [InlineData(120, 70, true, 110, 35, "Category button height must be between 36 and 200 pixels.")]
    public async Task Pixel_sizes_must_be_tappable_and_fit_the_screen(int? width, int? height, bool customCategories, int? catWidth, int? catHeight, string message)
    {
        var refused = await Manager.Put("/api/settings/item-buttons", new SaveItemButtonsRequest(true, ItemButtonSize.Custom, false, width, height, customCategories, catWidth, catHeight));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(message, (await refused.Read<ErrorView>()).Message);
        Assert.Equal(ItemButtonSettings.Default, await Cashier.GetJson<ItemButtonSettings>("/api/settings/item-buttons"));
    }
}
