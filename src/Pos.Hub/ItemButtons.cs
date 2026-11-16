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

    /// <summary>Exactly the width and height the owner typed, in pixels.</summary>
    Custom,
}

/// <summary>How a till shows its item and category buttons (More → Buttons).</summary>
/// <param name="ShowPrices">Show each item's price on its button.</param>
/// <param name="Size">Item button size: Small, Normal, Large (automatic, fitting the screen) or Custom.</param>
/// <param name="ItemWidth">Item button width in pixels (Custom only).</param>
/// <param name="ItemHeight">Item button height in pixels (Custom only).</param>
/// <param name="CustomCategories">False: category buttons size themselves to their names (automatic); true: the width and height below.</param>
/// <param name="CategoryWidth">Category button width in pixels (custom only).</param>
/// <param name="CategoryHeight">Category button height in pixels (custom only).</param>
public sealed record ItemButtonSettings(
    bool ShowPrices,
    ItemButtonSize Size,
    int? ItemWidth = null,
    int? ItemHeight = null,
    bool CustomCategories = false,
    int? CategoryWidth = null,
    int? CategoryHeight = null)
{
    /// <summary>Smallest and largest item button (pixels): still tappable, still fits a till screen.</summary>
    public static (int MinWidth, int MaxWidth, int MinHeight, int MaxHeight) ItemLimits => (60, 400, 40, 300);

    /// <summary>Smallest and largest category button (pixels).</summary>
    public static (int MinWidth, int MaxWidth, int MinHeight, int MaxHeight) CategoryLimits => (50, 400, 36, 200);

    /// <summary>Prices shown, normal size, categories automatic.</summary>
    public static ItemButtonSettings Default { get; } = new(true, ItemButtonSize.Normal);

    /// <summary>Checks the sizes; keeps pixel sizes only where they are used. Throws <see cref="ArgumentException"/>.</summary>
    public ItemButtonSettings Checked()
    {
        if (!Enum.IsDefined(Size))
        {
            throw new ArgumentException("Choose Small, Normal, Large or Custom.");
        }

        static int Pixels(int? value, int min, int max, string what) =>
            value is { } v && v >= min && v <= max ? v : throw new ArgumentException($"{what} must be between {min} and {max} pixels.");
        var custom = Size == ItemButtonSize.Custom;
        var (iw, iW, ih, iH) = ItemLimits;
        var (cw, cW, ch, cH) = CategoryLimits;
        return new ItemButtonSettings(
            ShowPrices,
            Size,
            custom ? Pixels(ItemWidth, iw, iW, "Item button width") : null,
            custom ? Pixels(ItemHeight, ih, iH, "Item button height") : null,
            CustomCategories,
            CustomCategories ? Pixels(CategoryWidth, cw, cW, "Category button width") : null,
            CustomCategories ? Pixels(CategoryHeight, ch, cH, "Category button height") : null);
    }
}

/// <summary>What the owner or a manager saves for this till.</summary>
/// <param name="ShowPrices">Show prices.</param>
/// <param name="Size">Item button size.</param>
/// <param name="AllTills">True to use the same on every till.</param>
/// <param name="ItemWidth">Custom item width (pixels).</param>
/// <param name="ItemHeight">Custom item height (pixels).</param>
/// <param name="CustomCategories">Custom category size.</param>
/// <param name="CategoryWidth">Custom category width (pixels).</param>
/// <param name="CategoryHeight">Custom category height (pixels).</param>
public sealed record SaveItemButtonsRequest(
    bool ShowPrices,
    ItemButtonSize Size,
    bool AllTills,
    int? ItemWidth = null,
    int? ItemHeight = null,
    bool CustomCategories = false,
    int? CategoryWidth = null,
    int? CategoryHeight = null);

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
        var settings = new ItemButtonSettings(request.ShowPrices, request.Size, request.ItemWidth, request.ItemHeight,
            request.CustomCategories, request.CategoryWidth, request.CategoryHeight).Checked();
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
