using System.Globalization;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using Pos.Domain.Orders;
using Pos.Hub.Data;

namespace Pos.Hub.Delivery;

/// <summary>
/// One address from a Royal Mail Postcode Address File (PAF) CSV, in the column order of the owner's files:
/// organisation, department, PO box, building name, sub-building, building number, dependent thoroughfare,
/// thoroughfare, double dependent locality, dependent locality, post town, postcode, postcode type, delivery point suffix.
/// </summary>
public sealed record PafAddress(
    string? Organisation,
    string? Department,
    string? PoBox,
    string? BuildingName,
    string? SubBuilding,
    string? BuildingNumber,
    string? DependentThoroughfare,
    string? Thoroughfare,
    string? DoubleDependentLocality,
    string? DependentLocality,
    string PostTown,
    string Postcode)
{
    /// <summary>
    /// The address as the till's three lines: house number and street; flat, building, business and area; town.
    /// Building "names" that are really numbers (e.g. "25-27", "11a") go with the street, as Royal Mail prints them.
    /// </summary>
    public DeliveryAddress ToDeliveryAddress()
    {
        var numberLike = BuildingName is { } n && n.Length > 0 && char.IsAsciiDigit(n[0]) && n.Length <= 8 && !n.Contains(' ', StringComparison.Ordinal);
        var number = BuildingNumber ?? (numberLike ? BuildingName : null);
        var street = DependentThoroughfare ?? Thoroughfare;
        var line1 = string.Join(' ', new[] { number, street }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var rest = new List<string?> { Organisation, SubBuilding, numberLike ? null : BuildingName, DependentThoroughfare is null ? null : Thoroughfare, DoubleDependentLocality, DependentLocality };
        if (string.IsNullOrWhiteSpace(line1))
        {
            // No number or street (e.g. a named farm): the building name leads.
            line1 = SubBuilding ?? BuildingName ?? Organisation ?? string.Empty;
            rest.Remove(line1);
        }

        var line2 = string.Join(", ", rest.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase));
        return new DeliveryAddress(line1, line2.Length == 0 ? null : line2, Title(PostTown), Postcode);
    }

    /// <summary>One line for the pick list, the way people say it: "Flat 10, Example Tower, Test Road", "Sample Cafe, 25-27 Test Road".</summary>
    public string Label()
    {
        var numberLike = BuildingName is { } n && n.Length > 0 && char.IsAsciiDigit(n[0]) && n.Length <= 8 && !n.Contains(' ', StringComparison.Ordinal);
        var number = BuildingNumber ?? (numberLike ? BuildingName : null);
        var street = string.Join(' ', new[] { number, DependentThoroughfare ?? Thoroughfare }.Where(p => !string.IsNullOrWhiteSpace(p)));
        return string.Join(", ", new[] { Organisation, SubBuilding, numberLike ? null : BuildingName, street, DependentThoroughfare is null ? null : Thoroughfare, DoubleDependentLocality, DependentLocality }
            .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Sort key: street, then house number in number order, then flat in number order.</summary>
    public string SortKey()
    {
        static string Natural(string? s) => string.IsNullOrEmpty(s)
            ? string.Empty
            : System.Text.RegularExpressions.Regex.Replace(s.ToUpperInvariant(), @"\d+", m => m.Value.PadLeft(6, '0'));
        // Joined with a character below every letter and digit, so a missing street sorts first.
        const char Gap = '\u0001';
        return string.Join(Gap, Natural(DependentThoroughfare ?? Thoroughfare), Natural(BuildingNumber ?? BuildingName), Natural(SubBuilding), Natural(Organisation));
    }

    /// <summary>"BIRMINGHAM" → "Birmingham", "STOKE-ON-TRENT" → "Stoke-On-Trent".</summary>
    private static string Title(string town) => CultureInfo.GetCultureInfo("en-GB").TextInfo.ToTitleCase(town.ToLowerInvariant());
}

/// <summary>Reads Royal Mail PAF CSV files.</summary>
public static class PafCsv
{
    /// <summary>Number of columns in each row.</summary>
    public const int Columns = 14;

    /// <summary>Reads every deliverable address; rows that are not addresses (wrong shape, bad postcode, PO boxes only) are counted as skipped.</summary>
    public static (List<PafAddress> Addresses, int Skipped) Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        using var parser = new TextFieldParser(reader) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
        parser.SetDelimiters(",");
        var addresses = new List<PafAddress>();
        var skipped = 0;
        while (!parser.EndOfData)
        {
            string[]? f;
            try
            {
                f = parser.ReadFields();
            }
            catch (MalformedLineException)
            {
                skipped++;
                continue;
            }

            if (f is null || f.Length < Columns || !UkPostcode.TryNormalise(f[11], out var postcode) || string.IsNullOrWhiteSpace(f[10]))
            {
                skipped++; // e.g. a header line or a broken row
                continue;
            }

            string? V(int i) => string.IsNullOrWhiteSpace(f[i]) ? null : f[i].Trim();
            var address = new PafAddress(V(0), V(1), V(2), V(3), V(4), V(5), V(6), V(7), V(8), V(9), f[10].Trim(), postcode);
            if (address.BuildingNumber is null && address.BuildingName is null && address.SubBuilding is null && address.Thoroughfare is null)
            {
                skipped++; // PO box only (even a business's): nowhere to deliver
                continue;
            }

            addresses.Add(address);
        }

        return (addresses, skipped);
    }
}

/// <summary>An address offered in the pick list on the till.</summary>
/// <param name="Label">One line, e.g. "Flat 1, Studley Tower, Canford Close".</param>
/// <param name="Address">The address to use.</param>
public sealed record AddressOption(string Label, DeliveryAddress Address);

/// <summary>An address file loaded into the hub.</summary>
/// <param name="Name">File name.</param>
/// <param name="Addresses">Addresses loaded.</param>
/// <param name="Postcodes">Postcodes covered.</param>
/// <param name="Skipped">Rows that were not addresses.</param>
/// <param name="ImportedAt">When.</param>
public sealed record AddressSource(string Name, int Addresses, int Postcodes, int Skipped, DateTimeOffset ImportedAt);

/// <summary>
/// The shop's own address list, loaded from Royal Mail PAF CSV files (one per postcode district, e.g. B12.csv).
/// Kept in the hub database, so looking up a postcode's houses needs no internet and costs nothing per search.
/// </summary>
public sealed class AddressBook(HubDatabase db, DeliverySettings settings, TimeProvider clock, ILogger<AddressBook> log)
{
    /// <summary>The addresses in a postcode, in street and house-number order.</summary>
    public IReadOnlyList<AddressOption> Find(string postcode)
    {
        if (!UkPostcode.TryNormalise(postcode, out var normalised))
        {
            return [];
        }

        return db.Run(() =>
        {
            using var command = db.Command("SELECT label, line1, line2, town, postcode FROM addresses WHERE postcode = $p ORDER BY sort_key;", ("$p", normalised));
            using var reader = command.ExecuteReader();
            var found = new List<AddressOption>();
            while (reader.Read())
            {
                found.Add(new AddressOption(reader.GetString(0), new DeliveryAddress(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4))));
            }

            return found;
        });
    }

    /// <summary>Files loaded so far.</summary>
    public IReadOnlyList<AddressSource> Sources() => db.Run(() =>
    {
        using var command = db.Command("SELECT name, addresses, postcodes, skipped, imported_at FROM address_sources ORDER BY name;");
        using var reader = command.ExecuteReader();
        var list = new List<AddressSource>();
        while (reader.Read())
        {
            list.Add(new AddressSource(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        }

        return list;
    });

    /// <summary>
    /// Loads (or reloads) one file. Its old addresses are replaced in one transaction, so a failed load leaves the
    /// previous list in place. Refuses a file with no addresses in it.
    /// </summary>
    public AddressSource Import(string name, TextReader content, string fingerprint = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var fileName = Path.GetFileName(name);
        var (addresses, skipped) = PafCsv.Read(content);
        if (addresses.Count == 0)
        {
            throw new ArgumentException($"{fileName} has no addresses in the Royal Mail address-file layout ({PafCsv.Columns} columns, postcode in column 12).");
        }

        var source = new AddressSource(fileName, addresses.Count, addresses.Select(a => a.Postcode).Distinct(StringComparer.Ordinal).Count(), skipped, clock.GetUtcNow());
        db.RunInTransaction(() =>
        {
            db.Execute("DELETE FROM addresses WHERE source = $s;", ("$s", fileName));
            using var insert = db.Command("INSERT INTO addresses (source, postcode, line1, line2, town, label, sort_key) VALUES ($s, $p, $l1, $l2, $t, $label, $sort);",
                ("$s", fileName), ("$p", string.Empty), ("$l1", string.Empty), ("$l2", null), ("$t", string.Empty), ("$label", string.Empty), ("$sort", string.Empty));
            foreach (var a in addresses)
            {
                var d = a.ToDeliveryAddress();
                insert.Parameters["$p"].Value = a.Postcode;
                insert.Parameters["$l1"].Value = d.Line1;
                insert.Parameters["$l2"].Value = (object?)d.Line2 ?? DBNull.Value;
                insert.Parameters["$t"].Value = d.Town!;
                insert.Parameters["$label"].Value = a.Label();
                insert.Parameters["$sort"].Value = a.SortKey();
                insert.ExecuteNonQuery();
            }

            db.Execute(
                """
                INSERT OR REPLACE INTO address_sources (name, fingerprint, addresses, postcodes, skipped, imported_at)
                VALUES ($n, $f, $a, $p, $s, $at);
                """,
                ("$n", fileName), ("$f", fingerprint), ("$a", source.Addresses), ("$p", source.Postcodes), ("$s", skipped),
                ("$at", source.ImportedAt.ToString("O", CultureInfo.InvariantCulture)));
            return 0;
        });
        HubLog.AddressFileLoaded(log, fileName, source.Addresses, source.Postcodes);
        return source;
    }

    /// <summary>
    /// Loads every .csv in the address folder that is new or has changed since it was last loaded.
    /// Returns what was loaded; files that fail are logged and skipped.
    /// </summary>
    public IReadOnlyList<AddressSource> LoadFolder()
    {
        var folder = settings.AddressFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        var loaded = new List<AddressSource>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.csv").Order(StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(path);
            var fingerprint = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            var known = db.Run(() => db.Scalar("SELECT fingerprint FROM address_sources WHERE name = $n;", ("$n", info.Name)) as string);
            if (known == fingerprint)
            {
                continue;
            }

            try
            {
                using var reader = new StreamReader(path, Encoding.Latin1);
                loaded.Add(Import(info.Name, reader, fingerprint));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                HubLog.AddressFileFailed(log, info.Name, ex.Message);
            }
        }

        return loaded;
    }
}

/// <summary>Loads new or changed address files when the hub starts.</summary>
public sealed class AddressFolderWorker(AddressBook book) : BackgroundService
{
    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => book.LoadFolder(), stoppingToken);
}
