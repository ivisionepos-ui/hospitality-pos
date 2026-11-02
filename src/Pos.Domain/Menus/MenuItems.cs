using System.Text.RegularExpressions;
using Pos.Domain.Common;
using Pos.Domain.Tax;

namespace Pos.Domain.Menus;

/// <summary>A food-type group that sets print order: Starters, Mains, Sides, Desserts, Drinks.</summary>
/// <param name="Id">Unique id.</param>
/// <param name="Name">Name, e.g. Starters.</param>
/// <param name="SortOrder">Position on receipts and tickets (lowest first).</param>
public sealed record CourseGroup(Guid Id, LocalizedText Name, int SortOrder);

/// <summary>A category tab on the till, e.g. Pizzas.</summary>
public sealed record Category
{
    /// <summary>Unique id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Tab name.</summary>
    public required LocalizedText Name { get; init; }

    /// <summary>Position of the tab (0 = first).</summary>
    public int SortOrder { get; init; }

    /// <summary>Tab colour as #RRGGBB, or null for the default.</summary>
    public string? Colour { get; init; }

    /// <summary>Food-type group for every item in it, unless the item overrides it.</summary>
    public Guid? CourseGroupId { get; init; }

    /// <summary>Hidden from the till and website.</summary>
    public bool IsHidden { get; init; }
}

/// <summary>One size of a product, e.g. 12". Products without sizes have a single variant.</summary>
/// <param name="Id">Unique id.</param>
/// <param name="Name">Size name, e.g. Large.</param>
/// <param name="Price">Normal price, VAT included.</param>
/// <param name="IsSoldOut">This size has run out.</param>
public sealed record ProductVariant(Guid Id, LocalizedText Name, Money Price, bool IsSoldOut = false);

/// <summary>Whether a product is sold on its own or is a meal deal.</summary>
public enum ProductType
{
    /// <summary>A normal item.</summary>
    Standard,

    /// <summary>A meal deal made of choice slots.</summary>
    MealDeal,
}

/// <summary>What a meal-deal option refers to.</summary>
public enum SlotOptionKind
{
    /// <summary>A product (any size).</summary>
    Product,

    /// <summary>One size of a product.</summary>
    Variant,

    /// <summary>Any product in a category (any size).</summary>
    Category,
}

/// <summary>An allowed choice in a meal-deal slot, with an optional extra charge.</summary>
/// <param name="Kind">Product, size or category.</param>
/// <param name="TargetId">Id of the product, size or category.</param>
/// <param name="Upcharge">Extra charge, e.g. +£1.50 for a premium pizza. Null = none.</param>
public sealed record SlotOption(SlotOptionKind Kind, Guid TargetId, Money? Upcharge = null);

/// <summary>A choice in a meal deal, e.g. "Choose a pizza".</summary>
/// <param name="Id">Unique id.</param>
/// <param name="Name">Prompt shown to staff and customers.</param>
/// <param name="MinChoices">Fewest choices (0 = optional slot).</param>
/// <param name="MaxChoices">Most choices.</param>
/// <param name="Options">What may be chosen.</param>
public sealed record MealDealSlot(Guid Id, LocalizedText Name, int MinChoices, int MaxChoices, IReadOnlyList<SlotOption> Options);

/// <summary>A choice of extras, e.g. "Extra toppings" (0 to 5).</summary>
/// <param name="Id">Unique id.</param>
/// <param name="Name">Group name.</param>
/// <param name="MinChoices">Fewest choices (0 = optional).</param>
/// <param name="MaxChoices">Most choices.</param>
/// <param name="Options">The extras.</param>
public sealed record ModifierGroup(Guid Id, LocalizedText Name, int MinChoices, int MaxChoices, IReadOnlyList<ModifierOption> Options);

/// <summary>One extra, e.g. Extra cheese +£1.00.</summary>
/// <param name="Id">Unique id.</param>
/// <param name="Name">Name.</param>
/// <param name="Price">Price per item, VAT included (VAT follows the item).</param>
/// <param name="IsSoldOut">Run out.</param>
public sealed record ModifierOption(Guid Id, LocalizedText Name, Money Price, bool IsSoldOut = false);

/// <summary>A product button on the till.</summary>
public sealed record Product
{
    /// <summary>Unique id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Category it sits in.</summary>
    public required Guid CategoryId { get; init; }

    /// <summary>Full name for receipts and the website.</summary>
    public required LocalizedText Name { get; init; }

    /// <summary>Short name for the till button, or null to use the full name.</summary>
    public LocalizedText? ButtonName { get; init; }

    /// <summary>Description for the website.</summary>
    public LocalizedText? Description { get; init; }

    /// <summary>Standard item or meal deal.</summary>
    public ProductType Type { get; init; } = ProductType.Standard;

    /// <summary>Sizes and their prices; at least one.</summary>
    public required IReadOnlyList<ProductVariant> Variants { get; init; }

    /// <summary>VAT category.</summary>
    public required VatCategory VatCategory { get; init; }

    /// <summary>Food-type group, overriding the category's; null = use the category's.</summary>
    public Guid? CourseGroupId { get; init; }

    /// <summary>Kitchen station the item prints to, or null for none.</summary>
    public Guid? KitchenStationId { get; init; }

    /// <summary>Button colour as #RRGGBB, or null for the category colour.</summary>
    public string? Colour { get; init; }

    /// <summary>Position in its category (0 = first).</summary>
    public int SortOrder { get; init; }

    /// <summary>Run out today.</summary>
    public bool IsSoldOut { get; init; }

    /// <summary>Hidden from the till and website.</summary>
    public bool IsHidden { get; init; }

    /// <summary>Extras offered with the item, in order.</summary>
    public IReadOnlyList<Guid> ModifierGroupIds { get; init; } = [];

    /// <summary>Choices for a meal deal; empty for a standard item.</summary>
    public IReadOnlyList<MealDealSlot> DealSlots { get; init; } = [];

    /// <summary>Allergens from the UK's 14 (e.g. "gluten", "milk").</summary>
    public IReadOnlySet<string> Allergens { get; init; } = new HashSet<string>();

    /// <summary>Name shown on the till button.</summary>
    public LocalizedText DisplayName => ButtonName ?? Name;
}

/// <summary>Shared checks for menu values.</summary>
public static partial class MenuRules
{
    /// <summary>True when the colour is null or a #RRGGBB value.</summary>
    public static bool IsValidColour(string? colour) => colour is null || ColourPattern().IsMatch(colour);

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColourPattern();
}
