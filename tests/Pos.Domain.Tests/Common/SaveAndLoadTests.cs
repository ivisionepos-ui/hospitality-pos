using Pos.Domain.Common;
using Pos.Domain.Delivery;
using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Domain.Pricing;
using Pos.Domain.Promotions;
using Pos.Domain.Tax;
using Pos.Domain.Tests.Menus;

namespace Pos.Domain.Tests.Common;

/// <summary>Menus and orders survive being saved as JSON and loaded again, exactly.</summary>
public class SaveAndLoadTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly OrderActor Cashier = new(Guid.NewGuid(), Guid.NewGuid());
    private static readonly Guid Manager = Guid.NewGuid();

    [Fact]
    public void Money_round_trips_as_pence_and_currency()
    {
        var json = PosJson.Serialize(Money.Gbp(1250));

        Assert.Equal("""{"minorUnits":1250,"currency":"GBP"}""", json);
        Assert.Equal(Money.Gbp(1250), PosJson.Deserialize<Money>(json));
    }

    [Fact]
    public void Menu_round_trips_with_arabic_names_deals_and_extras()
    {
        var s = new SampleMenu();
        s.Menu.SetSoldOut(s.Can, true, SampleMenu.Owner);

        var json = PosJson.Serialize(s.Menu.ToState());
        var loaded = Menu.FromState(PosJson.Deserialize<MenuState>(json));

        Assert.Equal(s.Menu.Version, loaded.Version);
        Assert.Equal(s.Menu.Categories.Select(c => c.Id), loaded.Categories.Select(c => c.Id));
        Assert.Equal("مارغريتا", loaded.GetProduct(s.Margherita).Name.For("ar"));
        Assert.True(loaded.GetProduct(s.Can).IsSoldOut);
        Assert.Equal(3, loaded.GetProduct(s.Deal).DealSlots.Count);
        Assert.Equal(Money.Gbp(150), loaded.GetProduct(s.Deal).DealSlots[0].Options[1].Upcharge);
        Assert.Equal(json, PosJson.Serialize(loaded.ToState())); // nothing lost or changed
    }

    [Fact]
    public void Loaded_menu_keeps_counting_versions_and_can_be_edited()
    {
        var s = new SampleMenu();
        var loaded = Menu.FromState(PosJson.Deserialize<MenuState>(PosJson.Serialize(s.Menu.ToState())));

        loaded.SetPrice(s.Can, s.CanRegular, Money.Gbp(130), Manager);

        Assert.Equal(s.Menu.Version + 1, loaded.Version);
        Assert.Equal(Money.Gbp(130), loaded.GetProduct(s.Can).Variants[0].Price);
    }

    [Fact]
    public void Import_checks_everything_and_rebuilds_the_same_menu()
    {
        var s = new SampleMenu();

        var imported = Menu.Import(s.Menu.ToState(), Manager);

        Assert.Equal(s.Menu.AllProducts.Select(p => p.Id), imported.AllProducts.Select(p => p.Id));
        var broken = s.Menu.ToState() with { Products = [.. s.Menu.AllProducts.Where(p => p.Id != s.Doner)] }; // deal points at the missing doner
        Assert.Throws<ArgumentException>(() => Menu.Import(broken, Manager));
    }

    [Fact]
    public void Order_round_trips_with_deal_discount_delivery_and_history()
    {
        var s = new SampleMenu();
        var builder = new MenuOrderBuilder(s.Menu);
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero));
        var setup = new OrderPricingSetup(new UkVatPolicy(), [], London, new DiscountPolicy(1000, Money.Gbp(500)));
        var intake = new OrderIntake(new InMemoryOrderStore(), new InMemoryOrderNumberSequence(), setup, new TimeOnly(5, 0), clock);

        var deal = new DealSelection(s.Deal, s.DealRegular,
        [
            new SlotChoice(s.PizzaSlot, s.Pepperoni, s.Pepperoni12, [s.ExtraCheese]),
            new SlotChoice(s.KebabSlot, s.Doner, s.DonerRegular, [s.Garlic]),
            new SlotChoice(s.DrinkSlot, s.Bottle, s.BottleRegular, []),
        ]);
        var can = new ItemSelection(s.Can, s.CanRegular, []);
        var order = intake.Submit(new NewOrder
        {
            OrderId = DeviceIds.New(clock), IdempotencyKey = "till1-0001", BranchId = Guid.NewGuid(), BranchCode = "LDN1",
            Source = OrderSource.Phone, Type = OrderType.Delivery, CreatedBy = Cashier,
            Lines = [new(builder.ToLine(deal), builder.DisplayFor(deal), "Ring the bell"), new(builder.ToLine(can), builder.DisplayFor(can))],
        }).Order;
        order.ApplyDiscount(new OrderDiscountInput(OrderDiscountType.Amount, 300, "Regular", Cashier.UserId), Cashier);
        order.SetDelivery(new DeliveryInput(2.1m, DeliveryChargeTable.Banded([new DeliveryBand(0m, 3m, Money.Gbp(250))], 3m)), Cashier);
        order.SendToKitchen(Cashier);
        order.VoidLine(order.Lines[1].Id, "Out of stock", Cashier, Manager);

        var json = PosJson.Serialize(order.ToState());
        var loaded = Order.Restore(PosJson.Deserialize<OrderState>(json), setup, clock);

        Assert.Equal(order.Totals.Total, loaded.Totals.Total);
        Assert.Equal(PosJson.Serialize(order.Totals.VatSummary), PosJson.Serialize(loaded.Totals.VatSummary));
        Assert.Equal(order.Number, loaded.Number);
        Assert.Equal(order.History.Count, loaded.History.Count);
        Assert.Equal("Ring the bell", loaded.Lines[0].Note);
        Assert.True(loaded.Lines[1].IsVoided);
        Assert.IsType<MealDealLineInput>(loaded.Lines[0].Pricing);
        Assert.Equal(json, PosJson.Serialize(loaded.ToState()));

        // The loaded order carries on working.
        loaded.MarkReady(Cashier);
        Assert.Equal(OrderStatus.Ready, loaded.Status);
    }

    [Fact]
    public void Enums_are_saved_as_words()
    {
        var json = PosJson.Serialize(new { Status = OrderStatus.Accepted, Kind = PromotionType.PercentOff });

        Assert.Equal("""{"status":"Accepted","kind":"PercentOff"}""", json);
    }
}
