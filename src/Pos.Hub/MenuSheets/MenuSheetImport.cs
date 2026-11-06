using System.Globalization;
using System.Text;
using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Tax;

namespace Pos.Hub.MenuSheets;

/// <summary>What happens to items already on the till that the spreadsheet does not list.</summary>
public enum MissingItems
{
    /// <summary>Left exactly as they are (the safe choice for a sheet of only some items).</summary>
    Keep,

    /// <summary>Hidden from the till (not deleted: they can be shown again in Edit menu).</summary>
    Hide,
}

/// <summary>Something wrong in the spreadsheet, with the row to look at (0 when it is not about one row).</summary>
/// <param name="Sheet">Sheet name.</param>
/// <param name="Row">Spreadsheet row number, as Excel shows it.</param>
/// <param name="Message">What to fix.</param>
public sealed record ImportProblem(string Sheet, int Row, string Message);

/// <summary>What an import would do, shown before anything is changed.</summary>
/// <param name="Categories">Categories in the sheet.</param>
/// <param name="NewCategories">Of those, new ones.</param>
/// <param name="Items">Items in the sheet.</param>
/// <param name="NewItems">Of those, new ones.</param>
/// <param name="ChangedItems">Existing items that change (price, name, sizes…).</param>
/// <param name="UnchangedItems">Existing items that stay the same.</param>
/// <param name="ExtrasGroups">Extras groups in the sheet.</param>
/// <param name="NewExtrasGroups">Of those, new ones.</param>
/// <param name="NotInSheet">Items on the till that the sheet does not list (meal deals not counted).</param>
/// <param name="Hidden">Items that will be hidden (when chosen).</param>
/// <param name="Changes">Plain-English list of what changes, for checking (the first few hundred).</param>
/// <param name="Problems">What must be fixed before importing.</param>
public sealed record ImportPreview(
    int Categories,
    int NewCategories,
    int Items,
    int NewItems,
    int ChangedItems,
    int UnchangedItems,
    int ExtrasGroups,
    int NewExtrasGroups,
    int NotInSheet,
    int Hidden,
    IReadOnlyList<string> Changes,
    IReadOnlyList<ImportProblem> Problems)
{
    /// <summary>True when there are no problems and something would change.</summary>
    public bool CanImport => Problems.Count == 0 && (NewCategories + NewItems + ChangedItems + NewExtrasGroups + Hidden > 0 || Changes.Count > 0);
}

/// <summary>The preview and, when it has no problems, the whole new menu.</summary>
/// <param name="Preview">What would happen.</param>
/// <param name="State">The menu after the import, already checked; null when there are problems.</param>
public sealed record ImportPlan(ImportPreview Preview, MenuState? State);

/// <summary>
/// Turns a menu spreadsheet into menu changes. One row per size: rows with the same category and item are one
/// item with several sizes. Existing categories, items, sizes and extras are found by name and keep their ids,
/// so kitchen printer choices, reports and open orders stay linked; importing the same sheet twice changes nothing.
/// A column that is left out keeps what the till has; meal deals are never changed by a sheet.
/// </summary>
public static class MenuSheetImport
{
    /// <summary>Sheet with the items.</summary>
    public const string MenuSheet = "Menu";

    /// <summary>Sheet with the extras groups.</summary>
    public const string ExtrasSheet = "Extras";

    /// <summary>Most items accepted from one sheet.</summary>
    public const int MaxItems = 2000;

    private const long MaxPence = 99_999_99;
    private const int MaxChangesListed = 400;

    private static readonly string[] Palette = ["#c2410c", "#b91c1c", "#a16207", "#4d7c0f", "#0f766e", "#1d4ed8", "#7c3aed", "#be185d", "#475569", "#15803d"];

    /// <summary>The column headings of the Menu sheet, in template order.</summary>
    public static readonly IReadOnlyList<string> MenuHeadings = ["Category", "Item", "Size", "Price", "VAT", "Description", "Extras", "Sold out"];

    /// <summary>The column headings of the Extras sheet, in template order.</summary>
    public static readonly IReadOnlyList<string> ExtrasHeadings = ["Group", "Extra", "Price", "Choose at least", "Choose at most"];

    /// <summary>Works out what importing the sheets would do, without changing anything.</summary>
    public static ImportPlan Plan(MenuState current, IReadOnlyList<SheetData> sheets, MissingItems missing, Guid userId, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(sheets);
        var work = new Planner(current, missing);
        // A CSV file is a single sheet named after the file; its problems are still shown as "Menu row 3".
        var menuSheet = FindSheet(sheets, MenuSheet, "item", "price") ?? (sheets.Count == 1 ? sheets[0] : null);
        if (menuSheet is not null && sheets.Count == 1)
        {
            menuSheet = menuSheet with { Name = MenuSheet };
        }
        var extrasSheet = FindSheet(sheets, ExtrasSheet, "group", "extra");
        if (extrasSheet is not null && ReferenceEquals(extrasSheet, menuSheet))
        {
            extrasSheet = null;
        }

        if (extrasSheet is not null)
        {
            work.ReadExtras(extrasSheet);
        }

        if (menuSheet is null)
        {
            work.Problem(sheets.Count > 0 ? sheets[0].Name : MenuSheet, 0, "No Menu sheet found. Use the template: its first row is Category, Item, Size, Price, VAT…");
        }
        else
        {
            work.ReadMenu(menuSheet);
        }

        return work.Finish(userId, clock);
    }

