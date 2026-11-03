using System.Globalization;
using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Orders;
using Pos.Domain.Payments;
using Pos.Domain.Pricing;
using Pos.Domain.Tax;

namespace Pos.Hub.Printing;

/// <summary>Kinds of kitchen ticket.</summary>
public enum KitchenTicketKind
{
    /// <summary>First items of an order.</summary>
    New,

    /// <summary>Items added later to the same order.</summary>
    Additional,

    /// <summary>Items removed after they were sent: stop making them.</summary>
    Void,
}

/// <summary>Builds receipts, kitchen tickets and report slips as <see cref="PrintDocument"/>s.</summary>
public static class Layouts
{
    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>
    /// A UK VAT receipt: shop details and VAT number, date and time, items in food-type order
    /// (with sizes, extras, deal choices, notes and offers), totals, VAT by rate, payments and change.
    /// A reprint is marked COPY.
    /// </summary>
    public static PrintDocument Receipt(Order order, OrderPayments? payments, ReceiptSettings shop, TimeZoneInfo timeZone, string? servedBy, bool isCopy)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(shop);
        ArgumentNullException.ThrowIfNull(timeZone);
        var lang = Lang.Parse(shop.Language);
        var blocks = new List<PrintBlock>();

        if (isCopy)
        {
            blocks.Add(new PrintText("*** COPY ***", PrintAlign.Center, PrintSize.Large, Bold: true));
        }

        blocks.Add(new PrintText(shop.BusinessName, PrintAlign.Center, PrintSize.Large, Bold: true));
        if (lang.Arabic && !string.IsNullOrWhiteSpace(shop.BusinessNameAr))
        {
            blocks.Add(new PrintText(shop.BusinessNameAr, PrintAlign.Center, PrintSize.Large, Bold: true));
        }

