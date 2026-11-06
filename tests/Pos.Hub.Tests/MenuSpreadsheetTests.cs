using System.IO.Compression;
using System.Net;
using System.Text;
using Pos.Domain.Common;
using Pos.Domain.Menus;
using Pos.Domain.Tax;
using Pos.Hub.MenuSheets;

namespace Pos.Hub.Tests;

/// <summary>Reading Excel and CSV files: the cells come out exactly as the owner typed them.</summary>
public sealed class SpreadsheetFileTests
{
    [Fact]
    public void Csv_handles_quotes_commas_inch_marks_and_line_breaks()
    {
        const string csv = "Category,Item,Size,Price\r\nPizza,\"Margherita, large\",\"16\"\"\",16.00\r\n,\"Two\nlines\",,\r\n\r\nDrinks,Can,,£1.20\r\n";
        var sheet = Spreadsheet.ReadCsv(csv, "menu");

        Assert.Equal(5, sheet.Rows.Count); // the blank row is kept, so row numbers match Excel
        Assert.Equal(["Pizza", "Margherita, large", "16\"", "16.00"], sheet.Rows[1]);
        Assert.Equal("Two\nlines", sheet.Rows[2][1]);
        Assert.All(sheet.Rows[3], c => Assert.Equal(string.Empty, c));
        Assert.Equal("£1.20", sheet.Rows[4][3]);
    }

    [Fact]
    public void Csv_from_excel_on_a_uk_pc_keeps_the_pound_sign_either_way()
    {
        var utf8WithMark = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Item,Price\nCan,£1.20\n")).ToArray();
        var windows = Encoding.Latin1.GetBytes("Item,Price\nCan,£1.20\n"); // plain "CSV (Comma delimited)"
        var semicolons = Encoding.UTF8.GetBytes("Item;Price\nCan;1.20\n");

        Assert.Equal("£1.20", Spreadsheet.Read(utf8WithMark, "a.csv")[0].Rows[1][1]);
        Assert.Equal("Item", Spreadsheet.Read(utf8WithMark, "a.csv")[0].Rows[0][0]);
        Assert.Equal("£1.20", Spreadsheet.Read(windows, "a.csv")[0].Rows[1][1]);
        Assert.Equal("1.20", Spreadsheet.Read(semicolons, "a.csv")[0].Rows[1][1]);
    }

    [Fact]
    public void Written_workbook_reads_back_the_same()
    {
        var sheets = new List<SheetData>
        {
            new("Menu", [["Category", "Item", "Price"], ["Pizza", "16\" Margherita & <cheese>", "16.00"], [], ["Drinks", "", "1.20"]]),
            new("Extras", [["Group", "Extra"], ["Toppings", "Jalapeños"]]),
        };
        var read = Spreadsheet.Read(Spreadsheet.WriteXlsx(sheets), "menu.xlsx");

        Assert.Equal(["Menu", "Extras"], read.Select(s => s.Name));
        Assert.Equal("16\" Margherita & <cheese>", read[0].Rows[1][1]);
        Assert.Empty(read[0].Rows[2]);
        Assert.Equal(["Drinks", "", "1.20"], read[0].Rows[3]);
        Assert.Equal("Jalapeños", read[1].Rows[1][1]);
    }

    [Fact]
    public void Workbook_saved_by_excel_with_shared_text_numbers_and_gaps_is_read()
    {
        // Laid out the way Excel saves: shared strings, number cells, a skipped row and a skipped column.
        var file = Zip(
            ("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Menu" sheetId="1" r:id="rId7"/></sheets></workbook>"""),
            ("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId7" Type="worksheet" Target="worksheets/data.xml"/></Relationships>"""),
            ("xl/sharedStrings.xml", """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Item</t></si><si><t>Price</t></si><si><r><t>Chicken </t></r><r><rPr><b/></rPr><t>Wings</t></r></si></sst>"""),
            ("xl/worksheets/data.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="C1" t="s"><v>1</v></c></row><row r="3"><c r="A3" t="s"><v>2</v></c><c r="C3"><v>14.850000000000001</v></c></row></sheetData></worksheet>"""));

        var sheet = Assert.Single(Spreadsheet.Read(file, "Menu.XLSX"));
        Assert.Equal(["Item", "", "Price"], sheet.Rows[0]);
        Assert.Empty(sheet.Rows[1]);
        Assert.Equal(["Chicken Wings", "", "14.850000000000001"], sheet.Rows[2]);
    }