    /// <summary>
    /// The menu as a workbook for editing in Excel: a Menu sheet, an Extras sheet and a How-to sheet.
    /// An empty menu gets a few example rows to copy. Meal deals and hidden items are left out.
    /// </summary>
    public static byte[] Workbook(MenuState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var menuRows = new List<IReadOnlyList<string>> { MenuHeadings };
        var groups = current.ModifierGroups.ToDictionary(g => g.Id);
        var categories = current.Categories.Where(c => !c.IsHidden).OrderBy(c => c.SortOrder).ToList();
        foreach (var category in categories)
        {
            foreach (var p in current.Products.Where(p => p.CategoryId == category.Id && p.Type == ProductType.Standard && !p.IsHidden).OrderBy(p => p.SortOrder))
            {
                var extras = string.Join("; ", p.ModifierGroupIds.Where(groups.ContainsKey).Select(id => groups[id].Name.En));
                for (var i = 0; i < p.Variants.Count; i++)
                {
                    var v = p.Variants[i];
                    menuRows.Add([
                        category.Name.En,
                        p.Name.En,
                        p.Variants.Count == 1 ? string.Empty : v.Name.En,
                        Price(v.Price),
                        VatName(p.VatCategory),
                        i == 0 ? p.Description?.En ?? string.Empty : string.Empty,
                        i == 0 ? extras : string.Empty,
                        p.IsSoldOut || v.IsSoldOut ? "Yes" : string.Empty,
                    ]);
                }
            }
        }

        var extrasRows = new List<IReadOnlyList<string>> { ExtrasHeadings };
        foreach (var g in current.ModifierGroups.OrderBy(g => g.Name.En, StringComparer.OrdinalIgnoreCase))
        {
            for (var i = 0; i < g.Options.Count; i++)
            {
                extrasRows.Add([
                    g.Name.En, g.Options[i].Name.En, Price(g.Options[i].Price),
                    i == 0 ? g.MinChoices.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    i == 0 ? g.MaxChoices.ToString(CultureInfo.InvariantCulture) : string.Empty,
                ]);
            }
        }

        if (menuRows.Count == 1)
        {
            menuRows.AddRange(ExampleMenuRows);
            if (extrasRows.Count == 1)
            {
                extrasRows.AddRange(ExampleExtrasRows);
            }
        }

        var sheets = new List<SheetData>
        {
            new(MenuSheet, menuRows),
            new(ExtrasSheet, extrasRows),
            new("How to", [.. HowTo.Select(line => (IReadOnlyList<string>)[line])]),
        };
        return Spreadsheet.WriteXlsx(sheets, new Dictionary<string, int[]>
        {
            [MenuSheet] = [18, 34, 10, 9, 11, 50, 26, 9],
            [ExtrasSheet] = [24, 28, 9, 15, 15],
            ["How to"] = [110],
        });
    }

    /// <summary>VAT words used in the sheet.</summary>
    public static string VatName(VatCategory vat) => vat switch
    {
        VatCategory.HotFood => "Hot food",
        VatCategory.ColdFood => "Cold food",
        _ => "Standard",
    };

    private static readonly IReadOnlyList<IReadOnlyList<string>> ExampleMenuRows =
    [
        ["Pizzas", "Margherita", "10\"", "7.99", "Hot food", "Tomato and mozzarella", "Extra toppings", ""],
        ["Pizzas", "Margherita", "12\"", "9.99", "Hot food", "", "", ""],
        ["Pizzas", "Pepperoni", "10\"", "8.99", "Hot food", "", "Extra toppings", ""],
        ["", "", "12\"", "10.99", "", "", "", ""],
        ["Sides", "Chips", "", "2.50", "Hot food", "", "", ""],
        ["Sides", "Coleslaw", "", "1.20", "Cold food", "", "", ""],
        ["Drinks", "Can of cola", "", "1.50", "Standard", "", "", ""],
    ];

    private static readonly IReadOnlyList<IReadOnlyList<string>> ExampleExtrasRows =
    [
        ["Extra toppings", "Extra cheese", "1.00", "0", "5"],
        ["Extra toppings", "Mushrooms", "0.80", "", ""],
        ["Extra toppings", "Jalapeños", "0.80", "", ""],
    ];

