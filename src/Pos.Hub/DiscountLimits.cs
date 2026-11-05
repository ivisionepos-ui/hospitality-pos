using System.Globalization;
using Pos.Domain.Common;
using Pos.Domain.Pricing;
using Pos.Hub.Data;

namespace Pos.Hub;

/// <summary>
/// What staff may give off an order without a manager (More → Discount limits, owner). Above either limit a manager
/// approves with their PIN. 0 and £0 mean every discount needs a manager.
/// </summary>
/// <param name="StaffMaxPercent">Largest percentage off, in whole percent (0–100).</param>
/// <param name="StaffMaxPence">Largest amount off, in pence.</param>
public sealed record DiscountLimits(int StaffMaxPercent, long StaffMaxPence)
{
    /// <summary>Largest amount the limit can be set to (£1,000), to catch typing mistakes.</summary>
    public const long MaxPence = 100_000;

    /// <summary>Checks the limits; throws <see cref="ArgumentException"/> with a clear reason.</summary>
    public DiscountLimits Checked() =>
        StaffMaxPercent is < 0 or > 100 ? throw new ArgumentException("The percentage must be between 0 and 100.")
        : StaffMaxPence is < 0 or > MaxPence ? throw new ArgumentException("The amount must be between £0 and £1,000.")
        : this;

    /// <summary>The rule the order uses.</summary>
    public DiscountPolicy Policy => new(StaffMaxPercent * 100, Money.Gbp(StaffMaxPence));

    /// <summary>"10% or £5.00", for messages.</summary>
    public string Text => StaffMaxPercent == 0 && StaffMaxPence == 0
        ? "every discount"
        : string.Create(CultureInfo.InvariantCulture, $"discounts over {StaffMaxPercent}% or £{StaffMaxPence / 100m:0.00}");
}

/// <summary>Keeps the discount limits in the hub database; until the owner saves them, appsettings.json is used.</summary>
public sealed class DiscountLimitsService(HubDatabase db, HubSettings settings, TimeProvider clock)
{
    private const string Key = "discount.limits";

    /// <summary>The limits in force now.</summary>
    public DiscountLimits Get()
    {
        var saved = db.Run(() => db.Scalar("SELECT value FROM settings WHERE key = $k;", ("$k", Key)) as string);
        return saved is not null
            ? PosJson.Deserialize<DiscountLimits>(saved)
            : new DiscountLimits(settings.StaffDiscountMaxPercent / 100, settings.StaffDiscountMaxPence);
    }

    /// <summary>Saves new limits; they apply to the next discount given.</summary>
    public DiscountLimits Save(DiscountLimits limits, Guid byUserId)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var checkedLimits = limits.Checked();
        db.Run(() => db.Execute(
            "INSERT OR REPLACE INTO settings (key, value, updated_at, updated_by) VALUES ($k, $v, $at, $by);",
            ("$k", Key), ("$v", PosJson.Serialize(checkedLimits)), ("$at", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("$by", byUserId.ToString())));
        return checkedLimits;
    }
}
