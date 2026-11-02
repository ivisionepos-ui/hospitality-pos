using System.Globalization;
using Pos.Domain.Common;

namespace Pos.Domain.Menus;

/// <summary>What kind of menu thing a change touched.</summary>
public enum MenuEntityType
{
    /// <summary>A food-type group.</summary>
    CourseGroup,

    /// <summary>A category tab.</summary>
    Category,

    /// <summary>A product button.</summary>
    Product,

    /// <summary>A group of extras.</summary>
    ModifierGroup,
}

/// <summary>The state of one menu thing before or after a change. State is null when it did not exist.</summary>
/// <param name="Type">What it is.</param>
/// <param name="Id">Its id.</param>
/// <param name="State">The record, or null.</param>
public sealed record MenuSnapshot(MenuEntityType Type, Guid Id, object? State);

/// <summary>What sort of change was made.</summary>
public enum MenuChangeKind
{
    /// <summary>Something new was added.</summary>
    Added,

    /// <summary>Something was edited (name, price, sold out, colour…).</summary>
    Updated,

    /// <summary>Buttons or tabs were moved.</summary>
    Moved,

    /// <summary>An earlier change was undone.</summary>
    Undo,
}

/// <summary>One entry in the menu change log: who changed what, when, and what it was before.</summary>
/// <param name="Version">Menu version after this change; tills use it to know they are up to date.</param>
/// <param name="At">When it happened (UTC).</param>
/// <param name="UserId">Who made it.</param>
/// <param name="Kind">Added, updated, moved or undo.</param>
/// <param name="Summary">Plain-English description for the log screen.</param>
/// <param name="Before">State of everything touched, before.</param>
/// <param name="After">State of everything touched, after.</param>
public sealed record MenuChange(
    long Version,
    DateTimeOffset At,
    Guid UserId,
    MenuChangeKind Kind,
    string Summary,
    IReadOnlyList<MenuSnapshot> Before,
    IReadOnlyList<MenuSnapshot> After);

/// <summary>
/// A business's menu, edited in "Edit menu" mode on the till or in the back office.
/// Every edit is checked, given a new version number, and logged with its before and after state,
/// so the last change can be undone and other tills can catch up.
/// </summary>
public sealed class Menu
{
    private readonly Dictionary<Guid, CourseGroup> _courseGroups = [];
    private readonly Dictionary<Guid, Category> _categories = [];
    private readonly Dictionary<Guid, Product> _products = [];
    private readonly Dictionary<Guid, ModifierGroup> _modifierGroups = [];
    private readonly List<MenuChange> _log = [];
    private readonly HashSet<long> _undoneVersions = [];
    private readonly TimeProvider _clock;

