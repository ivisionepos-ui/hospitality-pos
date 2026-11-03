using Pos.Domain.Common;
using Pos.Domain.Delivery;

namespace Pos.Hub.Delivery;

/// <summary>One distance band in appsettings.json, e.g. 0–2 miles £1.50.</summary>
public sealed class DeliveryBandSettings
{
    /// <summary>From this many miles.</summary>
    public decimal FromMiles { get; set; }

    /// <summary>Up to this many miles.</summary>
    public decimal ToMiles { get; set; }

    /// <summary>Fee in pence.</summary>
    public long FeePence { get; set; }

    /// <summary>Minimum order for this band, in pence (optional).</summary>
    public long? MinimumOrderPence { get; set; }
}

/// <summary>The "Delivery" section of appsettings.json: the shop's location and delivery charges.</summary>
public sealed class DeliverySettings
{
    /// <summary>The shop's postcode; its location is looked up once and remembered.</summary>
    public string ShopPostcode { get; set; } = string.Empty;

    /// <summary>
    /// Straight-line distance is multiplied by this to estimate road distance (roads are rarely straight).
    /// 1.3 is a common rule of thumb for towns. A road-distance service can replace it later.
    /// </summary>
    public decimal RoadFactor { get; set; } = 1.3m;

    /// <summary>Distance bands. Leave empty to charge per mile instead.</summary>
    public List<DeliveryBandSettings> Bands { get; set; } = [];

    /// <summary>Per-mile rate in pence (used when there are no bands).</summary>
    public long? RatePerMilePence { get; set; }

    /// <summary>Lowest per-mile fee in pence.</summary>
    public long? MinimumFeePence { get; set; }

    /// <summary>Furthest the shop delivers, in miles.</summary>
    public decimal MaximumMiles { get; set; } = 5m;

    /// <summary>Minimum order for delivery in pence (optional; a band can set its own).</summary>
    public long? MinimumOrderPence { get; set; }

    /// <summary>Free delivery when the food comes to at least this, in pence (optional).</summary>
    public long? FreeOverPence { get; set; }

    /// <summary>Postcode lookup service (free, UK open data, no account).</summary>
    public string PostcodeService { get; set; } = "https://api.postcodes.io";

    /// <summary>The charge table as the pricing rules use it.</summary>
    public DeliveryChargeTable Table()
    {
        static Money? Gbp(long? pence) => pence is { } p ? Money.Gbp(p) : null;
        if (Bands.Count > 0)
        {
            return DeliveryChargeTable.Banded(
                [.. Bands.Select(b => new DeliveryBand(b.FromMiles, b.ToMiles, Money.Gbp(b.FeePence), Gbp(b.MinimumOrderPence)))],
                MaximumMiles, Gbp(MinimumOrderPence), Gbp(FreeOverPence));
        }

        return RatePerMilePence is { } rate
            ? DeliveryChargeTable.PerMile(Money.Gbp(rate), Money.Gbp(MinimumFeePence ?? 0), MaximumMiles, null, Gbp(MinimumOrderPence), Gbp(FreeOverPence))
            : throw new InvalidOperationException("Delivery charges are not set: add Bands or RatePerMilePence to the Delivery settings.");
    }
}
