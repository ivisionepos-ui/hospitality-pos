using Pos.Domain.Orders;

namespace Pos.Domain.Tests.Orders;

/// <summary>Customer details on phone and delivery orders.</summary>
public class ContactTests
{
    [Theory]
    [InlineData("sw1a1aa", "SW1A 1AA")]
    [InlineData(" SW1A  1AA ", "SW1A 1AA")]
    [InlineData("m11ae", "M1 1AE")]
    [InlineData("B33 8TH", "B33 8TH")]
    [InlineData("cr26xh", "CR2 6XH")]
    [InlineData("DN551PT", "DN55 1PT")]
    [InlineData("w1a 0ax", "W1A 0AX")]
    [InlineData("EC1A1BB", "EC1A 1BB")]
    public void Postcodes_are_tidied(string typed, string expected)
    {
        Assert.True(UkPostcode.TryNormalise(typed, out var postcode));
        Assert.Equal(expected, postcode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("SW1A")]
    [InlineData("1AA SW1")]
    [InlineData("SW1A 1A")]
    public void Things_that_are_not_postcodes_are_refused(string typed) =>
        Assert.False(UkPostcode.TryNormalise(typed, out _));

    [Theory]
    [InlineData("07700 900123", "07700900123")]
    [InlineData("+44 7700 900123", "07700900123")]
    [InlineData("0044 (0)113 496 0000", "001134960000")] // the "(0)" makes it too long: refused below
    [InlineData("0113 496 0000", "01134960000")]
    [InlineData("(0113) 4960000", "01134960000")]
    public void Phone_numbers_keep_digits_only(string typed, string expected)
    {
        var ok = UkPhone.TryNormalise(typed, out var phone);
        if (expected.Length > 11)
        {
            Assert.False(ok);
        }
        else
        {
            Assert.True(ok);
            Assert.Equal(expected, phone);
        }
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("7700900123")]
    [InlineData("call me")]
    public void Bad_phone_numbers_are_refused(string typed) =>
        Assert.False(UkPhone.TryNormalise(typed, out _));

    [Fact]
    public void Contact_is_checked_and_tidied()
    {
        var contact = new OrderContact("  Sam   Patel ", "+447700900123", new DeliveryAddress(" 12  High Street ", "", null, "ls14ap"), "  ").Checked();

        Assert.Equal("Sam Patel", contact.Name);
        Assert.Equal("07700900123", contact.Phone);
        Assert.Equal("12 High Street, LS1 4AP", contact.Address!.ToString());
        Assert.Null(contact.Notes);
    }

    [Fact]
    public void Unusable_details_are_refused_with_a_clear_reason()
    {
        Assert.Contains("house number", Assert.Throws<ArgumentException>(() => new DeliveryAddress(" ", null, null, "LS1 4AP").Checked()).Message, StringComparison.Ordinal);
        Assert.Contains("not a UK postcode", Assert.Throws<ArgumentException>(() => new DeliveryAddress("1 High St", null, null, "12345").Checked()).Message, StringComparison.Ordinal);
        Assert.Contains("phone", Assert.Throws<ArgumentException>(() => new OrderContact("Sam", "123", null, null).Checked()).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new OrderContact(" ", null, null, null).Checked());
    }
}