    [Theory]
    [InlineData("menu.xls", "Old .xls")]
    [InlineData("menu.pdf", "Choose an Excel")]
    [InlineData("menu.xlsx", "not a real .xlsx")]
    public void Wrong_kinds_of_file_are_refused_with_what_to_do(string name, string message)
    {
        var ex = Assert.Throws<ArgumentException>(() => Spreadsheet.Read(Encoding.UTF8.GetBytes("Item,Price"), name));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_that_are_too_big_or_unzip_too_big_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Spreadsheet.Read(new byte[Spreadsheet.MaxBytes + 1], "menu.csv"));

        // A tiny zip that would unpack to 40 MB.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = zip.CreateEntry("xl/workbook.xml", CompressionLevel.SmallestSize).Open();
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 40; i++)
            {
                s.Write(zeros);
            }
        }

        Assert.True(buffer.Length < Spreadsheet.MaxBytes);
        Assert.Throws<ArgumentException>(() => Spreadsheet.Read(buffer.ToArray(), "bomb.xlsx"));
    }

    [Fact]
    public void A_workbook_with_a_dtd_is_refused()
    {
        var file = Zip(("xl/workbook.xml", """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY a "aaaa">]><workbook><sheets/></workbook>"""));
        Assert.Throws<ArgumentException>(() => Spreadsheet.Read(file, "menu.xlsx"));
    }

    internal static byte[] Zip(params (string Name, string Text)[] parts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in parts)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return buffer.ToArray();
    }
}

/// <summary>Turning sheet rows into menu changes: sizes, matching by name, problems by row, hiding, extras.</summary>
public sealed class MenuSheetImportTests
{
    private static readonly Guid User = Guid.NewGuid();

    private static SheetData Menu(params string[] csvLines) => Spreadsheet.ReadCsv(string.Join("\n", csvLines), "Menu");

    private static ImportPlan Plan(MenuState current, params SheetData[] sheets) => MenuSheetImport.Plan(current, sheets, MissingItems.Keep, User);

    private static MenuState Empty() => new("GBP", 0, [], [], [], []);

    [Fact]
    public void Rows_with_the_same_item_become_one_button_with_sizes()
    {
        var plan = Plan(Empty(), Menu(
            "Category,Item,Size,Price,VAT,Description",
            "Pizza,Margherita,\"16\"\"\",£16.00,Hot food,Tomato & cheese",
            "Pizza,Margherita,\"9\"\"\",8.55,Hot food,",
            ",,12\",12,,",            // empty category and item: another size of the item above
            "Drinks,Can of Pepsi,,1.75,Standard,",
            "Sides,Coleslaw,,1.10,cold,"));

        Assert.Empty(plan.Preview.Problems);
        var state = plan.State!;
        Assert.Equal(["Pizza", "Drinks", "Sides"], state.Categories.Select(c => c.Name.En));
        var pizza = state.Products.Single(p => p.Name.En == "Margherita");
        Assert.Equal(["16\"", "9\"", "12\""], pizza.Variants.Select(v => v.Name.En));
        Assert.Equal([1600L, 855, 1200], pizza.Variants.Select(v => v.Price.MinorUnits));
        Assert.Equal("Tomato & cheese", pizza.Description!.En);
        Assert.Equal("Regular", state.Products.Single(p => p.Name.En == "Can of Pepsi").Variants.Single().Name.En);
        Assert.Equal(VatCategory.AlwaysStandard, state.Products.Single(p => p.Name.En == "Can of Pepsi").VatCategory);
        Assert.Equal(VatCategory.ColdFood, state.Products.Single(p => p.Name.En == "Coleslaw").VatCategory);
        Assert.Equal((3, 3, 3), (plan.Preview.NewCategories, plan.Preview.NewItems, plan.Preview.Items));
        Assert.True(plan.Preview.CanImport);
    }

