using Pos.Domain.Common;
using Pos.Domain.Payments;
using Pos.Domain.Pricing;
using Pos.Domain.Promotions;
using Pos.Domain.Tax;

namespace Pos.Domain.Tests.Payments;

public class PaymentTests
{
    private static readonly Guid Order = Guid.NewGuid();
    private static readonly Guid Shift = Guid.NewGuid();
    private static readonly Guid Cashier = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 18, 0, 0, TimeSpan.Zero);

    private static PaymentContext Ctx(int minute = 0) => new(Shift, Cashier, null, Now.AddMinutes(minute));

    private static OrderPayments Ledger(long totalPence) => new(Order, Money.Gbp(totalPence), []);

    [Fact]
    public void Cash_gives_change_and_settles_the_order()
    {
        var ledger = Ledger(1139);

        var payment = ledger.TakeCash(Money.Gbp(2000), Ctx());

        Assert.Equal(Money.Gbp(1139), payment.Amount);
        Assert.Equal(Money.Gbp(861), payment.Change);
        Assert.Equal(Money.Gbp(2000), payment.Tendered);
        Assert.True(ledger.IsSettled);
    }

    [Fact]
    public void Exact_cash_gives_no_change()
    {
        var ledger = Ledger(1139);

        Assert.Equal(Money.Gbp(0), ledger.TakeCash(Money.Gbp(1139), Ctx()).Change);
    }

    [Fact]
    public void Part_cash_then_card_for_the_rest()
    {
        var ledger = Ledger(2500);

        ledger.TakeCash(Money.Gbp(1000), Ctx());
        Assert.Equal(Money.Gbp(1500), ledger.Balance);

        var card = ledger.TakeCard(Money.Gbp(1500), Ctx(1), tip: Money.Gbp(200), reference: " 004512 ");

        Assert.True(ledger.IsSettled);
        Assert.Equal(Money.Gbp(200), ledger.Tips);
        Assert.Equal("004512", card.Reference);
        Assert.Equal(Money.Gbp(2500), ledger.Paid); // tips are not part of the order total
    }

    [Fact]
    public void Cannot_pay_more_than_is_owed_or_pay_a_paid_order()
    {
        var ledger = Ledger(1000);

        Assert.Throws<InvalidOperationException>(() => ledger.TakeCard(Money.Gbp(1001), Ctx()));
        ledger.TakeCard(Money.Gbp(1000), Ctx());
        Assert.Throws<InvalidOperationException>(() => ledger.TakeCash(Money.Gbp(500), Ctx(1)));
    }

    [Fact]
    public void Cash_for_a_set_share_must_cover_it()
    {
        var ledger = Ledger(3000);

        Assert.Throws<ArgumentException>(() => ledger.TakeCash(Money.Gbp(900), Ctx(), amount: Money.Gbp(1000)));
        var share = ledger.TakeCash(Money.Gbp(2000), Ctx(), amount: Money.Gbp(1000));

        Assert.Equal(Money.Gbp(1000), share.Change);
        Assert.Equal(Money.Gbp(2000), ledger.Balance);
    }

    [Fact]
    public void Rejects_zero_negative_and_wrong_currency_amounts_and_tips_on_cash()
    {
        var ledger = Ledger(1000);

        Assert.Throws<ArgumentException>(() => ledger.TakeCash(Money.Gbp(0), Ctx()));
        Assert.Throws<ArgumentException>(() => ledger.TakeCard(Money.Gbp(-1), Ctx()));
        Assert.Throws<ArgumentException>(() => ledger.TakeCard(new Money(100, "EUR"), Ctx()));
        Assert.Throws<ArgumentException>(() => ledger.TakeCard(Money.Gbp(100), Ctx(), tip: Money.Gbp(-5)));
        Assert.Throws<ArgumentException>(() => ledger.TakeOther(PaymentMethod.Online, Money.Gbp(100), Ctx(), tip: Money.Gbp(5)));
        Assert.Throws<ArgumentException>(() => ledger.TakeOther(PaymentMethod.Cash, Money.Gbp(100), Ctx()));
    }

    [Fact]
    public void Same_payment_id_cannot_be_used_twice()
    {
        var ledger = Ledger(1000);
        var id = Guid.NewGuid();
        ledger.TakeCard(Money.Gbp(400), Ctx() with { PaymentId = id });

        Assert.Throws<InvalidOperationException>(() => ledger.TakeCard(Money.Gbp(400), Ctx(1) with { PaymentId = id }));
    }

    [Fact]
    public void Refunds_go_back_the_same_way_and_never_exceed_what_was_paid()
    {
        var ledger = Ledger(2000);
        var card = ledger.TakeCard(Money.Gbp(2000), Ctx());

        var first = ledger.Refund(card.Id, Money.Gbp(500), "Cold food", Manager, Ctx(5));

        Assert.Equal(PaymentMethod.Card, first.Method);
        Assert.Equal(Money.Gbp(-500), first.Amount);
        Assert.Equal(card.Id, first.RefundOf);
        Assert.Equal(Manager, first.ApprovedByUserId);
        Assert.Equal(Money.Gbp(1500), ledger.Refundable(card.Id));
        Assert.Equal(Money.Gbp(500), first.SaleReduction); // money back for the food: the sale goes down
        Assert.Equal(Money.Gbp(0), ledger.Balance);        // and the customer does not owe it again
        Assert.Equal(Money.Gbp(1500), ledger.Due);

        Assert.Throws<InvalidOperationException>(() => ledger.Refund(card.Id, Money.Gbp(1501), "Too much", Manager, Ctx(6)));
        ledger.Refund(card.Id, Money.Gbp(1500), "Rest", Manager, Ctx(7));
        Assert.Equal(Money.Gbp(0), ledger.Paid);
    }

    [Fact]
    public void Giving_back_an_overpayment_does_not_lower_the_sale_and_the_rest_of_a_bigger_refund_does()
    {
        var card = Ledger(1000).TakeCard(Money.Gbp(1000), Ctx());
        var ledger = new OrderPayments(Order, Money.Gbp(800), [card]); // an item removed after paying: the order is now £8
        Assert.Equal(Money.Gbp(-200), ledger.Balance);

        var refund = ledger.Refund(card.Id, Money.Gbp(300), "Removed item, and £1 off for the wait", Manager, Ctx(1));

        Assert.Equal(Money.Gbp(100), refund.SaleReduction); // £2 was overpaid; only the extra £1 lowers the sale
        Assert.Equal(Money.Gbp(0), ledger.Balance);
        Assert.True(ledger.IsSettled);
    }

    [Fact]
    public void Refund_needs_a_reason_a_manager_and_a_real_payment()
    {
        var ledger = Ledger(1000);
        var cash = ledger.TakeCash(Money.Gbp(1000), Ctx());

        Assert.Throws<ArgumentException>(() => ledger.Refund(cash.Id, Money.Gbp(100), " ", Manager, Ctx(1)));
        Assert.Throws<ArgumentException>(() => ledger.Refund(cash.Id, Money.Gbp(100), "Why", Guid.Empty, Ctx(1)));
        Assert.Throws<KeyNotFoundException>(() => ledger.Refund(Guid.NewGuid(), Money.Gbp(100), "Why", Manager, Ctx(1)));
        var refund = ledger.Refund(cash.Id, Money.Gbp(100), "Why", Manager, Ctx(1));
        Assert.Throws<KeyNotFoundException>(() => ledger.Refund(refund.Id, Money.Gbp(10), "Refund a refund", Manager, Ctx(2)));
    }

    [Fact]
    public void Balance_goes_negative_when_the_order_shrinks_after_payment()
    {
        var paid = new OrderPayments(Order, Money.Gbp(2000), [Ledger(2000).TakeCard(Money.Gbp(2000), Ctx())]);
        var afterVoid = new OrderPayments(Order, Money.Gbp(1500), paid.Payments);

        Assert.Equal(Money.Gbp(-500), afterVoid.Balance);
        Assert.False(afterVoid.IsSettled);
    }

    [Theory]
    [InlineData(1000, 3, new long[] { 334, 333, 333 })]
    [InlineData(2499, 2, new long[] { 1250, 1249 })]
    public void Split_equally_to_the_penny(long amount, int parts, long[] expected)
    {
        Assert.Equal(expected.Select(Money.Gbp), BillSplitter.Equally(Money.Gbp(amount), parts));
    }

    [Fact]
    public void Split_must_be_two_to_fifty_people()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BillSplitter.Equally(Money.Gbp(100), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BillSplitter.Equally(Money.Gbp(100), 51));
    }

    [Fact]
    public void Split_by_items_shares_discount_and_service_and_adds_up_to_the_total()
    {
        var context = new PricingContext(Now, TimeZoneInfo.Utc, Guid.NewGuid(), SalesChannel.Pos);
        ItemLineInput Item(long pence) => new()
        {
            Item = new PricingItem(Guid.NewGuid(), null, Guid.NewGuid(), Money.Gbp(pence)), VatCategory = VatCategory.HotFood, Quantity = 1,
        };
        var order = new OrderPricer(new UkVatPolicy(), []).Price(new OrderPricingRequest
        {
            OrderType = OrderType.EatIn, Context = context, Lines = [Item(1200), Item(800), Item(500)],
            Discount = new OrderDiscountInput(OrderDiscountType.Amount, 250, "Birthday", Cashier, Manager),
            ServiceCharge = new ServiceChargeInput(1000),
        });

        var first = BillSplitter.ForLines(order, [0]);
        var rest = BillSplitter.ForLines(order, [1, 2]);

        // £25.00 - £2.50 = £22.50, +10% service £2.25 = £24.75
        Assert.Equal(Money.Gbp(2475), order.Total);
        Assert.Equal(order.Total, first + rest);
        Assert.Equal(Money.Gbp(1188), first); // 1200 - 120 discount = 1080, + 108 service
        Assert.Throws<ArgumentOutOfRangeException>(() => BillSplitter.ForLines(order, [3]));
    }
}
