using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pos.Hub.Api;
using Pos.Hub.Delivery;
using Pos.Hub.Printing;

namespace Pos.Hub.Tests;

/// <summary>More → Shop details: name and address for receipts, and the postcode deliveries are measured from.</summary>
public sealed class ShopDetailsTests : HubTestBase
{
    private static readonly ShopDetails Olive = new("  Olive   Grill ", "12 High Street", "", "Leeds", "ls29jt", "0113 4960000", "gb123456789", "Thank you!");

    [Fact]
    public async Task Owner_saves_the_details_tidied_and_receipts_use_them_at_once()
    {
        var saved = await (await Owner.Put("/api/settings/shop", Olive)).Read<ShopDetails>();

        Assert.Equal("Olive Grill", saved.BusinessName);
        Assert.Null(saved.AddressLine2);
        Assert.Equal("LS2 9JT", saved.Postcode);
        Assert.Equal("01134 960000", saved.Phone);
        Assert.Equal("GB 123 4567 89", saved.VatNumber);

        var receipt = Host.Services.GetRequiredService<PrintSettings>().ReceiptText;
        Assert.Equal("Olive Grill", receipt.BusinessName);
        Assert.Equal(["12 High Street", "Leeds LS2 9JT"], receipt.AddressLines);
        Assert.Equal("GB 123 4567 89", receipt.VatNumber);
        Assert.Equal("Thank you!", receipt.Footer);
    }

    [Fact]
    public async Task Delivery_distances_are_measured_from_the_new_postcode()
    {
        Assert.Equal(1.3m, (await Cashier.GetJson<PostcodeCheck>("/api/delivery/check?postcode=LS2 9JT")).DistanceMiles); // from LS1 4AP

        await Owner.Put("/api/settings/shop", Olive); // the shop is now in LS2 9JT

        Assert.Equal(0m, (await Cashier.GetJson<PostcodeCheck>("/api/delivery/check?postcode=LS2 9JT")).DistanceMiles);
        Assert.Equal(2.6m, (await Cashier.GetJson<PostcodeCheck>("/api/delivery/check?postcode=LS7 3AA")).DistanceMiles);
    }

    [Fact]
    public async Task Details_are_kept_after_a_restart()
    {
        await Owner.Put("/api/settings/shop", Olive);

        Restart();

        Assert.Equal("Olive Grill", (await Cashier.GetJson<ShopDetails>("/api/settings/shop")).BusinessName);
        Assert.Equal("Olive Grill", Host.Services.GetRequiredService<PrintSettings>().ReceiptText.BusinessName);
        Assert.Equal("LS2 9JT", Host.Services.GetRequiredService<DeliverySettings>().ShopPostcode);
    }

    [Theory]
    [InlineData("", "Enter the shop's name")]
    [InlineData("postcode", "not a UK postcode")]
    [InlineData("vat", "not a UK VAT number")]
    [InlineData("phone", "does not look like a phone number")]
    public async Task Mistakes_are_explained_and_nothing_is_saved(string wrong, string message)
    {
        var details = wrong switch
        {
            "" => Olive with { BusinessName = " " },
            "postcode" => Olive with { Postcode = "12345" },
            "vat" => Olive with { VatNumber = "12345" },
            _ => Olive with { Phone = "call us" },
        };

        var response = await Owner.Put("/api/settings/shop", details);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, (await response.Read<ErrorView>()).Message, StringComparison.Ordinal);
        Assert.NotEqual("Olive Grill", (await Cashier.GetJson<ShopDetails>("/api/settings/shop")).BusinessName);
    }

    [Fact]
    public async Task Only_the_owner_changes_them()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Manager.Put("/api/settings/shop", Olive)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Cashier.GetAsync(new Uri("/api/settings/shop", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Shop_postcode_can_be_checked_before_saving()
    {
        var found = await Owner.GetJson<PostcodeLocation>("/api/delivery/locate?postcode=ls29jt");
        var unknown = await Owner.GetAsync(new Uri("/api/delivery/locate?postcode=LS99 9ZZ", UriKind.Relative));
        Host.Postcodes.Down = true;
        var offline = await Owner.GetAsync(new Uri("/api/delivery/locate?postcode=LS7 3AA", UriKind.Relative));

        Assert.Equal("LS2 9JT", found.Postcode);
        Assert.Equal("Leeds", found.Town);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, offline.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.GetAsync(new Uri("/api/delivery/locate?postcode=LS2 9JT", UriKind.Relative))).StatusCode);
    }
}
