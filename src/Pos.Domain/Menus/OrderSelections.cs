using Pos.Domain.Pricing;
using Pos.Domain.Promotions;

namespace Pos.Domain.Menus;

/// <summary>What staff or a customer picked for a normal item.</summary>
/// <param name="ProductId">The item.</param>
/// <param name="VariantId">The size.</param>
/// <param name="ModifierOptionIds">Extras chosen.</param>
/// <param name="Quantity">How many.</param>
public sealed record ItemSelection(Guid ProductId, Guid VariantId, IReadOnlyList<Guid> ModifierOptionIds, int Quantity = 1);

/// <summary>One choice inside a meal deal, e.g. "Pepperoni 12\", extra cheese" for "Choose a pizza".</summary>
/// <param name="SlotId">The deal's choice slot.</param>
/// <param name="ProductId">Item chosen.</param>
/// <param name="VariantId">Size chosen.</param>
/// <param name="ModifierOptionIds">Extras on the chosen item.</param>
public sealed record SlotChoice(Guid SlotId, Guid ProductId, Guid VariantId, IReadOnlyList<Guid> ModifierOptionIds);

/// <summary>What was picked for a meal deal.</summary>
/// <param name="ProductId">The deal.</param>
/// <param name="VariantId">The deal's size (deals usually have one).</param>
/// <param name="Choices">Choices, in the order made.</param>
/// <param name="Quantity">How many deals.</param>
public sealed record DealSelection(Guid ProductId, Guid VariantId, IReadOnlyList<SlotChoice> Choices, int Quantity = 1);

/// <summary>A reason a selection cannot be added to the order.</summary>
/// <param name="Code">Stable code for the screen to translate, e.g. "SoldOut".</param>
/// <param name="Message">English explanation.</param>
public sealed record SelectionProblem(string Code, string Message);

/// <summary>Thrown when an invalid selection is turned into an order line.</summary>
public sealed class InvalidSelectionException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public InvalidSelectionException()
    {
        Problems = [];
    }

    /// <summary>Creates the exception with a message.</summary>
    public InvalidSelectionException(string message)
        : base(message)
    {
        Problems = [];
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public InvalidSelectionException(string message, Exception innerException)
        : base(message, innerException)
    {
        Problems = [];
    }

    /// <summary>Creates the exception from the problems found.</summary>
    public InvalidSelectionException(IReadOnlyList<SelectionProblem> problems)
        : base(string.Join(" ", (problems ?? []).Select(p => p.Message)))
    {
        Problems = problems ?? [];
    }

    /// <summary>Everything wrong with the selection.</summary>
    public IReadOnlyList<SelectionProblem> Problems { get; }
}

/// <summary>
/// Checks selections against the menu (sizes, extras limits, sold out, meal-deal rules)
/// and turns valid selections into lines for the order pricer.
/// </summary>
public sealed class MenuOrderBuilder
{
    private readonly Menu _menu;

    /// <summary>Creates a builder for a menu.</summary>
    public MenuOrderBuilder(Menu menu) => _menu = menu ?? throw new ArgumentNullException(nameof(menu));

    /// <summary>Lists what is wrong with an item selection (empty when it is fine).</summary>
    public IReadOnlyList<SelectionProblem> Check(ItemSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var problems = new List<SelectionProblem>();
        CheckQuantity(selection.Quantity, problems);
        var product = FindSellable(selection.ProductId, ProductType.Standard, problems);
        if (product is not null)
        {
            CheckVariant(product, selection.VariantId, problems);
            CheckModifiers(product, selection.ModifierOptionIds, problems);
        }

        return problems;
    }

    /// <summary>Lists what is wrong with a meal deal selection (empty when it is fine).</summary>
    public IReadOnlyList<SelectionProblem> Check(DealSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var problems = new List<SelectionProblem>();
        CheckQuantity(selection.Quantity, problems);
        var deal = FindSellable(selection.ProductId, ProductType.MealDeal, problems);
        if (deal is null)
        {
            return problems;
        }

        CheckVariant(deal, selection.VariantId, problems);

        foreach (var unknown in selection.Choices.Where(c => deal.DealSlots.All(s => s.Id != c.SlotId)))
        {
            problems.Add(new("UnknownSlot", $"A choice does not belong to \"{deal.Name}\"."));
        }

        foreach (var slot in deal.DealSlots)
        {
            var choices = selection.Choices.Where(c => c.SlotId == slot.Id).ToList();
            if (choices.Count < slot.MinChoices)
            {
                problems.Add(new("ChoiceMissing", $"{slot.Name}: choose at least {slot.MinChoices}."));
            }

            if (choices.Count > slot.MaxChoices)
            {
                problems.Add(new("TooManyChoices", $"{slot.Name}: choose at most {slot.MaxChoices}."));
            }

            foreach (var choice in choices)
            {
                var item = FindSellable(choice.ProductId, ProductType.Standard, problems);
                if (item is null)
                {
                    continue;
                }

                CheckVariant(item, choice.VariantId, problems);
                if (MatchOption(slot, item, choice.VariantId) is null)
                {
                    problems.Add(new("NotAllowedInDeal", $"\"{item.Name}\" is not one of the choices for {slot.Name}."));
                }

                CheckModifiers(item, choice.ModifierOptionIds, problems);
            }
        }

        return problems;
    }