    /// <summary>Creates an empty menu.</summary>
    public Menu(string currency = "GBP", TimeProvider? clock = null)
    {
        Currency = new Money(0, currency).Currency;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Currency of all prices.</summary>
    public string Currency { get; }

    /// <summary>Current version; goes up by one with every change.</summary>
    public long Version { get; private set; }

    /// <summary>All changes, oldest first.</summary>
    public IReadOnlyList<MenuChange> ChangeLog => _log;

    /// <summary>Food-type groups in print order.</summary>
    public IReadOnlyList<CourseGroup> CourseGroups => [.. _courseGroups.Values.OrderBy(g => g.SortOrder)];

    /// <summary>Category tabs in screen order.</summary>
    public IReadOnlyList<Category> Categories => [.. _categories.Values.OrderBy(c => c.SortOrder)];

    /// <summary>All modifier groups.</summary>
    public IReadOnlyCollection<ModifierGroup> ModifierGroups => _modifierGroups.Values;

    /// <summary>Every item on the menu, by category and position.</summary>
    public IReadOnlyList<Product> AllProducts =>
        [.. _products.Values.OrderBy(p => _categories.TryGetValue(p.CategoryId, out var c) ? c.SortOrder : int.MaxValue).ThenBy(p => p.SortOrder)];

    /// <summary>The whole menu as plain data, for saving and for sending to tills.</summary>
    public MenuState ToState() => new(Currency, Version, CourseGroups, Categories, [.. ModifierGroups], AllProducts);

    /// <summary>
    /// Loads a menu saved with <see cref="ToState"/>. The change log starts empty; the version carries on from the saved one.
    /// </summary>
    public static Menu FromState(MenuState state, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var menu = new Menu(state.Currency, clock) { Version = state.Version };
        foreach (var g in state.CourseGroups)
        {
            menu._courseGroups[g.Id] = g;
        }

        foreach (var c in state.Categories)
        {
            menu._categories[c.Id] = c;
        }

        foreach (var g in state.ModifierGroups)
        {
            menu._modifierGroups[g.Id] = g;
        }

        foreach (var p in state.Products)
        {
            menu._products[p.Id] = p;
        }

        return menu;
    }

    /// <summary>
    /// Builds a new menu from plain data, checking everything as if each part were added by hand
    /// (used to import a menu). Positions follow the order of the lists.
    /// </summary>
    /// <param name="state">The menu to import.</param>
    /// <param name="userId">Who is importing it.</param>
    /// <param name="clock">Clock for the change log.</param>
    /// <param name="continueFromVersion">Version to count on from, so tills see the import as newer than what they have.</param>
    public static Menu Import(MenuState state, Guid userId, TimeProvider? clock = null, long continueFromVersion = 0)
    {
        ArgumentNullException.ThrowIfNull(state);
        var menu = new Menu(state.Currency, clock) { Version = continueFromVersion };
        foreach (var g in state.CourseGroups)
        {
            menu.AddCourseGroup(g, userId);
        }

        foreach (var c in state.Categories.OrderBy(c => c.SortOrder))
        {
            menu.AddCategory(c, userId);
        }

        foreach (var g in state.ModifierGroups)
        {
            menu.SaveModifierGroup(g, userId);
        }

        // Normal items first, so meal deals can point at them.
        foreach (var p in state.Products.OrderBy(p => p.Type == ProductType.MealDeal).ThenBy(p => p.SortOrder))
        {
            menu.AddProduct(p, userId);
        }

        return menu;
    }

    /// <summary>Buttons in a category, in screen order.</summary>
    public IReadOnlyList<Product> ProductsIn(Guid categoryId) =>
        [.. _products.Values.Where(p => p.CategoryId == categoryId).OrderBy(p => p.SortOrder)];

    /// <summary>Finds a product.</summary>
    public Product GetProduct(Guid id) =>
        _products.TryGetValue(id, out var p) ? p : throw new KeyNotFoundException("Item not found on the menu.");

    /// <summary>Finds a category.</summary>
    public Category GetCategory(Guid id) =>
        _categories.TryGetValue(id, out var c) ? c : throw new KeyNotFoundException("Category not found on the menu.");

    /// <summary>Finds a modifier group.</summary>
    public ModifierGroup GetModifierGroup(Guid id) =>
        _modifierGroups.TryGetValue(id, out var g) ? g : throw new KeyNotFoundException("Extras group not found on the menu.");

    /// <summary>True when the product exists.</summary>
    public bool HasProduct(Guid id) => _products.ContainsKey(id);

    /// <summary>Finds the product that owns a size.</summary>
    public Product? FindProductByVariant(Guid variantId) =>
        _products.Values.FirstOrDefault(p => p.Variants.Any(v => v.Id == variantId));

    /// <summary>Food-type group for an item: its own, else its category's.</summary>
    public CourseGroup? CourseGroupFor(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);
        var id = product.CourseGroupId ?? GetCategory(product.CategoryId).CourseGroupId;
        return id is { } groupId && _courseGroups.TryGetValue(groupId, out var group) ? group : null;
    }

    // ---------- Adding ----------

    /// <summary>Adds a food-type group.</summary>
    public MenuChange AddCourseGroup(CourseGroup group, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(group);
        EnsureNew(_courseGroups.ContainsKey(group.Id));
        return Commit(userId, MenuChangeKind.Added, $"Added food-type group \"{group.Name}\"",
            [Snap(MenuEntityType.CourseGroup, group.Id, null)], [Snap(MenuEntityType.CourseGroup, group.Id, group)]);
    }

    /// <summary>Adds a category tab at the end.</summary>
    public MenuChange AddCategory(Category category, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(category);
        EnsureNew(_categories.ContainsKey(category.Id));
        category = category with { SortOrder = _categories.Count };
        ValidateCategory(category);
        return Commit(userId, MenuChangeKind.Added, $"Added category \"{category.Name}\"",
            [Snap(MenuEntityType.Category, category.Id, null)], [Snap(MenuEntityType.Category, category.Id, category)]);
    }

