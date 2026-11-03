using System.Text.RegularExpressions;

namespace Pos.Domain.Orders;

/// <summary>A delivery address as staff typed it (UK).</summary>
/// <param name="Line1">House number or name and street, e.g. "12 High Street".</param>
/// <param name="Line2">Flat, building or area (optional).</param>
/// <param name="Town">Town (optional).</param>
/// <param name="Postcode">Postcode in standard form, e.g. "SW1A 1AA".</param>
public sealed record DeliveryAddress(string Line1, string? Line2, string? Town, string Postcode)
{
    /// <summary>Checks and tidies the address. Throws <see cref="ArgumentException"/> when it is not usable.</summary>
    public DeliveryAddress Checked()
    {
        if (string.IsNullOrWhiteSpace(Line1))
        {
            throw new ArgumentException("Enter the house number and street.");
        }

        if (!UkPostcode.TryNormalise(Postcode, out var postcode))
        {
            throw new ArgumentException($"\"{Postcode}\" is not a UK postcode.");
        }

        return new DeliveryAddress(Tidy(Line1, 120)!, Tidy(Line2, 120), Tidy(Town, 60), postcode);
    }

    /// <summary>The address on one line.</summary>
    public override string ToString() => string.Join(", ", new[] { Line1, Line2, Town, Postcode }.Where(p => !string.IsNullOrWhiteSpace(p)));

    internal static string? Tidy(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = Regex.Replace(value.Trim(), @"\s+", " ");
        return text.Length <= max ? text : text[..max];
    }
}

/// <summary>Who the order is for: name, phone, delivery address and notes for the driver.</summary>
/// <param name="Name">Customer name.</param>
/// <param name="Phone">Phone number, digits only (e.g. "07700900123").</param>
/// <param name="Address">Delivery address (delivery orders).</param>
/// <param name="Notes">Notes for the driver or shop, e.g. "Ring the side door".</param>
public sealed record OrderContact(string? Name, string? Phone, DeliveryAddress? Address, string? Notes)
{
    /// <summary>Checks and tidies the details. Throws <see cref="ArgumentException"/> when they are not usable.</summary>
    public OrderContact Checked()
    {
        string? phone = null;
        if (!string.IsNullOrWhiteSpace(Phone) && !UkPhone.TryNormalise(Phone, out phone))
        {
            throw new ArgumentException($"\"{Phone}\" does not look like a phone number.");
        }

        var contact = new OrderContact(DeliveryAddress.Tidy(Name, 80), phone, Address?.Checked(), DeliveryAddress.Tidy(Notes, 200));
        return contact.Name is null && contact.Phone is null && contact.Address is null && contact.Notes is null
            ? throw new ArgumentException("Customer details are empty.")
            : contact;
    }
}

/// <summary>UK postcodes.</summary>
public static partial class UkPostcode
{
    /// <summary>
    /// Turns "sw1a1aa", " SW1A  1AA " etc. into "SW1A 1AA". Returns false when it cannot be a UK postcode.
    /// (Whether it exists is checked by the postcode lookup.)
    /// </summary>
    public static bool TryNormalise(string? value, out string postcode)
    {
        postcode = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var compact = value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (!Shape().IsMatch(compact))
        {
            return false;
        }

        postcode = $"{compact[..^3]} {compact[^3..]}";
        return true;
    }

    [GeneratedRegex(@"^[A-Z]{1,2}[0-9][A-Z0-9]?[0-9][A-Z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}

/// <summary>UK phone numbers.</summary>
public static class UkPhone
{
    /// <summary>
    /// Keeps digits only and turns +44 / 0044 into 0, e.g. "+44 7700 900123" → "07700900123".
    /// Accepts 10–11 digit UK numbers. Returns false otherwise.
    /// </summary>
    public static bool TryNormalise(string? value, out string phone)
    {
        phone = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var digits = new string([.. value.Where(char.IsAsciiDigit)]);
        if (value.TrimStart().StartsWith('+') && digits.StartsWith("44", StringComparison.Ordinal))
        {
            digits = "0" + digits[2..];
        }
        else if (digits.StartsWith("0044", StringComparison.Ordinal))
        {
            digits = "0" + digits[4..];
        }

        if (digits.Length is < 10 or > 11 || digits[0] != '0')
        {
            return false;
        }

        phone = digits;
        return true;
    }
}