    /// <summary>Turns a valid item selection into a pricing line.</summary>
    public ItemLineInput ToLine(ItemSelection selection)
    {
        ThrowIfInvalid(Check(selection));
        var product = _menu.GetProduct(selection.ProductId);
        return new ItemLineInput
        {
            Item = ToPricingItem(product, selection.VariantId),
            VatCategory = product.VatCategory,
            Quantity = selection.Quantity,
            Modifiers = ToModifiers(product, selection.ModifierOptionIds),
        };
    }

    /// <summary>Turns a valid meal deal selection into a pricing line. Items are listed in slot order.</summary>
    public MealDealLineInput ToLine(DealSelection selection)
    {
        ThrowIfInvalid(Check(selection));
        var deal = _menu.GetProduct(selection.ProductId);
        var components = new List<DealComponentInput>();
        foreach (var slot in deal.DealSlots)
        {
            foreach (var choice in selection.Choices.Where(c => c.SlotId == slot.Id))
            {
                var item = _menu.GetProduct(choice.ProductId);
                components.Add(new DealComponentInput
                {
                    Item = ToPricingItem(item, choice.VariantId),
                    VatCategory = item.VatCategory,
                    Upcharge = MatchOption(slot, item, choice.VariantId)!.Upcharge,
                    Modifiers = ToModifiers(item, choice.ModifierOptionIds),
                });
            }
        }

        if (components.Count == 0)
        {
            throw new InvalidSelectionException([new SelectionProblem("ChoiceMissing", "Choose at least one item for the deal.")]);
        }

        return new MealDealLineInput { Deal = ToPricingItem(deal, selection.VariantId), Quantity = selection.Quantity, Components = components };
    }

    /// <summary>Print position for items with no food-type group: after all groups.</summary>
    public const int UngroupedSortOrder = 999;

    /// <summary>Names and print details for an item selection, copied from the menu now.</summary>
    public Orders.LineDisplay DisplayFor(ItemSelection selection)
    {
        ThrowIfInvalid(Check(selection));
        return Display(_menu.GetProduct(selection.ProductId), selection.VariantId, selection.ModifierOptionIds, []);
    }

    /// <summary>Names and print details for a meal deal, with what was chosen in it (in slot order).</summary>
    public Orders.LineDisplay DisplayFor(DealSelection selection)
    {
        ThrowIfInvalid(Check(selection));
        var deal = _menu.GetProduct(selection.ProductId);
        var items = deal.DealSlots
            .SelectMany(slot => selection.Choices.Where(c => c.SlotId == slot.Id))
            .Select(c => Display(_menu.GetProduct(c.ProductId), c.VariantId, c.ModifierOptionIds, []))
            .ToList();

        var display = Display(deal, selection.VariantId, [], items);
        if (_menu.CourseGroupFor(deal) is null && items.Count > 0)
        {
            // A deal with no group of its own prints with its earliest item (e.g. with the mains).
            display = display with { CourseSortOrder = items.Min(i => i.CourseSortOrder) };
        }

        return display;
    }

    private Orders.LineDisplay Display(Product product, Guid variantId, IReadOnlyList<Guid> optionIds, IReadOnlyList<Orders.LineDisplay> dealItems)
    {
        var options = product.ModifierGroupIds.Select(_menu.GetModifierGroup).SelectMany(g => g.Options).ToDictionary(o => o.Id);
        return new Orders.LineDisplay(
            product.Name,
            product.Variants.Count > 1 ? product.Variants.Single(v => v.Id == variantId).Name : null,
            [.. optionIds.Select(id => options[id].Name)],
            _menu.CourseGroupFor(product)?.SortOrder ?? UngroupedSortOrder,
            product.KitchenStationId,
            dealItems);
    }

