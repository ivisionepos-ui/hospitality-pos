using System.Globalization;
using Pos.Domain.Common;
using Pos.Hub.Data;
using Pos.Hub.Security;

namespace Pos.Hub;

/// <summary>How big the item buttons are on a till.</summary>
public enum ItemButtonSize
{
    /// <summary>Small: more items fit on the screen.</summary>
    Small,

    /// <summary>Normal (the default).</summary>
    Normal,

    /// <summary>Large: easier to tap, fewer items on the screen.</summary>
    Large,
}

/// <summary>How a till shows its item buttons (More → Item buttons).</summary>
/// <param name="ShowPrices">Show each item's price on its button.</param>
/// <param name="Size">Button size.</param>
public sealed record ItemButtonSettings(bool ShowPrices, ItemButtonSize Size)
{
    /// <summary>Prices shown, normal size.</summary>
    public static ItemButtonSettings Default { get; } = new(true, ItemButtonSize.Normal);
}

/// <summary>What the owner or a manager saves for this till.</summary>
/// <param name="ShowPrices">Show prices.</param>
/// <param name="Size">Button size.</param>
/// <param name="AllTills">True to use the same on every till.</param>
public sealed record SaveItemButtonsRequest(bool ShowPrices, ItemButtonSize Size, bool AllTills);

/// <summary>
/// Item buttons per till: with many items the buttons can be made small and the prices hidden so more fit on the screen.
/// Each till keeps its own choice (screens differ); one save can set every till.
/// </summary>
public sealed class ItemButtonService(HubDatabase db, AccessService access, TimeProvider clock)
{
    private const string Key = "item-buttons";

    /// <summary>The settings of one till (the default until someone changes them).</summary>
    public ItemButtonSettings For(Guid deviceId) =>
        All().TryGetValue(deviceId, out var settings) ? settings : ItemButtonSettings.Default;

    /// <summary>Saves this till's settings, or every till's.</summary>
    public ItemButtonSettings Save(Guid deviceId, SaveItemButtonsRequest request, Guid byUserId)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Size))
        {
            throw new ArgumentException("Choose Small, Normal or Large.");
        }

        var settings = new ItemButtonSettings(request.ShowPrices, request.Size);
        var all = All();
        var tills = request.AllTills
            ? access.ListDevices().Where(d => d.IsActive && d.Type is DeviceType.Till or DeviceType.BackOffice).Select(d => d.Id).Append(deviceId).Distinct()
            : [deviceId];
        foreach (var till in tills)
        {
            all[till] = settings;
        }

        db.Run(() => db.Execute(
            "INSERT OR REPLACE INTO settings (key, value, updated_at, updated_by) VALUES ($k, $v, $at, $by);",
            ("$k", Key), ("$v", PosJson.Serialize(all)), ("$at", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), ("$by", byUserId.ToString())));
        return settings;
    }

    private Dictionary<Guid, ItemButtonSettings> All() => db.Run(() =>
        db.Scalar("SELECT value FROM settings WHERE key = $k;", ("$k", Key)) is string json
            ? PosJson.Deserialize<Dictionary<Guid, ItemButtonSettings>>(json)
            : []);
}