        blocks.AddRange(shop.AddressLines.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => new PrintText(a, PrintAlign.Center)));
        if (!string.IsNullOrWhiteSpace(shop.Phone))
        {
            blocks.Add(new PrintText($"Tel: {shop.Phone}", PrintAlign.Center));
        }

        if (!string.IsNullOrWhiteSpace(shop.VatNumber))
        {
            blocks.Add(new PrintText($"VAT No: {shop.VatNumber}", PrintAlign.Center, PrintSize.Small));
        }

        blocks.Add(new PrintRule());
        blocks.Add(new PrintText($"Order {order.Number.Display}", PrintAlign.Center, PrintSize.Huge, Bold: true));
        blocks.Add(new PrintText(OrderKind(order), PrintAlign.Center, Bold: true));
        blocks.Add(new PrintColumns(LocalTime(order.CreatedAt, timeZone), order.Number.Reference, PrintSize.Small));
        if (!string.IsNullOrWhiteSpace(servedBy))
        {
            blocks.Add(new PrintText($"Served by {servedBy}", PrintAlign.Start, PrintSize.Small));
        }

        blocks.Add(new PrintRule());

        var priced = PricedByLine(order);
        foreach (var line in order.LinesForReceipt)
        {
            var p = priced[line.Id];
            var vatCode = p.VatRate?.Code ?? string.Join('/', p.Components.Select(c => c.VatRate.Code).Distinct());
            blocks.Add(new PrintColumns($"{line.Pricing.Quantity} x {Name(line.Display, lang.English)}", $"{Money(p.LineTotal)} {vatCode}", Bold: true));
            AddArabicName(blocks, line.Display, lang, indent: 1);
            AddDetails(blocks, line.Display, lang, indent: 1, showPrices: false);
            if (p.Offer.Promotion is { } offer && p.Saving.MinorUnits > 0)
            {
                blocks.Add(new PrintColumns($"{offer.Name}", $"-{Money(p.Saving)}", PrintSize.Small, Indent: 1));
            }

            if (line.Note is { } note)
            {
                blocks.Add(new PrintText($"* {note}", Size: PrintSize.Small, Indent: 1));
            }
        }

        blocks.Add(new PrintRule());
        var t = order.Totals;
        blocks.Add(new PrintColumns("Subtotal", Money(t.GoodsTotal)));
        if (t.TotalSaving.MinorUnits > 0)
        {
            blocks.Add(new PrintColumns("Offers included", $"-{Money(t.TotalSaving)}", PrintSize.Small));
        }

        if (t.Discount is { } discount)
        {
            blocks.Add(new PrintColumns($"Discount ({discount.Input.Reason})", $"-{Money(discount.AmountOff)}"));
        }

        if (t.Delivery is { } delivery)
        {
            blocks.Add(new PrintColumns($"Delivery ({delivery.Quote.DistanceMiles.ToString("0.0", CultureInfo.InvariantCulture)} miles)", Money(delivery.Fee)));
        }

        if (t.ServiceCharge is { } service)
        {
            var label = service.Input.IsOptional ? "Optional service charge" : "Service charge";
            blocks.Add(new PrintColumns($"{label} {(service.Input.BasisPoints / 100m).ToString("0.##", CultureInfo.InvariantCulture)}%", Money(service.Amount)));
        }

        blocks.Add(new PrintColumns("TOTAL", Money(t.Total), PrintSize.Large, Bold: true));

        if (payments is { Payments.Count: > 0 })
        {
            blocks.Add(new PrintSpace(0.5));
            foreach (var payment in payments.Payments)
            {
                var name = payment.Method switch
                {
                    PaymentMethod.Cash => "Cash",
                    PaymentMethod.Card => "Card",
                    PaymentMethod.DeliveryApp => "Paid via app",
                    _ => "Paid online",
                };
                if (payment.IsRefund)
                {
                    blocks.Add(new PrintColumns($"Refund to {name.ToLowerInvariant()} ({payment.Reason})", Money(payment.Amount)));
                    continue;
                }

                blocks.Add(new PrintColumns(payment.Reference is { } r ? $"{name} (ref {r})" : name, Money(payment.Tendered ?? payment.Amount)));
                if (payment.Change is { MinorUnits: > 0 } change)
                {
                    blocks.Add(new PrintColumns("Change", Money(change), Bold: true));
                }

                if (payment.Tip.MinorUnits > 0)
                {
                    blocks.Add(new PrintColumns("Tip - thank you", Money(payment.Tip)));
                }
            }

            if (payments.Balance.MinorUnits > 0)
            {
                blocks.Add(new PrintColumns("STILL TO PAY", Money(payments.Balance), PrintSize.Large, Bold: true));
            }
        }

        if (t.VatSummary.Count > 0)
        {
            blocks.Add(new PrintRule());
            blocks.Add(new PrintColumns("VAT rate", "Net / VAT", PrintSize.Small, Bold: true));
            foreach (var v in t.VatSummary)
            {
                blocks.Add(new PrintColumns($"{v.Rate.Code} {v.Rate.Percent.ToString("0.##", CultureInfo.InvariantCulture)}%", $"{Money(v.Net)} / {Money(v.Vat)}", PrintSize.Small));
            }

            if (t.OutsideScopeOfVat.MinorUnits > 0)
            {
                blocks.Add(new PrintColumns("Outside the scope of VAT", Money(t.OutsideScopeOfVat), PrintSize.Small));
            }
        }

        blocks.Add(new PrintRule());
        if (!string.IsNullOrWhiteSpace(shop.Footer))
        {
            blocks.Add(new PrintText(shop.Footer, PrintAlign.Center));
        }

        if (lang.Arabic && !string.IsNullOrWhiteSpace(shop.FooterAr))
        {
            blocks.Add(new PrintText(shop.FooterAr, PrintAlign.Center));
        }

        return new PrintDocument($"{(isCopy ? "Receipt copy" : "Receipt")} {order.Number.Display}", blocks);
    }

    /// <summary>
    /// A kitchen ticket: order number, how it is going out, time, and the items in food-type order
    /// with sizes, extras, deal choices and notes, large and with no prices.
    /// </summary>
    public static PrintDocument KitchenTicket(Order order, IReadOnlyList<OrderLine> lines, KitchenTicketKind kind, ReceiptSettings shop, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(shop);
        var lang = Lang.Parse(shop.Language);
        var blocks = new List<PrintBlock>
        {
            kind switch
            {
                KitchenTicketKind.Void => new PrintText("VOID - DO NOT MAKE", PrintAlign.Center, PrintSize.Large, Bold: true, Inverted: true),
                KitchenTicketKind.Additional => new PrintText("ADDITIONAL ITEMS", PrintAlign.Center, PrintSize.Large, Bold: true, Inverted: true),
                _ => new PrintText("NEW ORDER", PrintAlign.Center, PrintSize.Large, Bold: true),
            },
            new PrintText(order.Number.Display, PrintAlign.Center, PrintSize.Huge, Bold: true),
            new PrintText(OrderKind(order), PrintAlign.Center, PrintSize.Large, Bold: true),
            new PrintText($"Ordered {LocalTime(order.CreatedAt, timeZone)}", PrintAlign.Center),
            new PrintRule(),
        };

        foreach (var line in lines.OrderBy(l => l.Display.CourseSortOrder).ThenBy(l => l.EntryNumber))
        {
            blocks.Add(new PrintText($"{line.Pricing.Quantity} x {Name(line.Display, lang.English)}", Size: PrintSize.Large, Bold: true));
            AddArabicName(blocks, line.Display, lang, indent: 1, PrintSize.Large);
            AddDetails(blocks, line.Display, lang, indent: 1, showPrices: false, PrintSize.Large);
            if (line.Note is { } note)
            {
                blocks.Add(new PrintText($"** {note} **", Size: PrintSize.Large, Bold: true, Indent: 1));
            }

            if (kind == KitchenTicketKind.Void && line.VoidReason is { } reason)
            {
                blocks.Add(new PrintText($"Reason: {reason}", Indent: 1));
            }

            blocks.Add(new PrintSpace(0.4));
        }

        blocks.Add(new PrintRule());
        blocks.Add(new PrintText($"{lines.Sum(l => l.Pricing.Quantity)} item(s)", PrintAlign.Center, PrintSize.Small));
        var title = kind switch
        {
            KitchenTicketKind.Void => "Kitchen void",
            KitchenTicketKind.Additional => "Kitchen additional",
            _ => "Kitchen",
        };
        return new PrintDocument($"{title} {order.Number.Display}", blocks);
    }

    /// <summary>The Z report as a printed slip.</summary>
    public static PrintDocument ZReport(DayReport report, ReceiptSettings shop, Func<Guid, string> staffName)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(shop);
        ArgumentNullException.ThrowIfNull(staffName);
        var b = new List<PrintBlock>
        {
            new PrintText(shop.BusinessName, PrintAlign.Center, PrintSize.Large, Bold: true),
            new PrintText($"Z REPORT {report.ZNumber}", PrintAlign.Center, PrintSize.Huge, Bold: true),
            new PrintText(report.BusinessDate.ToString("dddd d MMMM yyyy", Uk), PrintAlign.Center),
            new PrintRule(),
            new PrintText("SALES", Bold: true),
            new PrintColumns("Completed orders", report.Sales.Orders.ToString(CultureInfo.InvariantCulture)),
            new PrintColumns("Items at menu price", Money(report.Sales.ItemsAtMenuPrice)),
            new PrintColumns("Offers", $"-{Money(report.Sales.OfferSavings)}"),
            new PrintColumns("Discounts", $"-{Money(report.Sales.Discounts)}"),
            new PrintColumns("Delivery fees", Money(report.Sales.DeliveryFees)),
            new PrintColumns("Service charges", Money(report.Sales.ServiceCharges)),
            new PrintColumns("TOTAL SALES", Money(report.Sales.Total), PrintSize.Large, Bold: true),
        };

        b.Add(new PrintRule());
        b.Add(new PrintText("BY SOURCE", Bold: true));
        b.AddRange(report.BySource.Select(s => new PrintColumns($"{s.Source} ({s.Orders})", Money(s.Total))));

        b.Add(new PrintRule());
        b.Add(new PrintText("PAYMENTS", Bold: true));
        foreach (var m in report.Payments)
        {
            b.Add(new PrintColumns($"{m.Method} ({m.Payments})", Money(m.Taken)));
            if (m.Refunds > 0)
            {
                b.Add(new PrintColumns($"{m.Method} refunds ({m.Refunds})", $"-{Money(m.Refunded)}", Indent: 1));
            }

            if (m.Tips.MinorUnits > 0)
            {
                b.Add(new PrintColumns($"{m.Method} tips", Money(m.Tips), Indent: 1));
            }
        }

        b.Add(new PrintColumns("NET TAKINGS", Money(report.Payments.Aggregate(Pos.Domain.Common.Money.Gbp(0), (s, m) => s + m.Net)), Bold: true));

        b.Add(new PrintRule());
        b.Add(new PrintText("VAT", Bold: true));
        foreach (var v in report.Vat)
        {
            b.Add(new PrintColumns($"{v.Code} {v.Percent.ToString("0.##", CultureInfo.InvariantCulture)}% gross", Money(v.Gross)));
            b.Add(new PrintColumns("Net / VAT", $"{Money(v.Net)} / {Money(v.Vat)}", PrintSize.Small, Indent: 1));
        }

        b.Add(new PrintRule());
        b.Add(new PrintText("CHECKS", Bold: true));
        var e = report.Exceptions;
        b.Add(new PrintColumns($"Cancelled orders ({e.CancelledOrders})", Money(e.CancelledValue)));
        b.Add(new PrintColumns($"Removed items ({e.VoidedItems})", Money(e.VoidedValue)));
        b.Add(new PrintColumns($"Refunds ({e.Refunds})", Money(e.RefundedValue)));
        b.Add(new PrintColumns("Rejected online orders", e.RejectedOrders.ToString(CultureInfo.InvariantCulture)));
        b.Add(new PrintColumns("Orders not finished", e.OpenOrders.ToString(CultureInfo.InvariantCulture), Bold: e.OpenOrders > 0));

        b.Add(new PrintRule());
        b.Add(new PrintText("CASH", Bold: true));
        foreach (var s in report.Shifts)
        {
            b.Add(new PrintColumns("Expected", Money(s.ExpectedCash)));
            b.Add(new PrintColumns("Counted", s.CountedCash is { } c ? Money(c) : "-"));
            b.Add(new PrintColumns("Over (+) / short (-)", s.Variance is { } v ? Money(v) : "-", Bold: true));
        }

        if (report.TipsByStaff.Count > 0)
        {
            b.Add(new PrintRule());
            b.Add(new PrintText("CARD TIPS BY STAFF", Bold: true));
            b.AddRange(report.TipsByStaff.Select(tip => new PrintColumns(staffName(tip.UserId), Money(tip.Tips))));
        }

        b.Add(new PrintRule());
        b.Add(new PrintText($"Taken {report.CreatedAt.UtcDateTime:dd/MM/yyyy HH:mm} UTC by {staffName(report.CreatedBy)}", PrintAlign.Center, PrintSize.Small));
        return new PrintDocument($"Z report {report.ZNumber}", b);
    }

    /// <summary>Money as on a UK receipt: £12.50, -£2.00.</summary>
    public static string Money(Money amount) =>
        (amount.IsNegative ? "-" : string.Empty) + (amount.Currency == "GBP" ? "£" : amount.Currency + " ")
        + (Math.Abs(amount.MinorUnits) / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    private static Dictionary<Guid, PricedLine> PricedByLine(Order order) =>
        order.ActiveLines.Zip(order.Totals.Lines).ToDictionary(x => x.First.Id, x => x.Second);

    private static string OrderKind(Order order)
    {
        var type = order.Type switch
        {
            OrderType.EatIn => "EAT IN",
            OrderType.Delivery => "DELIVERY",
            _ => "TAKEAWAY",
        };
        var source = order.Source switch
        {
            OrderSource.Counter => null,
            OrderSource.Table => "Table",
            OrderSource.JustEat => "Just Eat",
            OrderSource.UberEats => "Uber Eats",
            _ => order.Source.ToString(),
        };
        return source is null ? type : $"{type} - {source}";
    }

    private static string LocalTime(DateTimeOffset at, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(at, timeZone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Name(LineDisplay display, bool english)
    {
        var name = english || display.Name.Ar is null ? display.Name.En : display.Name.Ar;
        var size = display.Size is { } s ? (english || s.Ar is null ? s.En : s.Ar) : null;
        return size is null ? name : $"{name} ({size})";
    }

    private static void AddArabicName(List<PrintBlock> blocks, LineDisplay display, Lang lang, int indent, PrintSize size = PrintSize.Normal)
    {
        if (lang.Both && display.Name.Ar is { } ar)
        {
            blocks.Add(new PrintText(ar, PrintAlign.Start, size, Indent: indent));
        }
    }

    private static void AddDetails(List<PrintBlock> blocks, LineDisplay display, Lang lang, int indent, bool showPrices, PrintSize size = PrintSize.Normal)
    {
        _ = showPrices;
        var smaller = size == PrintSize.Large ? PrintSize.Normal : PrintSize.Small;
        foreach (var extra in display.Extras)
        {
            blocks.Add(new PrintText($"+ {Text(extra, lang)}", Size: smaller, Indent: indent));
        }

        foreach (var item in display.DealItems)
        {
            blocks.Add(new PrintText($"- {Name(item, lang.English)}", Size: smaller, Bold: true, Indent: indent));
            if (lang.Both && item.Name.Ar is { } ar)
            {
                blocks.Add(new PrintText(ar, Size: smaller, Indent: indent + 1));
            }

            foreach (var extra in item.Extras)
            {
                blocks.Add(new PrintText($"+ {Text(extra, lang)}", Size: smaller, Indent: indent + 1));
            }
        }
    }

    private static string Text(LocalizedText text, Lang lang) =>
        lang.Both && text.Ar is { } ar ? $"{text.En} / {ar}" : (lang.English || text.Ar is null ? text.En : text.Ar);

    /// <summary>Which languages to print.</summary>
    private readonly record struct Lang(bool English, bool Arabic)
    {
        public bool Both => English && Arabic;

        public static Lang Parse(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "ar" => new Lang(false, true),
            "both" => new Lang(true, true),
            _ => new Lang(true, false),
        };
    }
}
