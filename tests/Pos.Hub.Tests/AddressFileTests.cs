using System.Net;
using Pos.Hub.Api;
using Pos.Hub.Delivery;

namespace Pos.Hub.Tests;

/// <summary>Royal Mail address files (PAF CSV) loaded into the hub, and the house pick list.</summary>
public sealed class AddressFileTests : HubTestBase
{
    // Made-up addresses in the same 14-column layout as the owner's files (no real Royal Mail data in the code).
    // organisation, department, PO box, building name, sub-building, number, dependent street, street, double locality, locality, town, postcode, type, suffix
    public const string Sample = """
        ,,,,,10,,Test Road,,,LEEDS,LS2 9JT,S,1A
        ,,,,,2,,Test Road,,,LEEDS,LS2 9JT,S,1B
        ,,,Example Tower,Flat 10,,,Test Road,,,LEEDS,LS2 9JT,S,1C
        ,,,Example Tower,Flat 2,,,Test Road,,,LEEDS,LS2 9JT,S,1D
        Sample Cafe,,,25-27,,,,Test Road,,,LEEDS,LS2 9JT,S,1E
        ,,,,,5,Mews Close,Test Road,,Little Village,LEEDS,LS2 9JT,S,1F
        ,,,Rose Cottage,,,,,,Little Village,LEEDS,LS2 9JT,S,1G
        Big Firm Ltd,,1234,,,,,,,,LEEDS,LS2 9JU,L,1A
        not,an,address
        ,,,,,1,,Other Street,,,LEEDS,LS7 3AA,S,1A
        """;

    internal override IReadOnlyDictionary<string, string> ExtraSettings => new Dictionary<string, string>
    {
        ["Delivery:AddressFolder"] = Path.Combine(Path.GetDirectoryName(DbPath)!, "addresses"),
    };

    private string Folder => Path.Combine(Path.GetDirectoryName(DbPath)!, "addresses");

    private static async Task<HttpResponseMessage> Upload(HttpClient client, string name, string content)
    {
        using var body = new StringContent(content, System.Text.Encoding.Latin1, "text/csv");
        return await client.PostAsync(new Uri($"/api/addresses/files?name={name}", UriKind.Relative), body);
    }

    [Fact]
    public void Addresses_are_laid_out_the_way_royal_mail_prints_them()
    {
        var (all, skipped) = PafCsv.Read(new StringReader(Sample));
        var lines = all.Select(a => a.ToDeliveryAddress()).Select(d => $"{d.Line1} | {d.Line2} | {d.Town} | {d.Postcode}").ToList();

        Assert.Equal(2, skipped); // the broken row, and the PO box with nowhere to deliver
        Assert.Contains("10 Test Road |  | Leeds | LS2 9JT", lines);
        Assert.Contains("Test Road | Flat 10, Example Tower | Leeds | LS2 9JT", lines);
        Assert.Contains("25-27 Test Road | Sample Cafe | Leeds | LS2 9JT", lines);  // number-like building name goes with the street; the business is kept
        Assert.Contains("5 Mews Close | Test Road, Little Village | Leeds | LS2 9JT", lines);
        Assert.Contains("Rose Cottage | Little Village | Leeds | LS2 9JT", lines);     // no number or street
        Assert.Equal("Sample Cafe, 25-27 Test Road", all.Single(a => a.Organisation == "Sample Cafe").Label());
    }

    [Fact]
    public async Task Upload_then_pick_from_the_postcode_in_house_number_order()
    {
        var response = await Upload(Owner, "LS2.csv", Sample);
        var source = await response.Read<AddressSource>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(8, source.Addresses); // 7 in LS2 9JT, 1 in LS7 3AA; the PO box is skipped
        Assert.Equal(2, source.Postcodes);

        var houses = await Cashier.GetJson<List<AddressOption>>("/api/addresses?postcode=ls29jt");
        Assert.Equal(
            ["Rose Cottage, Little Village", "5 Mews Close, Test Road, Little Village", "2 Test Road", "10 Test Road", "Sample Cafe, 25-27 Test Road",
             "Flat 2, Example Tower, Test Road", "Flat 10, Example Tower, Test Road"],
            houses.Select(h => h.Label));
    }

    [Fact]
    public async Task Reloading_a_file_replaces_it_and_bad_files_change_nothing()
    {
        await Upload(Owner, "LS2.csv", Sample);
        await Upload(Owner, "LS2.csv", Sample);
        var bad = await Upload(Owner, "LS2.csv", "this,is,not,an,address,file");

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(7, (await Cashier.GetJson<List<AddressOption>>("/api/addresses?postcode=LS2 9JT")).Count); // not doubled, not wiped
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(Manager, "LS2.csv", Sample)).StatusCode); // owner only
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(Owner, "LS2.txt", Sample)).StatusCode);
    }

    [Fact]
    public async Task Files_in_the_address_folder_load_when_the_hub_starts()
    {
        Directory.CreateDirectory(Folder);
        await File.WriteAllTextAsync(Path.Combine(Folder, "LS7.csv"), ",,,,,1,,Other Street,,,LEEDS,LS7 3AA,S,1A\n");

        Restart();
        for (var i = 0; i < 50 && (await Owner.GetJson<List<AddressSource>>("/api/addresses/files")).Count == 0; i++)
        {
            await Task.Delay(100);
        }

        var files = await Owner.GetJson<List<AddressSource>>("/api/addresses/files");
        Assert.Equal("LS7.csv", Assert.Single(files).Name);
        Assert.Equal("1 Other Street", Assert.Single(await Cashier.GetJson<List<AddressOption>>("/api/addresses?postcode=LS7 3AA")).Address.Line1);
        var reloaded = await (await Owner.PostAsync(new Uri("/api/addresses/files/reload", UriKind.Relative), null)).Read<List<AddressSource>>();
        Assert.Empty(reloaded); // unchanged files are not loaded again
    }

    [Fact]
    public async Task Postcode_check_lists_the_houses_and_still_works_without_internet()
    {
        await Upload(Owner, "LS2.csv", Sample);

        var online = await Cashier.GetJson<PostcodeCheck>("/api/delivery/check?postcode=LS2 9JT");
        Assert.Equal(1.3m, online.DistanceMiles);
        Assert.Equal(7, online.Addresses.Count);

        Host.Postcodes.Down = true;
        var offline = await Cashier.GetJson<PostcodeCheck>("/api/delivery/check?postcode=LS7 3AA"); // never looked up before
        Assert.Null(offline.DistanceMiles);
        Assert.Contains("Type the distance", offline.Problem, StringComparison.Ordinal);
        Assert.Equal("1 Other Street", Assert.Single(offline.Addresses).Label);
        Assert.Empty(await Cashier.GetJson<List<AddressOption>>("/api/addresses?postcode=LS2 9JU")); // PO box only
    }
}