    [Fact]
    public void Every_problem_is_listed_with_its_row_and_nothing_is_built()
    {
        var plan = Plan(Empty(), Menu(
            "Category,Item,Size,Price,VAT,Extras,Sold out",
            "Pizza,Margherita,12\",8.999,Hot food,,",     // row 2: three decimals
            "Pizza,Hawaiian,,eight,Hot food,,",            // row 3: not a price
            "Pizza,Veggie,,8.00,Hot,Toppings,",            // row 4: no such extras group
            "Pizza,Veggie,,8.50,Hot,,",                    // row 5: listed twice
            "Drinks,Can,,1.20,,,",                         // row 6: VAT empty for a new item
            "Drinks,Water,,1.00,Zero-rated drink,,",       // row 7: unknown VAT
            "Drinks,Juice,,1.00,Standard,,maybe",          // row 8: sold out not yes or no
            "Drinks,Tea,,,Standard,,",                     // row 9: no price
            "Pizza,Calzone,Large,-1,Hot food,,"));         // row 10: negative

        Assert.Null(plan.State);
        Assert.False(plan.Preview.CanImport);
        var rows = plan.Preview.Problems.Select(p => p.Row).ToList();
        Assert.Equal([2, 3, 4, 5, 6, 7, 8, 9, 10], rows.Distinct());
        Assert.Contains(plan.Preview.Problems, p => p.Row == 2 && p.Message.Contains("2 decimal places", StringComparison.Ordinal));
        Assert.Contains(plan.Preview.Problems, p => p.Row == 4 && p.Message.Contains("\"Toppings\" is not on the Extras sheet", StringComparison.Ordinal));
        Assert.Contains(plan.Preview.Problems, p => p.Row == 5 && p.Message.Contains("listed twice (rows 4 and 5)", StringComparison.Ordinal));
        Assert.Contains(plan.Preview.Problems, p => p.Row == 6 && p.Message.Contains("VAT is empty", StringComparison.Ordinal));
        Assert.Contains(plan.Preview.Problems, p => p.Row == 7 && p.Message.Contains("Hot food, Cold food or Standard", StringComparison.Ordinal));
    }

    [Fact]
    public void Excel_rounding_in_a_price_is_not_a_problem()
    {
        var plan = Plan(Empty(), Menu("Category,Item,Price,VAT", "Pizza,Hawaiian,14.850000000000001,Hot food"));
        Assert.Equal(1485, plan.State!.Products.Single().Variants.Single().Price.MinorUnits);
    }

    [Fact]
    public void Headings_are_needed_and_can_be_written_loosely()
    {
        Assert.Contains("missing: Price", Plan(Empty(), Menu("Category,Item,Cost", "Pizza,Hawaiian,8")).Preview.Problems.Single().Message, StringComparison.Ordinal);

        var loose = Plan(Empty(), Menu("CATEGORY,Item name,Price (£),VAT type,Sold out?", "Pizza,Hawaiian,8,hot food,yes"));
        Assert.Empty(loose.Preview.Problems);
        Assert.True(loose.State!.Products.Single().IsSoldOut);
    }

    [Fact]
    public void Existing_items_are_found_by_name_and_keep_their_ids()
    {
        var current = TestMenu.State();
        var plan = Plan(current, Menu(
            "Category,Item,Size,Price,VAT",
            "Pizzas,Margherita,12\",12.49,Hot food",     // price up
            "pizzas,margherita,14\",14.49,Hot food",     // a new size; names in other letter case still match
            "Kebabs,Doner kebab,,8.99,Hot food",         // the same as now
            "Drinks,Can,,1.20,Cold food"));              // VAT changed

        Assert.Empty(plan.Preview.Problems);
        var state = plan.State!;
        var pizza = state.Products.Single(p => p.Id == TestMenu.Margherita);
        Assert.Equal("Margherita", pizza.Name.En);
        Assert.Equal(TestMenu.Margherita12, pizza.Variants[0].Id);
        Assert.Equal(1249, pizza.Variants[0].Price.MinorUnits);
        Assert.Equal("14\"", pizza.Variants[1].Name.En);
        Assert.Equal(TestMenu.DonerRegular, state.Products.Single(p => p.Id == TestMenu.Doner).Variants.Single().Id);
        Assert.Equal([TestMenu.Sauce], state.Products.Single(p => p.Id == TestMenu.Doner).ModifierGroupIds); // no Extras column: kept
        Assert.Equal(TestMenu.Pizzas, state.Categories.Single(c => c.Name.En == "Pizzas").Id);
        Assert.Equal("بيتزا", state.Categories.Single(c => c.Id == TestMenu.Pizzas).Name.Ar);           // Arabic names kept
        Assert.Equal((0, 0, 2, 1), (plan.Preview.NewCategories, plan.Preview.NewItems, plan.Preview.ChangedItems, plan.Preview.UnchangedItems));
        Assert.Contains("Pizzas › Margherita: 12\" £11.99 → £12.49, size 14\" added at £14.49", plan.Preview.Changes);
        Assert.Contains("Drinks › Can: VAT Standard → Cold food", plan.Preview.Changes);

        // The meal deal still points at the same categories and is untouched.
        Assert.Equal(current.Products.Single(p => p.Id == TestMenu.Deal), state.Products.Single(p => p.Id == TestMenu.Deal));
    }