    /// <summary>Adds or replaces a group of extras.</summary>
    public MenuChange SaveModifierGroup(ModifierGroup group, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(group);
        ValidateModifierGroup(group);
        _modifierGroups.TryGetValue(group.Id, out var before);
        var kind = before is null ? MenuChangeKind.Added : MenuChangeKind.Updated;
        return Commit(userId, kind, $"{(before is null ? "Added" : "Changed")} extras \"{group.Name}\"",
            [Snap(MenuEntityType.ModifierGroup, group.Id, before)], [Snap(MenuEntityType.ModifierGroup, group.Id, group)]);
    }

    /// <summary>Adds an item button to a category, at a position (default: the end).</summary>
    public MenuChange AddProduct(Product product, Guid userId, int? position = null)
    {
        ArgumentNullException.ThrowIfNull(product);
        EnsureNew(_products.ContainsKey(product.Id));
        ValidateProduct(product);

        var siblings = ProductsIn(product.CategoryId).ToList();
        var index = Math.Clamp(position ?? siblings.Count, 0, siblings.Count);
        siblings.Insert(index, product);
        var (before, after) = Renumber(siblings, product.CategoryId);
        return Commit(userId, MenuChangeKind.Added, $"Added \"{product.Name}\" to {GetCategory(product.CategoryId).Name}", before, after);
    }

    // ---------- Editing ----------

    /// <summary>Saves a category's edit panel (name, colour, food-type group, hidden).</summary>
    public MenuChange UpdateCategory(Category category, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(category);
        var current = GetCategory(category.Id);
        category = category with { SortOrder = current.SortOrder }; // position changes only through MoveCategory
        ValidateCategory(category);
        return Commit(userId, MenuChangeKind.Updated, Describe(current, category),
            [Snap(MenuEntityType.Category, category.Id, current)], [Snap(MenuEntityType.Category, category.Id, category)]);
    }

    /// <summary>Saves an item's edit panel: names, prices, VAT, group, station, colour, extras, sold out.</summary>
    public MenuChange UpdateProduct(Product product, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(product);
        var current = GetProduct(product.Id);
        if (product.CategoryId != current.CategoryId)
        {
            throw new InvalidOperationException("Use Move to put an item in another category.");
        }

        product = product with { SortOrder = current.SortOrder };
        ValidateProduct(product);
        return Commit(userId, MenuChangeKind.Updated, Describe(current, product),
            [Snap(MenuEntityType.Product, product.Id, current)], [Snap(MenuEntityType.Product, product.Id, product)]);
    }

    /// <summary>Renames an item (and optionally its button name).</summary>
    public MenuChange RenameProduct(Guid productId, LocalizedText name, Guid userId, LocalizedText? buttonName = null) =>
        UpdateProduct(GetProduct(productId) with { Name = name, ButtonName = buttonName }, userId);

    /// <summary>Changes the price of one size.</summary>
    public MenuChange SetPrice(Guid productId, Guid variantId, Money price, Guid userId)
    {
        var product = GetProduct(productId);
        if (product.Variants.All(v => v.Id != variantId))
        {
            throw new KeyNotFoundException("Size not found on this item.");
        }

        var variants = product.Variants.Select(v => v.Id == variantId ? v with { Price = price } : v).ToList();
        return UpdateProduct(product with { Variants = variants }, userId);
    }

    /// <summary>Marks an item as sold out or back in stock.</summary>
    public MenuChange SetSoldOut(Guid productId, bool soldOut, Guid userId) =>
        UpdateProduct(GetProduct(productId) with { IsSoldOut = soldOut }, userId);

    // ---------- Moving ----------

    /// <summary>Moves an item button to a position, in the same or another category (drag and drop).</summary>
    public MenuChange MoveProduct(Guid productId, Guid toCategoryId, int position, Guid userId)
    {
        var product = GetProduct(productId);
        var target = GetCategory(toCategoryId);

        var before = new List<MenuSnapshot>();
        var after = new List<MenuSnapshot>();
        if (product.CategoryId != toCategoryId)
        {
            var oldSiblings = ProductsIn(product.CategoryId).Where(p => p.Id != productId).ToList();
            var (b, a) = Renumber(oldSiblings, product.CategoryId);
            before.AddRange(b);
            after.AddRange(a);
        }

        var siblings = ProductsIn(toCategoryId).Where(p => p.Id != productId).ToList();
        siblings.Insert(Math.Clamp(position, 0, siblings.Count), product with { CategoryId = toCategoryId });
        var (b2, a2) = Renumber(siblings, toCategoryId);
        before.Add(Snap(MenuEntityType.Product, productId, product));
        before.AddRange(b2.Where(s => s.Id != productId));
        after.AddRange(a2);

        return Commit(userId, MenuChangeKind.Moved, $"Moved \"{product.Name}\" to {target.Name}, position {Math.Clamp(position, 0, siblings.Count - 1) + 1}", before, after);
    }

