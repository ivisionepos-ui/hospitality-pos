using System.Globalization;
using Pos.Domain.Common;
using Pos.Hub.Data;

namespace Pos.Hub.Printing;

/// <summary>The printouts whose layout the owner can change.</summary>
public enum ReceiptKind
{
    /// <summary>Customer receipt (counter, collection, eat-in).</summary>
    Receipt,

    /// <summary>Receipt for delivery orders (goes with the driver).</summary>
    Delivery,

    /// <summary>Kitchen ticket.</summary>
    Kitchen,
}

/// <summary>One part of a printout and whether it is printed.</summary>
/// <param name="Id">Which part, e.g. "phone" or "vatSummary".</param>
/// <param name="Show">Printed or not.</param>
public sealed record LayoutPart(string Id, bool Show);

/// <summary>A part as the owner's screen lists it.</summary>
/// <param name="Id">Part id.</param>
/// <param name="Label">What it is, e.g. "Shop phone".</param>
/// <param name="Group">Parts in different groups are separated by a dashed line.</param>
/// <param name="Required">Always printed (the law, or the printout would make no sense).</param>
public sealed record LayoutPartInfo(string Id, string Label, string Group, bool Required);

/// <summary>How one kind of printout is laid out (More → Receipt layouts).</summary>
/// <param name="Parts">The parts, top to bottom, each shown or hidden.</param>
/// <param name="ItemSize">Text size of the item lines (Normal, Large or Huge).</param>
/// <param name="Language">"en", "ar" or "both"; empty to follow the shop's setting.</param>
/// <param name="TopText">Extra text near the top, e.g. "Order online at …".</param>
/// <param name="BottomText">Extra text at the bottom, e.g. a promotion.</param>
public sealed record ReceiptLayout(IReadOnlyList<LayoutPart> Parts, PrintSize ItemSize, string? Language, string? TopText, string? BottomText)
{
    /// <summary>Longest top or bottom text.</summary>
    public const int MaxText = 200;

    /// <summary>Is this part printed?</summary>
    public bool Shows(string id) => Parts.FirstOrDefault(p => p.Id == id)?.Show ?? false;
}

/// <summary>The three layouts.</summary>
/// <param name="Receipt">Customer receipt.</param>
/// <param name="Delivery">Delivery receipt.</param>
/// <param name="Kitchen">Kitchen ticket.</param>
public sealed record ReceiptLayoutSet(ReceiptLayout Receipt, ReceiptLayout Delivery, ReceiptLayout Kitchen)
{
    /// <summary>The layout for one kind.</summary>
    public ReceiptLayout For(ReceiptKind kind) => kind switch
    {
        ReceiptKind.Delivery => Delivery,
        ReceiptKind.Kitchen => Kitchen,
        _ => Receipt,
    };
}

/// <summary>The parts each printout can have, the standard layouts, and checking what the owner saves.</summary>
public static class ReceiptParts
{
    /// <summary>Parts of a customer or delivery receipt, in the standard order.</summary>
    public static IReadOnlyList<LayoutPartInfo> Receipt { get; } =
    [
        new("shopName", "Shop name", "head", true),
        new("address", "Shop address", "head", true),
        new("phone", "Shop phone", "head", false),
        new("vatNumber", "VAT number", "head", true),
        new("topText", "Your text at the top", "head", false),
        new("orderNumber", "Order number", "order", false),
        new("orderType", "Order type (takeaway, delivery…)", "order", false),
        new("table", "Table number", "order", false),
        new("dateTime", "Date, time and reference", "order", true),
        new("servedBy", "Served by", "order", false),
        new("customer", "Customer name, phone, address and notes", "customer", false),
        new("items", "Items", "items", true),
        new("totals", "Totals", "totals", true),
        new("payments", "Payments and change", "totals", false),
        new("amountDue", "Amount due or PAID (delivery orders)", "totals", false),
        new("vatSummary", "VAT summary", "vat", true),
        new("footer", "Shop message (Shop details)", "footer", false),
        new("bottomText", "Your text at the bottom", "footer", false),
    ];

    /// <summary>Parts of a kitchen ticket, in the standard order.</summary>
    public static IReadOnlyList<LayoutPartInfo> Kitchen { get; } =
    [
        new("ticketType", "NEW ORDER / ADDITIONAL / VOID", "head", true),
        new("station", "Kitchen printer name (when there are several)", "head", false),
        new("orderNumber", "Order number", "head", false),
        new("orderType", "Order type (takeaway, delivery…)", "head", false),
        new("table", "Table number", "head", false),
        new("dateTime", "Time ordered", "head", false),
        new("topText", "Your text at the top", "head", false),
        new("customer", "Customer name, phone, address and notes", "customer", false),
        new("items", "Items", "items", true),
        new("itemCount", "Number of items", "end", false),
        new("bottomText", "Your text at the bottom", "end", false),
    ];

    /// <summary>Parts that are only required on a VAT receipt (when the shop has a VAT number).</summary>
    private static readonly HashSet<string> VatOnly = ["vatNumber", "vatSummary", "address"];

    /// <summary>The parts of one kind.</summary>
    public static IReadOnlyList<LayoutPartInfo> For(ReceiptKind kind) => kind == ReceiptKind.Kitchen ? Kitchen : Receipt;