    [Fact]
    public void Importing_the_downloaded_spreadsheet_changes_nothing()
    {
        var current = TestMenu.State();
        var sheets = Spreadsheet.Read(MenuSheetImport.Workbook(current), "menu.xlsx");
        var plan = Plan(current, [.. sheets]);

        Assert.Empty(plan.Preview.Problems);
        Assert.Empty(plan.Preview.Changes);
        Assert.False(plan.Preview.CanImport);
        Assert.Equal(3, plan.Preview.UnchangedItems);
        Assert.Equal(["Menu", "Extras", "How to"], sheets.Select(s => s.Name));
        Assert.DoesNotContain(sheets[0].Rows, r => r.Contains("Pizza & Kebab Deal")); // meal deals stay in Edit menu
    }

    [Fact]
    public void An_item_moved_to_another_category_keeps_its_id()
    {
        var plan = Plan(TestMenu.State(), Menu("Category,Item,Price,VAT", "Soft drinks,Can,1.20,Standard"));
        var can = plan.State!.Products.Single(p => p.Id == TestMenu.Can);
        Assert.Equal("Soft drinks", plan.State.Categories.Single(c => c.Id == can.CategoryId).Name.En);
        Assert.Contains("Soft drinks › Can: moved to Soft drinks", plan.Preview.Changes);
    }

    [Fact]
    public void Items_not_in_the_sheet_are_kept_or_hidden_but_never_deleted()
    {
        var sheet = Menu("Category,Item,Size,Price,VAT", "Pizzas,Margherita,12\",11.99,Hot food");

        var keep = MenuSheetImport.Plan(TestMenu.State(), [sheet], MissingItems.Keep, User);
        Assert.Equal(2, keep.Preview.NotInSheet); // the doner and the can; the meal deal is not counted
        Assert.False(keep.Preview.CanImport);     // nothing would change

        var hide = MenuSheetImport.Plan(TestMenu.State(), [sheet], MissingItems.Hide, User);
        Assert.Equal(2, hide.Preview.Hidden);
        var state = hide.State!;
        Assert.Equal(4, state.Products.Count); // nothing removed
        Assert.True(state.Products.Single(p => p.Id == TestMenu.Doner).IsHidden);
        Assert.True(state.Categories.Single(c => c.Id == TestMenu.Kebabs).IsHidden); // nothing left to show
        Assert.False(state.Products.Single(p => p.Id == TestMenu.Deal).IsHidden);
        Assert.False(state.Categories.Single(c => c.Id == TestMenu.Deals).IsHidden);
    }