    /// <summary>Moves a category tab to a position.</summary>
    public MenuChange MoveCategory(Guid categoryId, int position, Guid userId)
    {
        var category = GetCategory(categoryId);
        var tabs = Categories.Where(c => c.Id != categoryId).ToList();
        tabs.Insert(Math.Clamp(position, 0, tabs.Count), category);

        var before = new List<MenuSnapshot>();
        var after = new List<MenuSnapshot>();
        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].SortOrder != i)
            {
                before.Add(Snap(MenuEntityType.Category, tabs[i].Id, tabs[i]));
                after.Add(Snap(MenuEntityType.Category, tabs[i].Id, tabs[i] with { SortOrder = i }));
            }
        }

        return Commit(userId, MenuChangeKind.Moved, $"Moved category {category.Name}", before, after);
    }

    // ---------- Undo ----------

    /// <summary>
    /// Undoes the most recent change that has not been undone yet. Refuses if anything it touched
    /// has been changed again since (for example on another till), so nothing is lost by accident.
    /// </summary>
    public MenuChange UndoLast(Guid userId)
    {
        var last = _log.LastOrDefault(c => c.Kind != MenuChangeKind.Undo && !_undoneVersions.Contains(c.Version))
            ?? throw new InvalidOperationException("There is nothing to undo.");

        foreach (var snap in last.After)
        {
            if (!ReferenceEquals(Current(snap.Type, snap.Id), snap.State))
            {
                throw new InvalidOperationException("This change cannot be undone because the same item has been changed since.");
            }
        }

        _undoneVersions.Add(last.Version);
        return Commit(userId, MenuChangeKind.Undo, $"Undid: {last.Summary}", last.After, last.Before);
    }

    // ---------- Internals ----------

    private MenuChange Commit(Guid userId, MenuChangeKind kind, string summary, IReadOnlyList<MenuSnapshot> before, IReadOnlyList<MenuSnapshot> after)
    {
        foreach (var snap in after)
        {
            Apply(snap);
        }

        Version++;
        var change = new MenuChange(Version, _clock.GetUtcNow(), userId, kind, summary, before, after);
        _log.Add(change);
        return change;
    }

    private void Apply(MenuSnapshot snap)
    {
        switch (snap.Type)
        {
            case MenuEntityType.CourseGroup: Set(_courseGroups, snap.Id, (CourseGroup?)snap.State); break;
            case MenuEntityType.Category: Set(_categories, snap.Id, (Category?)snap.State); break;
            case MenuEntityType.Product: Set(_products, snap.Id, (Product?)snap.State); break;
            case MenuEntityType.ModifierGroup: Set(_modifierGroups, snap.Id, (ModifierGroup?)snap.State); break;
            default: throw new ArgumentOutOfRangeException(nameof(snap));
        }
    }

    private static void Set<T>(Dictionary<Guid, T> map, Guid id, T? value)
        where T : class
    {
        if (value is null)
        {
            map.Remove(id);
        }
        else
        {
            map[id] = value;
        }
    }

    private object? Current(MenuEntityType type, Guid id) => type switch
    {
        MenuEntityType.CourseGroup => _courseGroups.GetValueOrDefault(id),
        MenuEntityType.Category => _categories.GetValueOrDefault(id),
        MenuEntityType.Product => _products.GetValueOrDefault(id),
        MenuEntityType.ModifierGroup => _modifierGroups.GetValueOrDefault(id),
        _ => null,
    };

    private static MenuSnapshot Snap(MenuEntityType type, Guid id, object? state) => new(type, id, state);

    private (List<MenuSnapshot> Before, List<MenuSnapshot> After) Renumber(List<Product> ordered, Guid categoryId)
    {
        var before = new List<MenuSnapshot>();
        var after = new List<MenuSnapshot>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            var existing = _products.GetValueOrDefault(p.Id);
            var updated = p with { SortOrder = i, CategoryId = categoryId };
            if (existing is null || existing.SortOrder != i || existing.CategoryId != categoryId)
            {
                before.Add(Snap(MenuEntityType.Product, p.Id, existing));
                after.Add(Snap(MenuEntityType.Product, p.Id, updated));
            }
        }

        return (before, after);
    }

    private static void EnsureNew(bool exists)
    {
        if (exists)
        {
            throw new InvalidOperationException("Something with this id already exists.");
        }
    }

    private void ValidateCategory(Category category)
    {
        if (!MenuRules.IsValidColour(category.Colour))
        {
            throw new ArgumentException("Colour must look like #RRGGBB.", nameof(category));
        }

        if (category.CourseGroupId is { } g && !_courseGroups.ContainsKey(g))
        {
            throw new ArgumentException("Food-type group not found.", nameof(category));
        }
    }

    private void ValidateModifierGroup(ModifierGroup group)
    {
        if (group.Options.Count == 0)
        {
            throw new ArgumentException("An extras group needs at least one option.", nameof(group));
        }

        ValidateMinMax(group.MinChoices, group.MaxChoices, group.Options.Count, nameof(group));
        if (group.Options.Select(o => o.Id).Distinct().Count() != group.Options.Count)
        {
            throw new ArgumentException("Two extras have the same id.", nameof(group));
        }

        foreach (var option in group.Options)
        {
            CheckPrice(option.Price, nameof(group));
        }
    }

    private void ValidateProduct(Product product)
    {
        if (!_categories.ContainsKey(product.CategoryId))
        {
            throw new ArgumentException("Category not found.", nameof(product));
        }

        if (product.Variants.Count == 0)
        {
            throw new ArgumentException("An item needs at least one size and price.", nameof(product));
        }

        if (product.Variants.Select(v => v.Id).Distinct().Count() != product.Variants.Count)
        {
            throw new ArgumentException("Two sizes have the same id.", nameof(product));
        }

        foreach (var variant in product.Variants)
        {
            CheckPrice(variant.Price, nameof(product));
            var owner = FindProductByVariant(variant.Id);
            if (owner is not null && owner.Id != product.Id)
            {
                throw new ArgumentException("A size id is already used by another item.", nameof(product));
            }
        }

        if (!MenuRules.IsValidColour(product.Colour))
        {
            throw new ArgumentException("Colour must look like #RRGGBB.", nameof(product));
        }

        if (product.CourseGroupId is { } g && !_courseGroups.ContainsKey(g))
        {
            throw new ArgumentException("Food-type group not found.", nameof(product));
        }

        if (product.ModifierGroupIds.Distinct().Count() != product.ModifierGroupIds.Count
            || product.ModifierGroupIds.Any(id => !_modifierGroups.ContainsKey(id)))
        {
            throw new ArgumentException("An extras group is missing or listed twice.", nameof(product));
        }

        if (product.Type == ProductType.Standard && product.DealSlots.Count > 0)
        {
            throw new ArgumentException("Only meal deals have choice slots.", nameof(product));
        }

        if (product.Type == ProductType.MealDeal)
        {
            if (product.DealSlots.Count == 0)
            {
                throw new ArgumentException("A meal deal needs at least one choice.", nameof(product));
            }

            foreach (var slot in product.DealSlots)
            {
                if (slot.Options.Count == 0)
                {
                    throw new ArgumentException($"Choice \"{slot.Name}\" has no options.", nameof(product));
                }

                ValidateMinMax(slot.MinChoices, slot.MaxChoices, int.MaxValue, nameof(product));
                foreach (var option in slot.Options)
                {
                    if (option.Upcharge is { } up)
                    {
                        CheckPrice(up, nameof(product));
                    }

                    var exists = option.Kind switch
                    {
                        SlotOptionKind.Product => _products.TryGetValue(option.TargetId, out var p) && p.Type == ProductType.Standard,
                        SlotOptionKind.Variant => FindProductByVariant(option.TargetId) is { Type: ProductType.Standard },
                        SlotOptionKind.Category => _categories.ContainsKey(option.TargetId),
                        _ => false,
                    };
                    if (!exists)
                    {
                        throw new ArgumentException($"An option in \"{slot.Name}\" points to something not on the menu.", nameof(product));
                    }
                }
            }
        }
    }

    private static void ValidateMinMax(int min, int max, int available, string paramName)
    {
        if (min < 0 || max < 1 || min > max)
        {
            throw new ArgumentException("Choose limits must be: minimum 0 or more, maximum 1 or more, minimum not above maximum.", paramName);
        }

        if (min > available)
        {
            throw new ArgumentException("The minimum is more than the number of options.", paramName);
        }
    }

    private void CheckPrice(Money price, string paramName)
    {
        if (price.IsNegative || price.Currency != Currency)
        {
            throw new ArgumentException($"Prices must be zero or more, in {Currency}.", paramName);
        }
    }

    private static string Describe(Category before, Category after)
    {
        var parts = new List<string>();
        if (before.Name != after.Name)
        {
            parts.Add($"renamed to \"{after.Name}\"");
        }

        if (before.Colour != after.Colour)
        {
            parts.Add("colour changed");
        }

        if (before.CourseGroupId != after.CourseGroupId)
        {
            parts.Add("food-type group changed");
        }

        if (before.IsHidden != after.IsHidden)
        {
            parts.Add(after.IsHidden ? "hidden" : "shown");
        }

        return $"Category \"{before.Name}\": {(parts.Count > 0 ? string.Join(", ", parts) : "saved with no changes")}";
    }

    private static string Describe(Product before, Product after)
    {
        var parts = new List<string>();
        if (before.Name != after.Name)
        {
            parts.Add($"renamed to \"{after.Name}\"");
        }

        if (before.ButtonName != after.ButtonName)
        {
            parts.Add("button name changed");
        }

        foreach (var v in after.Variants)
        {
            var old = before.Variants.FirstOrDefault(x => x.Id == v.Id);
            var label = after.Variants.Count > 1 ? $" ({v.Name})" : string.Empty;
            if (old is null)
            {
                parts.Add($"size {v.Name} added at {Format(v.Price)}");
            }
            else if (old.Price != v.Price)
            {
                parts.Add($"price{label} {Format(old.Price)} → {Format(v.Price)}");
            }
        }

        if (before.Variants.Any(v => after.Variants.All(x => x.Id != v.Id)))
        {
            parts.Add("size removed");
        }

        if (before.IsSoldOut != after.IsSoldOut)
        {
            parts.Add(after.IsSoldOut ? "sold out" : "back in stock");
        }

        if (before.IsHidden != after.IsHidden)
        {
            parts.Add(after.IsHidden ? "hidden" : "shown");
        }

        if (before.VatCategory != after.VatCategory)
        {
            parts.Add($"VAT category {after.VatCategory}");
        }

        if (before.Colour != after.Colour)
        {
            parts.Add("colour changed");
        }

        if (before.CourseGroupId != after.CourseGroupId || before.KitchenStationId != after.KitchenStationId)
        {
            parts.Add("group or kitchen station changed");
        }

        if (!before.ModifierGroupIds.SequenceEqual(after.ModifierGroupIds) || !before.DealSlots.SequenceEqual(after.DealSlots))
        {
            parts.Add("extras or choices changed");
        }

        return $"\"{before.Name}\": {(parts.Count > 0 ? string.Join(", ", parts) : "saved with no changes")}";
    }

    private static string Format(Money money)
    {
        var symbol = money.Currency == "GBP" ? "£" : money.Currency + " ";
        return string.Create(CultureInfo.InvariantCulture, $"{symbol}{money.MinorUnits / 100}.{Math.Abs(money.MinorUnits % 100):00}");
    }
}

/// <summary>A whole menu as plain data: what is saved in the database and sent to tills.</summary>
/// <param name="Currency">Currency of all prices.</param>
/// <param name="Version">Menu version.</param>
/// <param name="CourseGroups">Food-type groups.</param>
/// <param name="Categories">Category tabs.</param>
/// <param name="ModifierGroups">Groups of extras.</param>
/// <param name="Products">Items and meal deals.</param>
public sealed record MenuState(
    string Currency,
    long Version,
    IReadOnlyList<CourseGroup> CourseGroups,
    IReadOnlyList<Category> Categories,
    IReadOnlyList<ModifierGroup> ModifierGroups,
    IReadOnlyList<Product> Products);