    private static readonly string[] HowTo =
    [
        "How to fill in the menu spreadsheet",
        "Menu sheet: one row for each size of each item. Rows with the same Category and Item become one button with several sizes.",
        "Category: the tab on the till, e.g. Pizzas. Leave it empty to use the category from the row above.",
        "Item: the name on the button and receipt. Leave it empty (and fill in Size) to add another size to the item above.",
        "Size: e.g. 12\", Large, Meal. Leave it empty for an item with only one price.",
        "Price: the selling price including VAT, e.g. 8.99 (a £ sign is fine).",
        "VAT: Hot food (pizza, kebabs, chips, hot drinks…), Cold food (cold sandwiches, salads, cakes, coleslaw…) or Standard (soft drinks, water, alcohol, sweets, crisps, ice cream). Ask your accountant if unsure.",
        "Description: optional, for the website and the item's details.",
        "Extras: optional, the names of extras groups from the Extras sheet, separated by ; (e.g. Extra toppings; Dips).",
        "Sold out: Yes for something that has run out today; leave empty otherwise.",
        "Extras sheet: one row for each extra. Group is the group name (empty = the group above), Extra its name, Price its price (0 for free). Choose at least / at most: how many the customer must and may pick (default 0 and all).",
        "Importing: More → Menu spreadsheet → Choose the file. The till shows what will change and any rows to fix, and changes nothing until you press Import.",
        "Items already on the till are found by name and keep their printer choices and sales history. Importing the same sheet again changes nothing.",
        "A column you delete from the sheet is left as it is on the till. Meal deals are set up in Edit menu and are never changed by the sheet.",
        "A backup is made before every import, and the last import can be undone from the same screen.",
    ];

    private static SheetData? FindSheet(IReadOnlyList<SheetData> sheets, string name, params string[] headings) =>
        sheets.FirstOrDefault(s => string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
        ?? sheets.FirstOrDefault(s => HeaderRow(s) is { } h && headings.All(x => Columns(s.Rows[h]).ContainsKey(x)));

    private static int? HeaderRow(SheetData sheet)
    {
        for (var i = 0; i < sheet.Rows.Count && i < 20; i++)
        {
            if (sheet.Rows[i].Any(c => !string.IsNullOrWhiteSpace(c)))
            {
                return i;
            }
        }

        return null;
    }

    // Heading → column number, by a simplified heading ("Price (£)" → "price", "Sold out?" → "soldout").
    private static Dictionary<string, int> Columns(IReadOnlyList<string> header)
    {
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Count; i++)
        {
            var key = new string([.. header[i].ToLowerInvariant().Where(char.IsAsciiLetter)]) switch
            {
                "category" or "categories" or "tab" => "category",
                "item" or "items" or "name" or "itemname" or "product" => "item",
                "size" or "sizes" or "variant" or "option" => "size",
                "price" or "prices" or "pricegbp" or "priceincvat" => "price",
                "vat" or "vattype" or "vatcategory" or "vatrate" => "vat",
                "description" or "details" => "description",
                "extras" or "extrasgroups" or "extrasgroup" or "modifiers" => "extras",
                "soldout" => "soldout",
                "group" or "extrasgroupname" or "groupname" => "group",
                "extra" or "extraname" => "extra",
                "chooseatleast" or "min" or "minimum" => "min",
                "chooseatmost" or "max" or "maximum" => "max",
                var other => other,
            };
            if (key.Length > 0)
            {
                columns.TryAdd(key, i);
            }
        }

        return columns;
    }

    private static string Price(Money money) =>
        string.Create(CultureInfo.InvariantCulture, $"{money.MinorUnits / 100}.{Math.Abs(money.MinorUnits % 100):00}");

    private static string Pounds(long pence) => Money.Gbp(pence).Display();

    private static string Key(string text) => text.Trim().ToUpperInvariant();

    /// <summary>The working state of one import.</summary>
    private sealed class Planner(MenuState current, MissingItems missing)
    {
        private readonly List<ImportProblem> _problems = [];
        private readonly List<string> _changes = [];
        private readonly Dictionary<string, ModifierGroup> _groupsByName = current.ModifierGroups.ToDictionary(g => Key(g.Name.En), StringComparer.Ordinal);
        private readonly Dictionary<Guid, ModifierGroup> _groups = current.ModifierGroups.ToDictionary(g => g.Id);
        private readonly List<SheetCategory> _sheetCategories = [];
        private readonly HashSet<string> _sheetGroupNames = new(StringComparer.Ordinal);
        private int _newGroups;
        private string _menuName = MenuSheet;
        private bool _hasVat;
        private bool _hasDescription;
        private bool _hasExtras;
        private bool _hasSoldOut;

        public void Problem(string sheet, int row, string message) => _problems.Add(new ImportProblem(sheet, row, message));

