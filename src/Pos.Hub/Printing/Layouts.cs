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
    /// A reprint is marked COPY. Which parts print, their order and the item size follow the owner's layout
    /// (the delivery layout for delivery orders); parts a VAT receipt must have always print.
    /// </summary>
    public static PrintDocument Receipt(Order order, OrderPayments? payments, ReceiptSettings shop, TimeZoneInfo timeZone, string? servedBy, bool isCopy, ReceiptLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(shop);
        ArgumentNullException.ThrowIfNull(timeZone);
        var kind = order.Type == OrderType.Delivery ? ReceiptKind.Delivery : ReceiptKind.Receipt;
        layout ??= ReceiptParts.Standard(kind);
        var lang = Lang.Parse(layout.Language ?? shop.Language);
        var itemSize = layout.ItemSize;
        var blocks = new List<PrintBlock>();
        if (isCopy)
        {
            blocks.Add(new PrintText("*** COPY ***", PrintAlign.Center, PrintSize.Large, Bold: true));
        }

        var parts = new PartWriter(blocks, ReceiptParts.Receipt);
        var t = order.Totals;
        foreach (var part in Printed(kind, layout, !string.IsNullOrWhiteSpace(shop.VatNumber)))
        {
            var b = new List<PrintBlock>();
            switch (part)
            {
                case "shopName":
                    b.Add(new PrintText(shop.BusinessName, PrintAlign.Center, PrintSize.Large, Bold: true));
                    if (lang.Arabic && !string.IsNullOrWhiteSpace(shop.BusinessNameAr))
                    {
                        b.Add(new PrintText(shop.BusinessNameAr, PrintAlign.Center, PrintSize.Large, Bold: true));
                    }

                    break;
                case "address":
                    b.AddRange(shop.AddressLines.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => new PrintText(a, PrintAlign.Center)));
                    break;
                case "phone" when !string.IsNullOrWhiteSpace(shop.Phone):
                    b.Add(new PrintText($"Tel: {shop.Phone}", PrintAlign.Center));
                    break;
                case "vatNumber" when !string.IsNullOrWhiteSpace(shop.VatNumber):
                    b.Add(new PrintText($"VAT No: {shop.VatNumber}", PrintAlign.Center, PrintSize.Small));
                    break;
                case "topText" or "bottomText":
                    AddText(b, part == "topText" ? layout.TopText : layout.BottomText);
                    break;
                case "orderNumber":
                    b.Add(new PrintText($"Order {order.Number.Display}", PrintAlign.Center, PrintSize.Huge, Bold: true));
                    break;
                case "orderType":
                    b.Add(new PrintText(OrderKind(order), PrintAlign.Center, Bold: true));
                    break;
                case "table" when order.Table is { } receiptTable:
                    b.Add(new PrintText($"Table {receiptTable}", PrintAlign.Center, PrintSize.Large, Bold: true));
                    break;
                case "dateTime":
                    b.Add(new PrintColumns(LocalTime(order.CreatedAt, timeZone), order.Number.Reference, PrintSize.Small));
                    break;
                case "servedBy" when !string.IsNullOrWhiteSpace(servedBy):
                    b.Add(new PrintText($"Served by {servedBy}", PrintAlign.Start, PrintSize.Small));
                    break;
                case "customer":
                    AddContact(b, order, PrintSize.Normal);
                    break;
                case "items":
                    AddReceiptItems(b, order, lang, itemSize);
                    break;
                case "totals":
                    AddTotals(b, t);
                    break;
                case "payments":
                    AddPayments(b, payments);
                    break;
                case "amountDue" when order.Type == OrderType.Delivery:
                    // The driver must see at a glance whether to collect money.
                    var due = payments?.Balance ?? t.Total;
                    b.Add(new PrintSpace(0.5));
                    b.Add(due.MinorUnits > 0
                        ? new PrintText($"AMOUNT DUE {Money(due)}", PrintAlign.Center, PrintSize.Large, Bold: true, Inverted: true)
                        : new PrintText("PAID - NOTHING TO COLLECT", PrintAlign.Center, PrintSize.Large, Bold: true));
                    break;
                case "vatSummary":
                    AddVatSummary(b, t);
                    break;
                case "footer":
                    if (!string.IsNullOrWhiteSpace(shop.Footer))
                    {
                        b.Add(new PrintText(shop.Footer, PrintAlign.Center));
                    }

                    if (lang.Arabic && !string.IsNullOrWhiteSpace(shop.FooterAr))
                    {
                        b.Add(new PrintText(shop.FooterAr, PrintAlign.Center));
                    }

                    break;
            }

            parts.Add(part, b);
        }

        return new PrintDocument($"{(isCopy ? "Receipt copy" : "Receipt")} {order.Number.Display}", blocks);
    }

    /// <summary>
    /// A kitchen ticket: order number, how it is going out, time, and the items in food-type order
    /// with sizes, extras, deal choices and notes, large and with no prices. Parts, order and item size follow the
    /// owner's kitchen layout.
    /// </summary>
    /// <param name="order">The order.</param>
    /// <param name="lines">The items for this ticket.</param>
    /// <param name="kind">New, additional or void.</param>
    /// <param name="shop">Receipt settings (language).</param>
    /// <param name="timeZone">Shop time zone.</param>
    /// <param name="station">The kitchen printer's name, printed at the top when there are several (e.g. "PIZZA").</param>
    /// <param name="layout">The kitchen layout (the standard one if not given).</param>
    public static PrintDocument KitchenTicket(Order order, IReadOnlyList<OrderLine> lines, KitchenTicketKind kind, ReceiptSettings shop, TimeZoneInfo timeZone, string? station = null, ReceiptLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(shop);
        layout ??= ReceiptParts.Standard(ReceiptKind.Kitchen);
        var lang = Lang.Parse(layout.Language ?? shop.Language);
        var size = layout.ItemSize;
        var blocks = new List<PrintBlock>();
        var parts = new PartWriter(blocks, ReceiptParts.Kitchen);
        foreach (var part in Printed(ReceiptKind.Kitchen, layout, vatRegistered: false))
        {
            var b = new List<PrintBlock>();
            switch (part)
            {
                case "ticketType":
                    b.Add(kind switch
                    {
                        KitchenTicketKind.Void => new PrintText("VOID - DO NOT MAKE", PrintAlign.Center, PrintSize.Large, Bold: true, Inverted: true),
                        KitchenTicketKind.Additional => new PrintText("ADDITIONAL ITEMS", PrintAlign.Center, PrintSize.Large, Bold: true, Inverted: true),
                        _ => new PrintText("NEW ORDER", PrintAlign.Center, PrintSize.Large, Bold: true),
                    });
                    break;
                case "station" when station is not null:
                    b.Add(new PrintText(station.ToUpperInvariant(), PrintAlign.Center, PrintSize.Large, Bold: true, Inverted: true));
                    break;
                case "orderNumber":
                    b.Add(new PrintText(order.Number.Display, PrintAlign.Center, PrintSize.Huge, Bold: true));
                    break;
                case "orderType":
                    b.Add(new PrintText(OrderKind(order), PrintAlign.Center, PrintSize.Large, Bold: true));
                    break;
                case "table" when order.Table is { } table:
                    b.Add(new PrintText($"TABLE {table}", PrintAlign.Center, PrintSize.Huge, Bold: true));
                    break;
                case "dateTime":
                    b.Add(new PrintText($"Ordered {LocalTime(order.CreatedAt, timeZone)}", PrintAlign.Center));
                    break;
                case "topText" or "bottomText":
                    AddText(b, part == "topText" ? layout.TopText : layout.BottomText);
                    break;
                case "customer":
                    AddContact(b, order, PrintSize.Normal);
                    break;
                case "items":
                    foreach (var line in lines.OrderBy(l => l.Display.CourseSortOrder).ThenBy(l => l.EntryNumber))
                    {
                        b.Add(new PrintText($"{line.Pricing.Quantity} x {Name(line.Display, lang.English)}", Size: size, Bold: true));
                        AddArabicName(b, line.Display, lang, indent: 1, size);
                        AddDetails(b, line.Display, lang, indent: 1, showPrices: false, size);
                        if (line.Note is { } note)
                        {
                            b.Add(new PrintText($"** {note} **", Size: size, Bold: true, Indent: 1));
                        }

                        if (kind == KitchenTicketKind.Void && line.VoidReason is { } reason)
                        {
                            b.Add(new PrintText($"Reason: {reason}", Indent: 1));
                        }

                        b.Add(new PrintSpace(0.4));
                    }

                    break;
                case "itemCount":
                    b.Add(new PrintText($"{lines.Sum(l => l.Pricing.Quantity)} item(s)", PrintAlign.Center, PrintSize.Small));
                    break;
            }

            parts.Add(part, b);
        }

        var title = kind switch
        {
            KitchenTicketKind.Void => "Kitchen void",
            KitchenTicketKind.Additional => "Kitchen additional",
            _ => "Kitchen",
        };
        return new PrintDocument(station is null ? $"{title} {order.Number.Display}" : $"{title} {order.Number.Display} {station}", blocks);
    }

    /// <summary>The parts that print, in order: the shown ones plus any the law or the printout needs.</summary>
    private static IEnumerable<string> Printed(ReceiptKind kind, ReceiptLayout layout, bool vatRegistered) =>
        layout.Parts.Where(p => p.Show || ReceiptParts.IsRequired(kind, p.Id, vatRegistered)).Select(p => p.Id);

    /// <summary>Adds parts in order, with a dashed line wherever the next part belongs to another group.</summary>
    private sealed class PartWriter(List<PrintBlock> blocks, IReadOnlyList<LayoutPartInfo> catalogue)
    {
        private string? _group;

        public void Add(string id, List<PrintBlock> part)
        {
            if (part.Count == 0)
            {
                return;
            }

            var group = catalogue.First(p => p.Id == id).Group;
            if (_group is not null && _group != group)
            {
                blocks.Add(new PrintRule());
            }

            _group = group;
            blocks.AddRange(part);
        }
    }

    private static void AddText(List<PrintBlock> blocks, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            blocks.AddRange(text.Split('\n').Select(line => new PrintText(line, PrintAlign.Center)));
        }
    }

    private static void AddReceiptItems(List<PrintBlock> blocks, Order order, Lang lang, PrintSize size)
    {
        var smaller = Smaller(size);
        var priced = PricedByLine(order);
        foreach (var line in order.LinesForReceipt)
        {
            var p = priced[line.Id];
            var vatCode = p.VatRate?.Code ?? string.Join('/', p.Components.Select(c => c.VatRate.Code).Distinct());
            blocks.Add(new PrintColumns($"{line.Pricing.Quantity} x {Name(line.Display, lang.English)}", $"{Money(p.LineTotal)} {vatCode}", size, Bold: true));
            AddArabicName(blocks, line.Display, lang, indent: 1, size);
            AddDetails(blocks, line.Display, lang, indent: 1, showPrices: false, size);
            if (p.Offer.Promotion is { } offer && p.Saving.MinorUnits > 0)
            {
                blocks.Add(new PrintColumns($"{offer.Name}", $"-{Money(p.Saving)}", smaller, Indent: 1));
            }

            if (line.Note is { } note)
            {
                blocks.Add(new PrintText($"* {note}", Size: smaller, Indent: 1));
            }
        }
    }

    private static void AddTotals(List<PrintBlock> blocks, PricedOrder t)
    {
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
            var where = delivery.Quote.Status == Pos.Domain.Delivery.DeliveryQuoteStatus.DistanceUnknown
                ? "distance not known"
                : $"{delivery.Quote.DistanceMiles.ToString("0.0", CultureInfo.InvariantCulture)} miles";
            blocks.Add(new PrintColumns($"Delivery ({where})", Money(delivery.Fee)));
        }

        if (t.ServiceCharge is { } service)
        {
            var label = service.Input.IsOptional ? "Optional service charge" : "Service charge";
            blocks.Add(new PrintColumns($"{label} {(service.Input.BasisPoints / 100m).ToString("0.##", CultureInfo.InvariantCulture)}%", Money(service.Amount)));
        }

        blocks.Add(new PrintColumns("TOTAL", Money(t.Total), PrintSize.Large, Bold: true));
    }

    private static void AddPayments(List<PrintBlock> blocks, OrderPayments? payments)
    {
        if (payments is not { Payments.Count: > 0 })
        {
            return;
        }

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

    private static void AddVatSummary(List<PrintBlock> blocks, PricedOrder t)
    {
        if (t.VatSummary.Count == 0)
        {
            return;
        }

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

    private static PrintSize Smaller(PrintSize size) => size switch
    {
        PrintSize.Huge => PrintSize.Large,
        PrintSize.Large => PrintSize.Normal,
        _ => PrintSize.Small,
    };

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
        var smaller = Smaller(size);
        foreach (var extra in display.Extras)
        {
            blocks.Add(new PrintText(Extra(Text(extra, lang)), Size: smaller, Indent: indent));
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
                blocks.Add(new PrintText(Extra(Text(extra, lang)), Size: smaller, Indent: indent + 1));
            }
        }
    }

    /// <summary>Customer name, phone, delivery address and notes, when the order has them.</summary>
    private static void AddContact(List<PrintBlock> blocks, Order order, PrintSize size)
    {
        if (order.Contact is not { } c)
        {
            return;
        }

        if (c.Name is { } name)
        {
            blocks.Add(new PrintText(name, Size: PrintSize.Large, Bold: true));
        }

        if (c.Phone is { } phone)
        {
            blocks.Add(new PrintText($"Tel: {PhoneText(phone)}", Size: size));
        }

        if (c.Address is { } a)
        {
            blocks.Add(new PrintText(a.Line1, Size: size, Bold: true));
            foreach (var part in new[] { a.Line2, a.Town }.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                blocks.Add(new PrintText(part!, Size: size));
            }

            blocks.Add(new PrintText(a.Postcode, Size: size, Bold: true));
        }

        if (c.Notes is { } notes)
        {
            blocks.Add(new PrintText($"** {notes} **", Size: size, Bold: true));
        }
    }

    /// <summary>"07700900123" as "07700 900123".</summary>
    private static string PhoneText(string digits) => digits.Length == 11 ? $"{digits[..5]} {digits[5..]}" : digits;

    /// <summary>"+ Garlic", but a removal such as "No onion" is printed as it is.</summary>
    private static string Extra(string name) =>
        name.StartsWith("No ", StringComparison.OrdinalIgnoreCase) ? name : $"+ {name}";

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
