using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Pos.Hub.MenuSheets;

/// <summary>One sheet of a spreadsheet: its name and its rows of cell text. Row 0 is spreadsheet row 1.</summary>
/// <param name="Name">Sheet name, e.g. "Menu".</param>
/// <param name="Rows">Rows of cells; blank rows are kept so row numbers match what the owner sees.</param>
public sealed record SheetData(string Name, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// Reads Excel (.xlsx) and CSV files, and writes simple .xlsx files, with no extra software or paid library.
/// Only cell text is read: formulas, macros and pictures are ignored, and files are checked for size first.
/// </summary>
public static class Spreadsheet
{
    /// <summary>Largest file accepted (a big menu is well under 200 KB).</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>Most rows read from one sheet.</summary>
    public const int MaxRows = 10_000;

    /// <summary>Most columns read from one row.</summary>
    public const int MaxColumns = 40;

    // Unzipped parts are capped too, so a small "zip bomb" cannot fill the hub's memory.
    private const long MaxPartBytes = 30L * 1024 * 1024;

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = false,
        CloseInput = true,
    };

    /// <summary>Reads an .xlsx or .csv file into sheets. A CSV file is one sheet, named after the file.</summary>
    /// <exception cref="ArgumentException">The file is empty, too big, or not a readable spreadsheet.</exception>
    public static IReadOnlyList<SheetData> Read(byte[] bytes, string fileName)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (bytes.Length == 0)
        {
            throw new ArgumentException("The file is empty.");
        }

        if (bytes.Length > MaxBytes)
        {
            throw new ArgumentException("The file is too big (most is 2 MB). Is it the right file?");
        }

        var extension = Path.GetExtension(fileName).ToUpperInvariant();
        var isZip = bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K';
        return extension switch
        {
            ".XLSX" when isZip => ReadXlsx(bytes),
            ".XLSX" => throw new ArgumentException("This file is not a real .xlsx workbook. In Excel use File → Save As → Excel Workbook (.xlsx)."),
            ".CSV" or ".TXT" => [ReadCsv(Decode(bytes), Path.GetFileNameWithoutExtension(fileName))],
            ".XLS" => throw new ArgumentException("Old .xls files cannot be read. In Excel use File → Save As → Excel Workbook (.xlsx)."),
            ".NUMBERS" or ".ODS" => throw new ArgumentException("Save the sheet as Excel (.xlsx) or CSV first."),
            _ => throw new ArgumentException("Choose an Excel (.xlsx) or CSV (.csv) file."),
        };
    }

    // ---------- CSV ----------

    /// <summary>Reads CSV text (Excel's "CSV (Comma delimited)"; semicolon or tab files also work).</summary>
    public static SheetData ReadCsv(string text, string name)
    {
        ArgumentNullException.ThrowIfNull(text);
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = firstLine.Contains(',', StringComparison.Ordinal) ? ','
            : firstLine.Contains(';', StringComparison.Ordinal) ? ';'
            : firstLine.Contains('\t', StringComparison.Ordinal) ? '\t' : ',';

        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }

                continue;
            }

            if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                AddCell(row, cell);
            }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                AddCell(row, cell);
                AddRow(rows, row);
                row = [];
            }
            else
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            AddCell(row, cell);
            AddRow(rows, row);
        }

        // A file ending in a line break leaves no extra blank row.
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrWhiteSpace))
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new SheetData(name, rows);
    }

    private static void AddCell(List<string> row, StringBuilder cell)
    {
        if (row.Count < MaxColumns)
        {
            row.Add(cell.ToString().Trim());
        }

        cell.Clear();
    }

    private static void AddRow(List<IReadOnlyList<string>> rows, List<string> row)
    {
        if (rows.Count >= MaxRows)
        {
            throw new ArgumentException($"The sheet has more than {MaxRows:N0} rows.");
        }

        rows.Add(row);
    }

    // UTF-8 (what Excel's "CSV UTF-8" saves), else Windows-1252 (plain "CSV" on a UK PC: £ is the same byte in Latin-1).
    private static string Decode(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    // ---------- XLSX reading ----------

    private static List<SheetData> ReadXlsx(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var shared = zip.GetEntry("xl/sharedStrings.xml") is { } s ? ReadSharedStrings(s) : [];
            var sheets = new List<SheetData>();
            foreach (var (name, path) in SheetPaths(zip))
            {
                if (zip.GetEntry(path) is { } entry)
                {
                    sheets.Add(new SheetData(name, ReadSheet(entry, shared)));
                }
            }

            return sheets.Count > 0 ? sheets : throw new ArgumentException("The workbook has no sheets.");
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or FormatException)
        {
            throw new ArgumentException("The Excel file could not be read. Open it in Excel, save it again as .xlsx, and try once more.", ex);
        }
    }

    private static XmlReader Open(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxPartBytes)
        {
            throw new ArgumentException("The workbook is too big to read.");
        }

        return XmlReader.Create(new LimitedStream(entry.Open(), MaxPartBytes), XmlSettings);
    }

    // Sheet names in workbook order, with the file each one is stored in.
    private static List<(string Name, string Path)> SheetPaths(ZipArchive zip)
    {
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (zip.GetEntry("xl/_rels/workbook.xml.rels") is { } rels)
        {
            using var reader = Open(rels);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship" && reader.GetAttribute("Id") is { } id && reader.GetAttribute("Target") is { } target)
                {
                    target = target.Replace('\\', '/');
                    targets[id] = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                }
            }
        }

        var sheets = new List<(string, string)>();
        var workbook = zip.GetEntry("xl/workbook.xml") ?? throw new ArgumentException("This is not an Excel workbook.");
        using (var reader = Open(workbook))
        {
            var index = 0;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet")
                {
                    index++;
                    var name = reader.GetAttribute("name") ?? $"Sheet{index}";
                    var relId = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    var path = relId is not null && targets.TryGetValue(relId, out var t) ? t : $"xl/worksheets/sheet{index}.xml";
                    sheets.Add((name, path));
                }
            }
        }

        return sheets;
    }

    private static List<string> ReadSharedStrings(ZipArchiveEntry entry)
    {
        var strings = new List<string>();
        using var reader = Open(entry);
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                strings.Add(TextOf(reader.ReadSubtree()));
            }

            reader.Read();
        }

        return strings;
    }

    // All the <t> text inside an element (rich text runs joined), leaving out phonetic hints (<rPh>).
    private static string TextOf(XmlReader element)
    {
        using (element)
        {
            var text = new StringBuilder();
            element.Read();
            while (!element.EOF)
            {
                if (element.NodeType == XmlNodeType.Element && element.LocalName == "rPh")
                {
                    element.Skip();
                }
                else if (element.NodeType == XmlNodeType.Element && element.LocalName == "t")
                {
                    text.Append(element.ReadElementContentAsString()); // moves on by itself
                }
                else
                {
                    element.Read();
                }
            }

            return text.ToString();
        }
    }

    private static List<IReadOnlyList<string>> ReadSheet(ZipArchiveEntry entry, List<string> shared)
    {
        var rows = new List<IReadOnlyList<string>>();
        using var reader = Open(entry);
        List<string>? row = null;
        var nextRow = 1;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
            {
                var number = int.TryParse(reader.GetAttribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : nextRow;
                if (number > MaxRows)
                {
                    throw new ArgumentException($"The sheet has more than {MaxRows:N0} rows.");
                }

                while (rows.Count < number - 1)
                {
                    rows.Add([]); // rows Excel left out because they are empty
                }

                row = [];
                rows.Add(row);
                nextRow = number + 1;
                if (reader.IsEmptyElement)
                {
                    row = null;
                }
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c" && row is not null)
            {
                var column = ColumnIndex(reader.GetAttribute("r")) ?? row.Count;
                var type = reader.GetAttribute("t");
                var value = reader.IsEmptyElement ? string.Empty : CellValue(reader.ReadSubtree(), type, shared);
                if (column < MaxColumns)
                {
                    while (row.Count < column)
                    {
                        row.Add(string.Empty);
                    }

                    row.Add(value.Trim());
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row")
            {
                row = null;
            }
        }

        while (rows.Count > 0 && rows[^1].All(string.IsNullOrWhiteSpace))
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return rows;
    }

    private static string CellValue(XmlReader cell, string? type, List<string> shared)
    {
        using (cell)
        {
            string? v = null;
            var inline = string.Empty;
            cell.Read();
            while (!cell.EOF)
            {
                if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "v")
                {
                    v = cell.ReadElementContentAsString(); // moves on by itself
                }
                else if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "is")
                {
                    inline = TextOf(cell.ReadSubtree());
                    cell.Read();
                }
                else
                {
                    cell.Read();
                }
            }

            return type switch
            {
                "s" => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < shared.Count ? shared[i] : string.Empty,
                "inlineStr" => inline,
                "b" => v == "1" ? "TRUE" : "FALSE",
                "e" => string.Empty, // #N/A and other errors
                _ => v ?? string.Empty, // numbers and formula results as Excel stored them
            };
        }
    }

    // "C12" → 2. Null when missing or odd.
    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        var index = 0;
        var letters = 0;
        foreach (var c in reference)
        {
            if (c is >= 'A' and <= 'Z')
            {
                index = (index * 26) + (c - 'A' + 1);
                letters++;
            }
            else
            {
                break;
            }
        }

        return letters is > 0 and <= 3 ? index - 1 : null;
    }

    // ---------- XLSX writing ----------

    /// <summary>Writes sheets as an .xlsx workbook. Row 0 of each sheet is shown bold as headings and stays in view.</summary>
    /// <param name="sheets">The sheets, each with text cells only (nothing is ever written as a formula).</param>
    /// <param name="widths">Column widths per sheet name, in Excel's character units.</param>
    public static byte[] WriteXlsx(IReadOnlyList<SheetData> sheets, IReadOnlyDictionary<string, int[]>? widths = null)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Part(zip, "[Content_Types].xml", w =>
            {
                w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                Element(w, "Default", ("Extension", "rels"), ("ContentType", "application/vnd.openxmlformats-package.relationships+xml"));
                Element(w, "Default", ("Extension", "xml"), ("ContentType", "application/xml"));
                Element(w, "Override", ("PartName", "/xl/workbook.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"));
                Element(w, "Override", ("PartName", "/xl/styles.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"));
                for (var i = 1; i <= sheets.Count; i++)
                {
                    Element(w, "Override", ("PartName", $"/xl/worksheets/sheet{i}.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
                }

                w.WriteEndElement();
            });

            Part(zip, "_rels/.rels", w =>
            {
                w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                Element(w, "Relationship", ("Id", "rId1"), ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"), ("Target", "xl/workbook.xml"));
                w.WriteEndElement();
            });

            Part(zip, "xl/workbook.xml", w =>
            {
                const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
                w.WriteStartElement("workbook", main);
                w.WriteAttributeString("xmlns", "r", null, rel);
                w.WriteStartElement("sheets", main);
                for (var i = 0; i < sheets.Count; i++)
                {
                    w.WriteStartElement("sheet", main);
                    w.WriteAttributeString("name", SheetName(sheets[i].Name));
                    w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("id", rel, $"rId{i + 1}");
                    w.WriteEndElement();
                }

                w.WriteEndElement();
                w.WriteEndElement();
            });

            Part(zip, "xl/_rels/workbook.xml.rels", w =>
            {
                w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                for (var i = 1; i <= sheets.Count; i++)
                {
                    Element(w, "Relationship", ("Id", $"rId{i}"), ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"), ("Target", $"worksheets/sheet{i}.xml"));
                }

                Element(w, "Relationship", ("Id", $"rId{sheets.Count + 1}"), ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"), ("Target", "styles.xml"));
                w.WriteEndElement();
            });

            Part(zip, "xl/styles.xml", WriteStyles);

            for (var i = 0; i < sheets.Count; i++)
            {
                var sheet = sheets[i];
                var columnWidths = widths is not null && widths.TryGetValue(sheet.Name, out var cw) ? cw : null;
                Part(zip, $"xl/worksheets/sheet{i + 1}.xml", w => WriteSheet(w, sheet, columnWidths));
            }
        }

        return buffer.ToArray();
    }

    private static void WriteStyles(XmlWriter w)
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        w.WriteStartElement("styleSheet", main);
        w.WriteStartElement("fonts", main);
        w.WriteAttributeString("count", "2");
        w.WriteStartElement("font", main);
        Element(w, "sz", ("val", "11"));
        Element(w, "name", ("val", "Calibri"));
        w.WriteEndElement();
        w.WriteStartElement("font", main);
        Element(w, "b");
        Element(w, "sz", ("val", "11"));
        Element(w, "name", ("val", "Calibri"));
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteStartElement("fills", main);
        w.WriteAttributeString("count", "3");
        w.WriteStartElement("fill", main);
        Element(w, "patternFill", ("patternType", "none"));
        w.WriteEndElement();
        w.WriteStartElement("fill", main);
        Element(w, "patternFill", ("patternType", "gray125"));
        w.WriteEndElement();
        w.WriteStartElement("fill", main);
        w.WriteStartElement("patternFill", main);
        w.WriteAttributeString("patternType", "solid");
        Element(w, "fgColor", ("rgb", "FFE5E7EB"));
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteStartElement("borders", main);
        w.WriteAttributeString("count", "1");
        Element(w, "border");
        w.WriteEndElement();
        w.WriteStartElement("cellStyleXfs", main);
        w.WriteAttributeString("count", "1");
        Element(w, "xf", ("numFmtId", "0"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0"));
        w.WriteEndElement();
        w.WriteStartElement("cellXfs", main);
        w.WriteAttributeString("count", "4");
        Element(w, "xf", ("numFmtId", "0"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0"), ("xfId", "0"));
        Element(w, "xf", ("numFmtId", "0"), ("fontId", "1"), ("fillId", "2"), ("borderId", "0"), ("xfId", "0"), ("applyFont", "1"), ("applyFill", "1"));
        w.WriteStartElement("xf", main); // wrapped text, for the instructions sheet
        w.WriteAttributeString("numFmtId", "0");
        w.WriteAttributeString("fontId", "0");
        w.WriteAttributeString("fillId", "0");
        w.WriteAttributeString("borderId", "0");
        w.WriteAttributeString("xfId", "0");
        w.WriteAttributeString("applyAlignment", "1");
        Element(w, "alignment", ("wrapText", "1"), ("vertical", "top"));
        w.WriteEndElement();
        Element(w, "xf", ("numFmtId", "2"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0"), ("xfId", "0"), ("applyNumberFormat", "1")); // 0.00 for prices
        w.WriteEndElement();
        w.WriteStartElement("cellStyles", main);
        w.WriteAttributeString("count", "1");
        Element(w, "cellStyle", ("name", "Normal"), ("xfId", "0"), ("builtinId", "0"));
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void WriteSheet(XmlWriter w, SheetData sheet, int[]? widths)
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        w.WriteStartElement("worksheet", main);
        w.WriteStartElement("sheetViews", main);
        w.WriteStartElement("sheetView", main);
        w.WriteAttributeString("workbookViewId", "0");
        if (sheet.Rows.Count > 1)
        {
            Element(w, "pane", ("ySplit", "1"), ("topLeftCell", "A2"), ("activePane", "bottomLeft"), ("state", "frozen"));
        }

        w.WriteEndElement();
        w.WriteEndElement();
        if (widths is { Length: > 0 })
        {
            w.WriteStartElement("cols", main);
            for (var i = 0; i < widths.Length; i++)
            {
                var n = (i + 1).ToString(CultureInfo.InvariantCulture);
                Element(w, "col", ("min", n), ("max", n), ("width", widths[i].ToString(CultureInfo.InvariantCulture)), ("customWidth", "1"));
            }

            w.WriteEndElement();
        }

        var wrap = widths is { Length: 1 };
        w.WriteStartElement("sheetData", main);
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            w.WriteStartElement("row", main);
            w.WriteAttributeString("r", (r + 1).ToString(CultureInfo.InvariantCulture));
            var cells = sheet.Rows[r];
            for (var c = 0; c < cells.Count; c++)
            {
                if (string.IsNullOrEmpty(cells[c]))
                {
                    continue;
                }

                w.WriteStartElement("c", main);
                w.WriteAttributeString("r", ColumnName(c) + (r + 1).ToString(CultureInfo.InvariantCulture));
                if (r > 0 && !wrap && IsPrice(cells[c]))
                {
                    // Prices as numbers (shown 0.00), so Excel can add them up and does not flag "number stored as text".
                    w.WriteAttributeString("s", "3");
                    w.WriteElementString("v", main, cells[c]);
                    w.WriteEndElement();
                    continue;
                }

                w.WriteAttributeString("t", "inlineStr");
                if (r == 0 && !wrap)
                {
                    w.WriteAttributeString("s", "1");
                }
                else if (wrap)
                {
                    w.WriteAttributeString("s", r == 0 ? "1" : "2");
                }

                w.WriteStartElement("is", main);
                w.WriteStartElement("t", main);
                w.WriteAttributeString("xml", "space", null, "preserve");
                w.WriteString(XmlSafe(cells[c]));
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static bool IsPrice(string text) =>
        text.Length is >= 4 and <= 9 && text[^3] == '.' && text.Remove(text.Length - 3, 1).All(char.IsAsciiDigit) && (text[0] != '0' || text[1] == '.');

    private static void Part(ZipArchive zip, string name, Action<XmlWriter> write)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        writer.WriteStartDocument(standalone: true);
        write(writer);
        writer.WriteEndDocument();
    }

    private static void Element(XmlWriter w, string name, params (string Name, string Value)[] attributes)
    {
        w.WriteStartElement(null, name, null); // in the namespace of the element around it
        foreach (var (n, v) in attributes)
        {
            w.WriteAttributeString(n, v);
        }

        w.WriteEndElement();
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var i = index + 1; i > 0; i = (i - 1) / 26)
        {
            name = (char)('A' + ((i - 1) % 26)) + name;
        }

        return name;
    }

    // Excel sheet names: at most 31 characters, none of : \ / ? * [ ].
    private static string SheetName(string name)
    {
        var clean = new string([.. name.Where(c => ":\\/?*[]".IndexOf(c, StringComparison.Ordinal) < 0)]).Trim();
        return clean.Length == 0 ? "Sheet" : clean[..Math.Min(31, clean.Length)];
    }

    private static string XmlSafe(string text) =>
        new([.. text.Where(c => c is '\t' or '\n' or '\r' || (c >= 0x20 && c != 0xFFFE && c != 0xFFFF))]);

    /// <summary>Stops reading a stream after a number of bytes.</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            _read += n;
            return _read > limit ? throw new ArgumentException("The workbook is too big to read.") : n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