        // ----- Extras sheet -----
        public void ReadExtras(SheetData sheet)
        {
            if (HeaderRow(sheet) is not { } header)
            {
                return; // an empty Extras sheet is fine
            }

            var col = Columns(sheet.Rows[header]);
            if (!col.ContainsKey("group") || !col.ContainsKey("extra"))
            {
                Problem(sheet.Name, header + 1, "The Extras sheet needs the headings Group, Extra and Price in its first row.");
                return;
            }

            var read = new List<(string Group, int Row, string Name, long Pence, int? Min, int? Max)>();
            string? group = null;
            for (var r = header + 1; r < sheet.Rows.Count; r++)
            {
                var cells = sheet.Rows[r];
                string Cell(string name) => col.TryGetValue(name, out var i) && i < cells.Count ? cells[i].Trim() : string.Empty;
                if (cells.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                var row = r + 1;
                group = Cell("group") is { Length: > 0 } g ? g : group;
                var name = Cell("extra");
                if (group is null)
                {
                    Problem(sheet.Name, row, "Group is empty.");
                    continue;
                }

                if (name.Length == 0)
                {
                    Problem(sheet.Name, row, "Extra (its name) is empty.");
                    continue;
                }

                if (!CheckName(sheet.Name, row, group, "Group") || !CheckName(sheet.Name, row, name, "Extra"))
                {
                    continue;
                }

                var pence = Cell("price").Length == 0 ? 0 : ParsePrice(sheet.Name, row, Cell("price"));
                var min = ParseCount(sheet.Name, row, Cell("min"), "Choose at least");
                var max = ParseCount(sheet.Name, row, Cell("max"), "Choose at most");
                if (pence is null || min == -1 || max == -1)
                {
                    continue;
                }

                read.Add((group, row, name, pence.Value, min, max));
            }

            foreach (var g in read.GroupBy(x => Key(x.Group), StringComparer.Ordinal))
            {
                var rows = g.ToList();
                var first = rows[0];
                var duplicate = rows.GroupBy(x => Key(x.Name), StringComparer.Ordinal).FirstOrDefault(x => x.Count() > 1);
                if (duplicate is not null)
                {
                    Problem(sheet.Name, duplicate.Skip(1).First().Row, $"\"{duplicate.First().Name}\" is listed twice in extras group \"{first.Group}\".");
                    continue;
                }

                var min = rows.Select(x => x.Min).FirstOrDefault(x => x is not null) ?? 0;
                var max = rows.Select(x => x.Max).FirstOrDefault(x => x is not null) ?? rows.Count;
                if (min > max || max < 1 || min > rows.Count)
                {
                    Problem(sheet.Name, first.Row, $"Extras group \"{first.Group}\": choose at least {min} and at most {max} does not work with {rows.Count} extras.");
                    continue;
                }

                _groupsByName.TryGetValue(g.Key, out var existing);
                var options = rows.Select(x =>
                {
                    var old = existing?.Options.FirstOrDefault(o => Key(o.Name.En) == Key(x.Name));
                    return new ModifierOption(old?.Id ?? Guid.NewGuid(), new LocalizedText(x.Name, old?.Name.Ar), new Money(x.Pence, current.Currency), old?.IsSoldOut ?? false);
                }).ToList();
                var updated = new ModifierGroup(existing?.Id ?? Guid.NewGuid(), new LocalizedText(first.Group, existing?.Name.Ar), min, max, options);
                if (existing is null)
                {
                    _newGroups++;
                    Change($"New extras group: {first.Group} ({rows.Count} extras)");
                }
                else if (!SameGroup(existing, updated))
                {
                    Change($"Extras group {first.Group}: changed");
                }

                _groupsByName[g.Key] = updated;
                _groups[updated.Id] = updated;
                _sheetGroupNames.Add(g.Key);
            }
        }

        // ----- Menu sheet -----
        public void ReadMenu(SheetData sheet)
        {
            _menuName = sheet.Name;
            if (HeaderRow(sheet) is not { } header)
            {
                Problem(sheet.Name, 0, "The Menu sheet is empty.");
                return;
            }

            var col = Columns(sheet.Rows[header]);
            var missingHeadings = new[] { ("category", "Category"), ("item", "Item"), ("price", "Price") }.Where(x => !col.ContainsKey(x.Item1)).Select(x => x.Item2).ToList();
            if (missingHeadings.Count > 0)
            {
                Problem(sheet.Name, header + 1, $"The first row must have the headings Category, Item, Size, Price, VAT… (missing: {string.Join(", ", missingHeadings)}).");
                return;
            }

            _hasVat = col.ContainsKey("vat");
            _hasDescription = col.ContainsKey("description");
            _hasExtras = col.ContainsKey("extras");
            _hasSoldOut = col.ContainsKey("soldout");

            string? category = null;
            SheetItem? lastItem = null;
            for (var r = header + 1; r < sheet.Rows.Count; r++)
            {
                var cells = sheet.Rows[r];
                string Cell(string name) => col.TryGetValue(name, out var i) && i < cells.Count ? cells[i].Trim() : string.Empty;
                if (cells.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                var row = r + 1;
                var categoryCell = Cell("category");
                var itemCell = Cell("item");
                var sizeCell = Cell("size");
                if (categoryCell.Length > 0)
                {
                    if (!CheckName(sheet.Name, row, categoryCell, "Category"))
                    {
                        continue;
                    }

                    category = categoryCell;
                }

                if (category is null)
                {
                    Problem(sheet.Name, row, "Category is empty.");
                    continue;
                }

                SheetItem item;
                if (itemCell.Length == 0)
                {
                    if (lastItem is null || sizeCell.Length == 0 || (categoryCell.Length > 0 && Key(categoryCell) != Key(lastItem.Category.Name)))
                    {
                        Problem(sheet.Name, row, "Item (its name) is empty.");
                        continue;
                    }

                    item = lastItem; // another size of the item above
                }
                else
                {
                    if (!CheckName(sheet.Name, row, itemCell, "Item"))
                    {
                        continue;
                    }

                    var cat = _sheetCategories.FirstOrDefault(c => Key(c.Name) == Key(category));
                    if (cat is null)
                    {
                        cat = new SheetCategory(category, row);
                        _sheetCategories.Add(cat);
                    }

                    item = cat.Items.FirstOrDefault(i => Key(i.Name) == Key(itemCell)) ?? cat.Add(itemCell, row);
                }

                if (sizeCell.Length > 0 && !CheckName(sheet.Name, row, sizeCell, "Size"))
                {
                    continue;
                }

                var price = Cell("price");
                long? pence = price.Length == 0 ? null : ParsePrice(sheet.Name, row, price);
                if (price.Length == 0)
                {
                    Problem(sheet.Name, row, $"{item.Name}: Price is empty.");
                }

                var vatCell = Cell("vat");
                VatCategory? vat = null;
                if (vatCell.Length > 0)
                {
                    vat = ParseVat(vatCell);
                    if (vat is null)
                    {
                        Problem(sheet.Name, row, $"{item.Name}: VAT \"{vatCell}\" is not known. Use Hot food, Cold food or Standard.");
                    }
                    else if (item.Vat is { } earlier && earlier != vat)
                    {
                        Problem(sheet.Name, row, $"{item.Name}: every size must have the same VAT ({VatName(earlier)} on row {item.Row}).");
                    }
                    else
                    {
                        item.Vat = vat;
                    }
                }

                if (Cell("description") is { Length: > 0 } description)
                {
                    if (description.Length > 500)
                    {
                        Problem(sheet.Name, row, $"{item.Name}: Description is longer than 500 characters.");
                    }
                    else
                    {
                        item.Description ??= description;
                    }
                }

                if (Cell("extras") is { Length: > 0 } extras)
                {
                    foreach (var name in extras.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!_groupsByName.TryGetValue(Key(name), out var g))
                        {
                            Problem(sheet.Name, row, $"{item.Name}: extras group \"{name}\" is not on the Extras sheet or the till.");
                        }
                        else if (!item.Extras.Contains(g.Id))
                        {
                            item.Extras.Add(g.Id);
                        }
                    }
                }

                var soldOutCell = Cell("soldout");
                var soldOut = ParseYesNo(soldOutCell);
                if (soldOut is null)
                {
                    Problem(sheet.Name, row, $"{item.Name}: Sold out \"{soldOutCell}\" should be Yes or empty.");
                }

                if (item.Sizes.Any(s => Key(s.Name) == Key(sizeCell)))
                {
                    var first = item.Sizes.First(s => Key(s.Name) == Key(sizeCell));
                    Problem(sheet.Name, row, sizeCell.Length == 0
                        ? $"{item.Name} is listed twice (rows {first.Row} and {row}). Give each row a Size, or remove one."
                        : $"{item.Name}: size {sizeCell} is listed twice (rows {first.Row} and {row}).");
                    continue;
                }

                if (pence is not null)
                {
                    item.Sizes.Add(new SheetSize(sizeCell, pence.Value, soldOut ?? false, row));
                }

                lastItem = item;
            }

            if (_sheetCategories.Sum(c => c.Items.Count) > MaxItems)
            {
                Problem(sheet.Name, 0, $"The sheet has more than {MaxItems:N0} items.");
            }
            else if (_sheetCategories.Count == 0 && _problems.Count == 0)
            {
                Problem(sheet.Name, 0, "The Menu sheet has headings but no items.");
            }
        }

        // ----- Putting it together -----
        public ImportPlan Finish(Guid userId, TimeProvider? clock)
        {
            var categories = current.Categories.OrderBy(c => c.SortOrder).ToList();
            var products = current.Products.ToList();
            var byId = products.ToDictionary(p => p.Id);
            var catByName = new Dictionary<string, Category>(StringComparer.Ordinal);
            foreach (var c in categories)
            {
                catByName.TryAdd(Key(c.Name.En), c);
            }

            var used = new HashSet<Guid>();
            var newCategories = 0;
            int newItems = 0, changedItems = 0, unchangedItems = 0;
            var categoryOrder = new List<Guid>();
            var productOrder = new Dictionary<Guid, List<Guid>>();

            foreach (var sc in _sheetCategories)
            {
                if (!catByName.TryGetValue(Key(sc.Name), out var category))
                {
                    category = new Category { Id = Guid.NewGuid(), Name = new LocalizedText(sc.Name), Colour = Palette[(categories.Count + newCategories) % Palette.Length] };
                    newCategories++;
                    Change($"New category: {sc.Name}");
                    categories.Add(category);
                    catByName[Key(sc.Name)] = category;
                }
                else if (category.Name.En != sc.Name || category.IsHidden)
                {
                    var renamed = category with { Name = new LocalizedText(sc.Name, category.Name.Ar), IsHidden = false };
                    Change(category.IsHidden ? $"Category {sc.Name}: shown again" : $"Category {category.Name.En}: renamed to {sc.Name}");
                    categories[categories.IndexOf(category)] = renamed;
                    catByName[Key(sc.Name)] = renamed;
                    category = renamed;
                }

                categoryOrder.Add(category.Id);
                var order = productOrder[category.Id] = [];
                foreach (var item in sc.Items)
                {
                    if (item.Sizes.Count == 0)
                    {
                        continue; // its rows had problems, already reported
                    }

                    var existing = FindExisting(products, category.Id, item.Name, used);
                    if (existing is { Type: ProductType.MealDeal })
                    {
                        Problem(_menuName, item.Row, $"{item.Name} is a meal deal on the till. Change meal deals in Edit menu, or give this item another name.");
                        continue;
                    }

                    if (existing is null && !_hasVat)
                    {
                        Problem(_menuName, item.Row, $"{item.Name}: new items need a VAT column (Hot food, Cold food or Standard).");
                        continue;
                    }

                    if (existing is null && item.Vat is null)
                    {
                        Problem(_menuName, item.Row, $"{item.Name}: VAT is empty. Use Hot food, Cold food or Standard.");
                        continue;
                    }

                    var built = Build(item, category.Id, existing);
                    if (existing is null)
                    {
                        newItems++;
                        products.Add(built);
                        Change($"New: {category.Name.En} › {Describe(built)}");
                    }
                    else
                    {
                        used.Add(existing.Id);
                        var changes = Differences(existing, built, category);
                        if (changes.Count == 0)
                        {
                            unchangedItems++;
                        }
                        else
                        {
                            changedItems++;
                            Change($"{category.Name.En} › {existing.Name.En}: {string.Join(", ", changes)}");
                        }

                        products[products.IndexOf(byId[existing.Id])] = built;
                    }

                    used.Add(built.Id);
                    byId[built.Id] = built;
                    order.Add(built.Id);
                }
            }

            // Items the sheet does not list.
            var notListed = products.Where(p => p.Type == ProductType.Standard && !used.Contains(p.Id) && !p.IsHidden).ToList();
            var hidden = 0;
            if (missing == MissingItems.Hide && _problems.Count == 0)
            {
                foreach (var p in notListed)
                {
                    products[products.IndexOf(p)] = p with { IsHidden = true };
                    hidden++;
                }

                if (hidden > 0)
                {
                    Change($"Hidden (not in the sheet): {hidden} {(hidden == 1 ? "item" : "items")}");
                }

                // A category left with nothing to show is hidden too.
                for (var i = 0; i < categories.Count; i++)
                {
                    var c = categories[i];
                    if (!c.IsHidden && !categoryOrder.Contains(c.Id) && !products.Any(p => p.CategoryId == c.Id && !p.IsHidden))
                    {
                        categories[i] = c with { IsHidden = true };
                        Change($"Category {c.Name.En}: hidden (nothing left to show)");
                    }
                }
            }

            var preview = new ImportPreview(
                _sheetCategories.Count, newCategories, _sheetCategories.Sum(c => c.Items.Count), newItems, changedItems, unchangedItems,
                _sheetGroupNames.Count, _newGroups, notListed.Count, hidden, [.. _changes.Take(MaxChangesListed)], [.. _problems.OrderBy(p => p.Sheet == _menuName ? 1 : 0).ThenBy(p => p.Row)]);
            if (_problems.Count > 0)
            {
                return new ImportPlan(preview, null);
            }

            // Order: the sheet's categories and items first, in sheet order; everything else after, as it was.
            var orderedCategories = categoryOrder.Select(id => categories.First(c => c.Id == id))
                .Concat(categories.Where(c => !categoryOrder.Contains(c.Id)))
                .Select((c, i) => c with { SortOrder = i }).ToList();
            var orderedProducts = new List<Product>();
            foreach (var c in orderedCategories)
            {
                var listed = productOrder.TryGetValue(c.Id, out var ids) ? ids : [];
                var inCategory = listed.Select(id => byId[id])
                    .Concat(products.Where(p => p.CategoryId == c.Id && !listed.Contains(p.Id)).OrderBy(p => p.SortOrder));
                orderedProducts.AddRange(inCategory.Select((p, i) => products.First(x => x.Id == p.Id) with { SortOrder = i }));
            }

            var state = new MenuState(current.Currency, current.Version, current.CourseGroups, orderedCategories, [.. _groups.Values], orderedProducts);

            // A dry run of the real import, so anything the menu rules refuse is shown now, not halfway through.
            try
            {
                _ = Menu.Import(state, userId, clock);
            }
            catch (ArgumentException ex)
            {
                return new ImportPlan(preview with { Problems = [new ImportProblem(_menuName, 0, ex.Message)] }, null);
            }

            return new ImportPlan(preview, state);
        }

        private Product Build(SheetItem item, Guid categoryId, Product? existing)
        {
            var allSoldOut = item.Sizes.All(x => x.SoldOut);
            var variants = item.Sizes.Select(s =>
            {
                // A named size keeps the id of the size with that name. An empty size is the item's only price:
                // it keeps the item's only size whatever it is called (the spreadsheet leaves single sizes empty).
                var old = s.Name.Length > 0
                    ? existing?.Variants.FirstOrDefault(v => Key(v.Name.En) == Key(s.Name))
                    : existing is { Variants.Count: 1 } ? existing.Variants[0]
                    : existing?.Variants.FirstOrDefault(v => Key(v.Name.En) == "REGULAR");
                var name = s.Name.Length > 0 ? new LocalizedText(s.Name, old?.Name.Ar) : old?.Name ?? new LocalizedText("Regular");
                // When every size has run out the item itself is marked sold out; otherwise just the sizes that have.
                var soldOut = _hasSoldOut ? s.SoldOut && !allSoldOut : old?.IsSoldOut ?? false;
                return new ProductVariant(old?.Id ?? Guid.NewGuid(), name, new Money(s.Pence, current.Currency), soldOut);
            }).ToList();

            var name = new LocalizedText(item.Name, existing?.Name.Ar);
            var description = _hasDescription
                ? item.Description is null ? null : new LocalizedText(item.Description, existing?.Description?.Ar)
                : existing?.Description;
            if (existing is null)
            {
                return new Product
                {
                    Id = Guid.NewGuid(),
                    CategoryId = categoryId,
                    Name = name,
                    Description = description,
                    Variants = variants,
                    VatCategory = item.Vat!.Value,
                    ModifierGroupIds = [.. item.Extras],
                    IsSoldOut = _hasSoldOut && item.Sizes.All(s => s.SoldOut),
                };
            }

            return existing with
            {
                CategoryId = categoryId,
                Name = name,
                Description = description,
                Variants = variants,
                VatCategory = item.Vat ?? existing.VatCategory,
                ModifierGroupIds = _hasExtras ? [.. item.Extras] : existing.ModifierGroupIds,
                IsSoldOut = _hasSoldOut ? item.Sizes.All(s => s.SoldOut) : existing.IsSoldOut,
                IsHidden = false,
            };
        }

        // The same name in the same category; else the same name in another category, once only (the item was moved).
        private static Product? FindExisting(List<Product> products, Guid categoryId, string name, HashSet<Guid> used)
        {
            var named = products.Where(p => !used.Contains(p.Id) && Key(p.Name.En) == Key(name)).ToList();
            return named.FirstOrDefault(p => p.CategoryId == categoryId) ?? (named.Count == 1 ? named[0] : null);
        }

        private static List<string> Differences(Product before, Product after, Category category)
        {
            var parts = new List<string>();
            if (before.Name.En != after.Name.En)
            {
                parts.Add($"renamed from {before.Name.En}");
            }

            if (before.CategoryId != after.CategoryId)
            {
                parts.Add($"moved to {category.Name.En}");
            }

            foreach (var v in after.Variants)
            {
                var old = before.Variants.FirstOrDefault(x => x.Id == v.Id);
                var label = after.Variants.Count > 1 ? $"{v.Name.En} " : string.Empty;
                if (old is null)
                {
                    parts.Add($"size {v.Name.En} added at {Pounds(v.Price.MinorUnits)}");
                }
                else
                {
                    if (old.Price != v.Price)
                    {
                        parts.Add($"{label}{Pounds(old.Price.MinorUnits)} → {Pounds(v.Price.MinorUnits)}");
                    }

                    if (old.Name.En != v.Name.En)
                    {
                        parts.Add($"size {old.Name.En} renamed {v.Name.En}");
                    }

                    if (old.IsSoldOut != v.IsSoldOut)
                    {
                        parts.Add($"{label}{(v.IsSoldOut ? "sold out" : "back in stock")}");
                    }
                }
            }

            foreach (var v in before.Variants.Where(v => after.Variants.All(x => x.Id != v.Id)))
            {
                parts.Add($"size {v.Name.En} removed");
            }

            if (!before.Variants.Select(v => v.Id).SequenceEqual(after.Variants.Select(v => v.Id)) && before.Variants.Count == after.Variants.Count
                && before.Variants.All(v => after.Variants.Any(x => x.Id == v.Id)))
            {
                parts.Add("sizes reordered");
            }

            if (before.VatCategory != after.VatCategory)
            {
                parts.Add($"VAT {VatName(before.VatCategory)} → {VatName(after.VatCategory)}");
            }

            if (before.Description?.En != after.Description?.En)
            {
                parts.Add("description changed");
            }

            if (!before.ModifierGroupIds.SequenceEqual(after.ModifierGroupIds))
            {
                parts.Add("extras changed");
            }

            if (before.IsSoldOut != after.IsSoldOut)
            {
                parts.Add(after.IsSoldOut ? "sold out" : "back in stock");
            }

            if (before.IsHidden && !after.IsHidden)
            {
                parts.Add("shown again");
            }

            return parts;
        }

        private static string Describe(Product p) =>
            $"{p.Name.En} ({string.Join(", ", p.Variants.Select(v => p.Variants.Count == 1 ? Pounds(v.Price.MinorUnits) : $"{v.Name.En} {Pounds(v.Price.MinorUnits)}"))}, {VatName(p.VatCategory)})";

        private static bool SameGroup(ModifierGroup a, ModifierGroup b) =>
            a.Name.En == b.Name.En && a.MinChoices == b.MinChoices && a.MaxChoices == b.MaxChoices && a.Options.SequenceEqual(b.Options);

        private void Change(string line) => _changes.Add(line);

        private bool CheckName(string sheet, int row, string text, string heading)
        {
            if (text.Length > LocalizedText.MaxLength)
            {
                Problem(sheet, row, $"{heading} is longer than {LocalizedText.MaxLength} characters.");
                return false;
            }

            return true;
        }

        private long? ParsePrice(string sheet, int row, string text)
        {
            var clean = text.Replace("£", string.Empty, StringComparison.Ordinal).Replace("GBP", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (!decimal.TryParse(clean, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var pounds))
            {
                Problem(sheet, row, $"Price \"{text}\" is not a price. Write it like 8.99.");
                return null;
            }

            var pence = pounds * 100m;
            var rounded = Math.Round(pence, MidpointRounding.AwayFromZero);
            if (Math.Abs(pence - rounded) > 0.0001m)
            {
                // Excel sometimes stores 14.85 as 14.850000000000001; real extra decimals are a mistake.
                Problem(sheet, row, $"Price \"{text}\" has more than 2 decimal places.");
                return null;
            }

            if (rounded < 0 || rounded > MaxPence)
            {
                Problem(sheet, row, $"Price \"{text}\" must be between 0 and {Pounds(MaxPence)}.");
                return null;
            }

            return (long)rounded;
        }

        // -1 = a problem was reported.
        private int? ParseCount(string sheet, int row, string text, string heading)
        {
            if (text.Length == 0)
            {
                return null;
            }

            if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) && n == Math.Floor(n) && n is >= 0 and <= 50)
            {
                return (int)n;
            }

            Problem(sheet, row, $"{heading} \"{text}\" should be a whole number from 0 to 50.");
            return -1;
        }