    /// <summary>
    /// Finds the deal option that allows this item. The most specific wins:
    /// a listed size first, then the product, then its category.
    /// </summary>
    private static SlotOption? MatchOption(MealDealSlot slot, Product item, Guid variantId) =>
        slot.Options.FirstOrDefault(o => o.Kind == SlotOptionKind.Variant && o.TargetId == variantId)
        ?? slot.Options.FirstOrDefault(o => o.Kind == SlotOptionKind.Product && o.TargetId == item.Id)
        ?? slot.Options.FirstOrDefault(o => o.Kind == SlotOptionKind.Category && o.TargetId == item.CategoryId);

    private static PricingItem ToPricingItem(Product product, Guid variantId)
    {
        var variant = product.Variants.Single(v => v.Id == variantId);
        return new PricingItem(product.Id, variant.Id, product.CategoryId, variant.Price);
    }

    private List<ModifierInput> ToModifiers(Product product, IReadOnlyList<Guid> optionIds)
    {
        var options = product.ModifierGroupIds
            .Select(_menu.GetModifierGroup)
            .SelectMany(g => g.Options)
            .ToDictionary(o => o.Id);
        return [.. optionIds.Select(id => new ModifierInput(id, options[id].Price))];
    }

    private Product? FindSellable(Guid productId, ProductType expectedType, List<SelectionProblem> problems)
    {
        if (!_menu.HasProduct(productId))
        {
            problems.Add(new("NotOnMenu", "This item is no longer on the menu."));
            return null;
        }

        var product = _menu.GetProduct(productId);
        if (product.Type != expectedType)
        {
            problems.Add(new("WrongType", expectedType == ProductType.MealDeal
                ? $"\"{product.Name}\" is not a meal deal."
                : $"\"{product.Name}\" is a meal deal and cannot be chosen here."));
            return null;
        }

        if (product.IsHidden || _menu.GetCategory(product.CategoryId).IsHidden)
        {
            problems.Add(new("Hidden", $"\"{product.Name}\" is not available."));
        }

        if (product.IsSoldOut)
        {
            problems.Add(new("SoldOut", $"\"{product.Name}\" is sold out."));
        }

        return product;
    }

    private static void CheckVariant(Product product, Guid variantId, List<SelectionProblem> problems)
    {
        var variant = product.Variants.FirstOrDefault(v => v.Id == variantId);
        if (variant is null)
        {
            problems.Add(new("SizeNotFound", $"Choose a size for \"{product.Name}\"."));
        }
        else if (variant.IsSoldOut)
        {
            problems.Add(new("SoldOut", $"\"{product.Name}\" ({variant.Name}) is sold out."));
        }
    }

    private void CheckModifiers(Product product, IReadOnlyList<Guid> optionIds, List<SelectionProblem> problems)
    {
        if (optionIds.Distinct().Count() != optionIds.Count)
        {
            problems.Add(new("DuplicateExtra", "The same extra was chosen twice."));
        }

        var groups = product.ModifierGroupIds.Select(_menu.GetModifierGroup).ToList();
        var known = groups.SelectMany(g => g.Options).Select(o => o.Id).ToHashSet();
        if (optionIds.Any(id => !known.Contains(id)))
        {
            problems.Add(new("ExtraNotAllowed", $"An extra chosen is not offered with \"{product.Name}\"."));
        }

        foreach (var group in groups)
        {
            var chosen = group.Options.Where(o => optionIds.Contains(o.Id)).ToList();
            if (chosen.Count < group.MinChoices)
            {
                problems.Add(new("ExtraMissing", $"{group.Name}: choose at least {group.MinChoices}."));
            }

            if (chosen.Count > group.MaxChoices)
            {
                problems.Add(new("TooManyExtras", $"{group.Name}: choose at most {group.MaxChoices}."));
            }

            foreach (var option in chosen.Where(o => o.IsSoldOut))
            {
                problems.Add(new("SoldOut", $"{option.Name} is sold out."));
            }
        }
    }

    private static void CheckQuantity(int quantity, List<SelectionProblem> problems)
    {
        if (quantity < 1)
        {
            problems.Add(new("BadQuantity", "Quantity must be at least 1."));
        }
    }

    private static void ThrowIfInvalid(IReadOnlyList<SelectionProblem> problems)
    {
        if (problems.Count > 0)
        {
            throw new InvalidSelectionException(problems);
        }
    }
}