    [Fact]
    public void A_meal_deal_cannot_be_changed_from_the_sheet()
    {
        var plan = Plan(TestMenu.State(), Menu("Category,Item,Price,VAT", "Meal deals,Pizza & Kebab Deal,15,Hot food"));
        Assert.Contains("is a meal deal", plan.Preview.Problems.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extras_sheet_makes_groups_and_items_use_them()
    {
        var extras = Spreadsheet.ReadCsv(string.Join("\n",
            "Group,Extra,Price,Choose at least,Choose at most",
            "Extra toppings,Cheese,1.00,0,3",
            ",Mushrooms,0.80,,",
            "Sauce,Garlic,0,1,1",
            "Sauce,Chilli,,,"), "Extras");
        var menu = Menu("Category,Item,Size,Price,VAT,Extras", "Pizzas,Margherita,12\",11.99,Hot food,Extra toppings; sauce");

        var plan = Plan(TestMenu.State(), menu, extras);
        Assert.Empty(plan.Preview.Problems);
        var state = plan.State!;
        var toppings = state.ModifierGroups.Single(g => g.Name.En == "Extra toppings");
        Assert.Equal((0, 3), (toppings.MinChoices, toppings.MaxChoices));
        Assert.Equal([100L, 80], toppings.Options.Select(o => o.Price.MinorUnits));
        var sauce = state.ModifierGroups.Single(g => g.Id == TestMenu.Sauce);     // existing group found by name
        Assert.Equal(TestMenu.Garlic, sauce.Options[0].Id);                        // existing extra keeps its id
        Assert.Equal(["Garlic", "Chilli"], sauce.Options.Select(o => o.Name.En));
        Assert.Equal([toppings.Id, TestMenu.Sauce], state.Products.Single(p => p.Id == TestMenu.Margherita).ModifierGroupIds);
        Assert.Equal((2, 1), (plan.Preview.ExtrasGroups, plan.Preview.NewExtrasGroups));
    }

    [Fact]
    public void Sold_out_sizes_and_items()
    {
        var plan = Plan(Empty(), Menu(
            "Category,Item,Size,Price,VAT,Sold out",
            "Pizza,Margherita,12\",9,Hot food,yes",
            "Pizza,Margherita,9\",7,Hot food,",
            "Pizza,Hawaiian,,9,Hot food,Yes"));

        var margherita = plan.State!.Products.Single(p => p.Name.En == "Margherita");
        Assert.False(margherita.IsSoldOut);
        Assert.Equal([true, false], margherita.Variants.Select(v => v.IsSoldOut));
        var hawaiian = plan.State.Products.Single(p => p.Name.En == "Hawaiian");
        Assert.True(hawaiian.IsSoldOut);
        Assert.False(hawaiian.Variants.Single().IsSoldOut);
    }

    [Fact]
    public void A_big_menu_imports_in_sheet_order()
    {
        var lines = new List<string> { "Category,Item,Size,Price,VAT" };
        for (var c = 0; c < 20; c++)
        {
            for (var i = 0; i < 30; i++)
            {
                lines.Add($"Category {c},Item {i},Small,{c}.{i:00},Hot food");
                lines.Add($",,Large,{c + 1}.{i:00},");
            }
        }

        var plan = Plan(Empty(), Menu([.. lines]));
        Assert.Empty(plan.Preview.Problems);
        Assert.Equal(600, plan.State!.Products.Count);
        Assert.Equal("Category 19", plan.State.Categories[^1].Name.En);
        Assert.Equal(Enumerable.Range(0, 30), plan.State.Products.Where(p => p.Name.En.StartsWith("Item", StringComparison.Ordinal) && plan.State.Categories.Single(c => c.Id == p.CategoryId).Name.En == "Category 3").Select(p => p.SortOrder));
    }
}

/// <summary>The menu spreadsheet through the hub: preview, import with a backup, undo, and who may do it.</summary>
public sealed class MenuSpreadsheetHubTests : HubTestBase
{
    private static async Task<HttpResponseMessage> Send(HttpClient client, string action, string name, byte[] file, string missing = "keep")
    {
        using var body = new ByteArrayContent(file);
        body.Headers.ContentType = new("application/octet-stream");
        return await client.PostAsync(new Uri($"/api/menu/spreadsheet/{action}?name={Uri.EscapeDataString(name)}&missing={missing}", UriKind.Relative), body);
    }

    private static byte[] Csv(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\r\n", lines));

    private static readonly byte[] PriceRise = Csv(
        "Category,Item,Size,Price,VAT",
        "Pizzas,Margherita,12\",12.49,Hot food",
        "Sides,Chips,,2.50,Hot food");

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        (await Owner.Put("/api/menu", TestMenu.State())).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Preview_changes_nothing_then_import_backs_up_and_updates_the_tills()
    {
        var before = await Owner.GetJson<MenuState>("/api/menu");

        var preview = await (await Send(Manager, "preview", "menu.csv", PriceRise)).Read<ImportPreview>();
        Assert.Equal((1, 1, 1), (preview.NewCategories, preview.NewItems, preview.ChangedItems));
        Assert.Equal(before.Version, (await Owner.GetJson<MenuState>("/api/menu")).Version);

        var response = await Send(Manager, "import", "menu.csv", PriceRise);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await Owner.GetJson<MenuState>("/api/menu");
        Assert.True(after.Version > before.Version);
        Assert.Equal(1249, after.Products.Single(p => p.Id == TestMenu.Margherita).Variants.Single().Price.MinorUnits);
        Assert.Contains(after.Products, p => p.Name.En == "Chips");
        Assert.NotEmpty(Directory.GetFileSystemEntries(Host.BackupFolder)); // a backup was made first

        var last = await Manager.GetJson<LastImport>("/api/menu/spreadsheet/last");
        Assert.Equal("menu.csv", last.FileName);
        Assert.True(last.CanUndo);

        // The same file again: nothing to change.
        var again = await Send(Manager, "import", "menu.csv", PriceRise);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Undo_puts_the_menu_back_exactly()
    {
        var before = await Owner.GetJson<MenuState>("/api/menu");
        (await Send(Owner, "import", "menu.csv", PriceRise)).EnsureSuccessStatusCode();

        (await Owner.Post("/api/menu/spreadsheet/undo", new { })).EnsureSuccessStatusCode();
        var undone = await Owner.GetJson<MenuState>("/api/menu");
        Assert.Equal(PosJson.Serialize(before with { Version = 0 }), PosJson.Serialize(undone with { Version = 0 }));
        Assert.Equal(HttpStatusCode.NoContent, (await Owner.GetAsync(new Uri("/api/menu/spreadsheet/last", UriKind.Relative))).StatusCode);

        // Survives a restart of the hub.
        Restart();
        Assert.Equal(PosJson.Serialize(before with { Version = 0 }), PosJson.Serialize((await Owner.GetJson<MenuState>("/api/menu")) with { Version = 0 }));
    }

    [Fact]
    public async Task Undo_is_refused_once_the_menu_has_been_changed_since()
    {
        (await Send(Owner, "import", "menu.csv", PriceRise)).EnsureSuccessStatusCode();
        (await Owner.Post($"/api/menu/products/{TestMenu.Can}/sold-out", new Api.SoldOutRequest(true))).EnsureSuccessStatusCode();

        Assert.False((await Owner.GetJson<LastImport>("/api/menu/spreadsheet/last")).CanUndo);
        var response = await Owner.Post("/api/menu/spreadsheet/undo", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True((await Owner.GetJson<MenuState>("/api/menu")).Products.Single(p => p.Id == TestMenu.Can).IsSoldOut);
    }

    [Fact]
    public async Task A_sheet_with_problems_is_never_imported()
    {
        var bad = Csv("Category,Item,Price,VAT", "Pizzas,Margherita,12.49,Hot food", "Sides,Chips,two pounds,Hot food");
        var before = await Owner.GetJson<MenuState>("/api/menu");

        var preview = await (await Send(Owner, "preview", "menu.csv", bad)).Read<ImportPreview>();
        Assert.Equal(3, preview.Problems.Single().Row);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(Owner, "import", "menu.csv", bad)).StatusCode);
        Assert.Equal(before.Version, (await Owner.GetJson<MenuState>("/api/menu")).Version);
    }

    [Fact]
    public async Task Hiding_what_is_not_in_the_sheet_hides_but_keeps()
    {
        (await Send(Owner, "import", "menu.csv", PriceRise, missing: "hide")).EnsureSuccessStatusCode();
        var menu = await Owner.GetJson<MenuState>("/api/menu");
        Assert.Equal(5, menu.Products.Count);
        Assert.True(menu.Products.Single(p => p.Id == TestMenu.Doner).IsHidden);
        Assert.False(menu.Products.Single(p => p.Id == TestMenu.Margherita).IsHidden);
    }

    [Fact]
    public async Task Download_is_a_workbook_that_imports_back_unchanged()
    {
        var response = await Owner.GetAsync(new Uri("/api/menu/spreadsheet", UriKind.Relative));
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType!.MediaType);
        var file = await response.Content.ReadAsByteArrayAsync();

        var preview = await (await Send(Owner, "preview", "menu.xlsx", file)).Read<ImportPreview>();
        Assert.Empty(preview.Problems);
        Assert.Empty(preview.Changes);
    }

    [Fact]
    public async Task Only_staff_who_edit_the_menu_can_use_it()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.GetAsync(new Uri("/api/menu/spreadsheet", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Cashier, "preview", "menu.csv", PriceRise)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Kitchen, "import", "menu.csv", PriceRise)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Cashier.Post("/api/menu/spreadsheet/undo", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(Anonymous, "import", "menu.csv", PriceRise)).StatusCode);
    }

    [Fact]
    public async Task Wrong_files_get_a_clear_answer()
    {
        var response = await Send(Owner, "preview", "menu.xls", [1, 2, 3]);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Excel Workbook (.xlsx)", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Owner, "preview", "menu.csv", new byte[Spreadsheet.MaxBytes + 10])).StatusCode);
    }

    [Fact]
    public async Task New_items_get_their_price_and_vat()
    {
        (await Send(Owner, "import", "menu.csv", PriceRise)).EnsureSuccessStatusCode();
        var menu = await Owner.GetJson<MenuState>("/api/menu");
        var chips = menu.Products.Single(p => p.Name.En == "Chips");
        Assert.Equal(VatCategory.HotFood, chips.VatCategory);
        Assert.Equal(Money.Gbp(250), chips.Variants.Single().Price);
    }
}