        private static VatCategory? ParseVat(string text) => new string([.. text.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit)]) switch
        {
            "hotfood" or "hot" or "h" => VatCategory.HotFood,
            "coldfood" or "cold" or "c" or "zero" or "z" => VatCategory.ColdFood,
            "standard" or "alwaysstandard" or "s" or "drink" or "drinks" or "20" => VatCategory.AlwaysStandard,
            _ => null,
        };

        private static bool? ParseYesNo(string text) => text.Trim().ToLowerInvariant() switch
        {
            "" or "no" or "n" or "false" or "0" or "-" => false,
            "yes" or "y" or "true" or "1" or "x" or "soldout" or "sold out" => true,
            _ => null,
        };
    }

    private sealed class SheetCategory(string name, int row)
    {
        public string Name { get; } = name;

        public int Row { get; } = row;

        public List<SheetItem> Items { get; } = [];

        public SheetItem Add(string itemName, int itemRow)
        {
            var item = new SheetItem(this, itemName, itemRow);
            Items.Add(item);
            return item;
        }
    }

    private sealed class SheetItem(SheetCategory category, string name, int row)
    {
        public SheetCategory Category { get; } = category;

        public string Name { get; } = name;

        public int Row { get; } = row;

        public List<SheetSize> Sizes { get; } = [];

        public VatCategory? Vat { get; set; }

        public string? Description { get; set; }

        public List<Guid> Extras { get; } = [];
    }

    private sealed record SheetSize(string Name, long Pence, bool SoldOut, int Row);

    /// <summary>Text for logs: a short summary of a preview.</summary>
    public static string Summary(ImportPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{preview.Items} items in {preview.Categories} categories: {preview.NewItems} new, {preview.ChangedItems} changed");
        if (preview.Hidden > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $", {preview.Hidden} hidden");
        }

        return text.ToString();
    }
}
