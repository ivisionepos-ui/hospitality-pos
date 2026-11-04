using System.Globalization;
using System.Text.RegularExpressions;
using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Hub.Data;
using Pos.Hub.Delivery;
using Pos.Hub.Printing;

namespace Pos.Hub;

/// <summary>The shop's name and address as the owner enters them on the till (More → Shop details).</summary>
/// <param name="BusinessName">Name on receipts.</param>
/// <param name="AddressLine1">First address line, e.g. "12 High Street".</param>
/// <param name="AddressLine2">Second line (optional).</param>
/// <param name="Town">Town.</param>
/// <param name="Postcode">Postcode: printed on receipts, and where delivery distances are measured from.</param>
/// <param name="Phone">Phone number on receipts.</param>
/// <param name="VatNumber">VAT registration number (required on receipts when VAT registered).</param>
/// <param name="ReceiptMessage">Message at the bottom of receipts.</param>
public sealed record ShopDetails(
    string BusinessName,
    string? AddressLine1,
    string? AddressLine2,
    string? Town,
    string? Postcode,
    string? Phone,
    string? VatNumber,
    string? ReceiptMessage)
{
    /// <summary>Checks and tidies the details; throws <see cref="ArgumentException"/> with a clear reason.</summary>
    public ShopDetails Checked()
    {
        static string? Tidy(string? v, int max) => string.IsNullOrWhiteSpace(v) ? null : Regex.Replace(v.Trim(), @"\s+", " ") is var t && t.Length <= max ? t : throw new ArgumentException($"\"{v.Trim()[..20]}…\" is too long (at most {max} characters).");
        var name = Tidy(BusinessName, 60) ?? throw new ArgumentException("Enter the shop's name.");
        string? postcode = null;
        if (!string.IsNullOrWhiteSpace(Postcode) && !UkPostcode.TryNormalise(Postcode, out postcode))
        {
            throw new ArgumentException($"\"{Postcode}\" is not a UK postcode.");
        }

        string? phone = null;
        if (!string.IsNullOrWhiteSpace(Phone))
        {
            phone = UkPhone.TryNormalise(Phone, out var digits)
                ? digits.Length == 11 ? $"{digits[..5]} {digits[5..]}" : $"{digits[..4]} {digits[4..7]} {digits[7..]}"
                : throw new ArgumentException($"\"{Phone}\" does not look like a phone number.");
        }

        string? vat = null;
        if (!string.IsNullOrWhiteSpace(VatNumber))
        {
            // UK VAT numbers: 9 digits (12 for groups), usually written "GB 123 4567 89".
            var digits = new string([.. VatNumber.Where(char.IsAsciiDigit)]);
            vat = digits.Length switch
            {
                9 => $"GB {digits[..3]} {digits[3..7]} {digits[7..]}",
                12 => $"GB {digits[..3]} {digits[3..7]} {digits[7..9]} {digits[9..]}",
                _ => throw new ArgumentException($"\"{VatNumber}\" is not a UK VAT number (9 digits, e.g. GB 123 4567 89)."),
            };
        }

        return new ShopDetails(name, Tidy(AddressLine1, 80), Tidy(AddressLine2, 80), Tidy(Town, 60), postcode, phone, vat, Tidy(ReceiptMessage, 120));
    }
}

/// <summary>
/// Keeps the shop details in the hub database and puts them where they are used: the receipt header and footer,
/// and the shop postcode that delivery distances are measured from. Until the owner saves them, the values in
/// appsettings.json are used.
/// </summary>
public sealed class ShopDetailsService(HubDatabase db, PrintSettings printing, DeliverySettings delivery, TimeProvider clock)
{
    private const string Key = "shop.details";

    /// <summary>The current details.</summary>
    public ShopDetails Get()
    {
        var saved = db.Run(() => db.Scalar("SELECT value FROM settings WHERE key = $k;", ("$k", Key)) as string);
        if (saved is not null)
        {
            return PosJson.Deserialize<ShopDetails>(saved);
        }

        var r = printing.ReceiptText;
        var lines = r.AddressLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return new ShopDetails(r.BusinessName, lines.ElementAtOrDefault(0), lines.ElementAtOrDefault(1), lines.ElementAtOrDefault(2),
            string.IsNullOrWhiteSpace(delivery.ShopPostcode) ? null : delivery.ShopPostcode, r.Phone, r.VatNumber, r.Footer);
    }

    /// <summary>Saves the details and uses them straight away (no restart).</summary>
    public ShopDetails Save(ShopDetails details, Guid byUserId)
    {
        ArgumentNullException.ThrowIfNull(details);
        var checkedDetails = details.Checked();
        db.Run(() => db.Execute(
            "INSERT OR REPLACE INTO settings (key, value, updated_at, updated_by) VALUES ($k, $v, $at, $by);",
            ("$k", Key), ("$v", PosJson.Serialize(checkedDetails)), ("$at", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("$by", byUserId.ToString())));
        Apply(checkedDetails);
        return checkedDetails;
    }

    /// <summary>Puts the saved details into the receipt and delivery settings (called at start-up and after saving).</summary>
    public void Apply() => Apply(Get());

    private void Apply(ShopDetails d)
    {
        var r = printing.ReceiptText;
        r.BusinessName = d.BusinessName;
        r.AddressLines.Clear();
        foreach (var line in new[] { d.AddressLine1, d.AddressLine2, string.Join(' ', new[] { d.Town, d.Postcode }.Where(p => p is not null)) }.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            r.AddressLines.Add(line!);
        }

        r.Phone = d.Phone;
        r.VatNumber = d.VatNumber;
        r.Footer = d.ReceiptMessage;
        delivery.ShopPostcode = d.Postcode ?? string.Empty;
    }
}