    /// <summary>Must this part always print? (VAT parts only when the shop is VAT registered.)</summary>
    public static bool IsRequired(ReceiptKind kind, string id, bool vatRegistered)
    {
        var info = For(kind).FirstOrDefault(p => p.Id == id);
        return info is { Required: true } && (kind == ReceiptKind.Kitchen || vatRegistered || !VatOnly.Contains(id));
    }

    /// <summary>The standard layout: what the POS printed before layouts could be changed.</summary>
    public static ReceiptLayout Standard(ReceiptKind kind) => new(
        [.. For(kind).Select(p => new LayoutPart(p.Id, p.Id is not ("topText" or "bottomText")))],
        kind == ReceiptKind.Kitchen ? PrintSize.Large : PrintSize.Normal,
        null,
        null,
        null);

    /// <summary>All three standard layouts.</summary>
    public static ReceiptLayoutSet StandardSet { get; } = new(Standard(ReceiptKind.Receipt), Standard(ReceiptKind.Delivery), Standard(ReceiptKind.Kitchen));

    /// <summary>
    /// Checks a layout: every known part exactly once (missing ones added at the end, hidden), required parts shown,
    /// sizes and languages known, texts not too long. Throws <see cref="ArgumentException"/> with a plain message.
    /// </summary>
    public static ReceiptLayout Checked(ReceiptKind kind, ReceiptLayout layout, bool vatRegistered)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var known = For(kind);
        var parts = layout.Parts ?? [];
        if (parts.Any(p => known.All(k => k.Id != p.Id)))
        {
            throw new ArgumentException("A part of the layout is not known. Open the screen again.");
        }

        if (parts.GroupBy(p => p.Id).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("A part is listed twice.");
        }

        var all = parts.Concat(known.Where(k => parts.All(p => p.Id != k.Id)).Select(k => new LayoutPart(k.Id, false))).ToList();
        foreach (var hidden in all.Where(p => !p.Show && IsRequired(kind, p.Id, vatRegistered)))
        {
            var label = known.First(k => k.Id == hidden.Id).Label;
            throw new ArgumentException(vatRegistered && VatOnly.Contains(hidden.Id)
                ? $"\"{label}\" must stay on: a UK VAT receipt must show the shop's name, address, VAT number, date and time, items, total and VAT."
                : $"\"{label}\" must stay on.");
        }

        if (layout.ItemSize is not (PrintSize.Normal or PrintSize.Large or PrintSize.Huge))
        {
            throw new ArgumentException("Item text size must be Normal, Large or Huge.");
        }

        var language = string.IsNullOrWhiteSpace(layout.Language) ? null : layout.Language.Trim().ToLowerInvariant();
        if (language is not (null or "en" or "ar" or "both"))
        {
            throw new ArgumentException("Language must be English, Arabic or both.");
        }

        return new ReceiptLayout(all, layout.ItemSize, language, Text(layout.TopText, "top"), Text(layout.BottomText, "bottom"));
    }

    private static string? Text(string? value, string where)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var lines = value.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).ToList();
        var text = string.Join('\n', lines).Trim('\n');
        return text.Length > ReceiptLayout.MaxText || lines.Count > 4
            ? throw new ArgumentException($"The {where} text is too long (at most 4 lines and {ReceiptLayout.MaxText} characters).")
            : text;
    }
}

/// <summary>The saved layouts (More → Receipt layouts, owner).</summary>
public sealed class ReceiptLayoutService(HubDatabase db, TimeProvider clock)
{
    private const string Key = "receipt-layouts";

    /// <summary>The layouts in use (the standard ones until the owner changes them).</summary>
    public ReceiptLayoutSet Get() => db.Run(() =>
        db.Scalar("SELECT value FROM settings WHERE key = $k;", ("$k", Key)) is string json
            ? PosJson.Deserialize<ReceiptLayoutSet>(json)
            : ReceiptParts.StandardSet);

    /// <summary>Checks and saves all three layouts.</summary>
    public ReceiptLayoutSet Save(ReceiptLayoutSet layouts, bool vatRegistered, Guid byUserId)
    {
        ArgumentNullException.ThrowIfNull(layouts);
        var set = Check(layouts, vatRegistered);
        db.Run(() => db.Execute(
            "INSERT OR REPLACE INTO settings (key, value, updated_at, updated_by) VALUES ($k, $v, $at, $by);",
            ("$k", Key), ("$v", PosJson.Serialize(set)), ("$at", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("$by", byUserId.ToString())));
        return set;
    }

    /// <summary>Checks all three layouts (each message says which printout it is about).</summary>
    public static ReceiptLayoutSet Check(ReceiptLayoutSet layouts, bool vatRegistered)
    {
        ArgumentNullException.ThrowIfNull(layouts);
        ReceiptLayout One(ReceiptKind kind, ReceiptLayout? layout, string name)
        {
            try
            {
                return ReceiptParts.Checked(kind, layout ?? ReceiptParts.Standard(kind), vatRegistered);
            }
            catch (ArgumentException e)
            {
                throw new ArgumentException($"{name}: {e.Message}", e);
            }
        }

        return new ReceiptLayoutSet(
            One(ReceiptKind.Receipt, layouts.Receipt, "Customer receipt"),
            One(ReceiptKind.Delivery, layouts.Delivery, "Delivery receipt"),
            One(ReceiptKind.Kitchen, layouts.Kitchen, "Kitchen ticket"));
    }
}
