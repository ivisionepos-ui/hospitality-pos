using Pos.Domain.Common;
using Pos.Domain.Orders;
using Pos.Domain.Pricing;
using Pos.Domain.Promotions;
using Pos.Domain.Tax;

namespace Pos.Hub;

/// <summary>The branch's settings, from the "Hub" section of appsettings.json.</summary>
public sealed class HubSettings
{
    /// <summary>This branch's id.</summary>
    public Guid BranchId { get; set; }

    /// <summary>Short code used in order references, e.g. "SHOP1".</summary>
    public string BranchCode { get; set; } = "SHOP1";

    /// <summary>IANA time zone, e.g. "Europe/London".</summary>
    public string TimeZoneId { get; set; } = "Europe/London";

    /// <summary>When the trading day starts, e.g. "05:00".</summary>
    public string TradingDayStartsAt { get; set; } = "05:00";

    /// <summary>Port for plain HTTP, open to this PC only (set-up, local tools).</summary>
    public int LocalHttpPort { get; set; } = 5080;

    /// <summary>Port for encrypted HTTPS on the shop network (tills and screens). 0 = shop network off.</summary>
    public int NetworkHttpsPort { get; set; } = 5443;

    /// <summary>Path of the SQLite database file.</summary>
    public string DatabasePath { get; set; } = "data/hub.db";

    /// <summary>
    /// Path of the key file used to protect staff PINs. Empty = "hub.key" next to the database.
    /// Keep it with the database in backups; without it, staff must be given new PINs.
    /// </summary>
    public string KeyPath { get; set; } = string.Empty;

    /// <summary>The key file path actually used.</summary>
    public string KeyFilePath => string.IsNullOrWhiteSpace(KeyPath)
        ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DatabasePath)) ?? ".", "hub.key")
        : KeyPath;

    /// <summary>Largest % discount staff may give without a manager, in basis points (1000 = 10%).</summary>
    public int StaffDiscountMaxPercent { get; set; }

    /// <summary>Largest £ discount staff may give without a manager, in pence.</summary>
    public long StaffDiscountMaxPence { get; set; }

    /// <summary>The branch time zone.</summary>
    public TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    /// <summary>Trading day start as a time.</summary>
    public TimeOnly DayStartsAt => TimeOnly.Parse(TradingDayStartsAt, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Pricing settings for new and loaded orders.</summary>
    public OrderPricingSetup PricingSetup(IReadOnlyList<Promotion> promotions) =>
        new(new UkVatPolicy(), promotions, TimeZone, new DiscountPolicy(StaffDiscountMaxPercent, Money.Gbp(StaffDiscountMaxPence)));

    /// <summary>Checks the settings at start-up so mistakes show immediately.</summary>
    public void Validate()
    {
        if (BranchId == Guid.Empty)
        {
            throw new InvalidOperationException("Hub:BranchId must be set.");
        }

        if (string.IsNullOrWhiteSpace(BranchCode))
        {
            throw new InvalidOperationException("Hub:BranchCode must be set.");
        }

        _ = TimeZone;
        _ = DayStartsAt;
        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new InvalidOperationException("Hub:DatabasePath must be set.");
        }
    }
}
