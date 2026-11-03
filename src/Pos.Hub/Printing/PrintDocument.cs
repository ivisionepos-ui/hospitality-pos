namespace Pos.Hub.Printing;

/// <summary>Text size on a printout.</summary>
public enum PrintSize
{
    /// <summary>Small print (footers, VAT details).</summary>
    Small,

    /// <summary>Normal text.</summary>
    Normal,

    /// <summary>Large text (totals, kitchen items).</summary>
    Large,

    /// <summary>Very large text (order numbers).</summary>
    Huge,
}

/// <summary>Where text sits across the paper.</summary>
public enum PrintAlign
{
    /// <summary>Left (or right for Arabic text, which reads right to left).</summary>
    Start,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Right (or left for Arabic text).</summary>
    End,
}

/// <summary>One block of a printout.</summary>
public abstract record PrintBlock;

/// <summary>A line (or wrapped lines) of text. Arabic text is laid out right to left automatically.</summary>
/// <param name="Text">The text.</param>
/// <param name="Align">Where it sits.</param>
/// <param name="Size">Size.</param>
/// <param name="Bold">Bold.</param>
/// <param name="Indent">Indent from the start edge, in steps of about two characters.</param>
/// <param name="Inverted">White text on a black band (headings such as VOID).</param>
public sealed record PrintText(string Text, PrintAlign Align = PrintAlign.Start, PrintSize Size = PrintSize.Normal, bool Bold = false, int Indent = 0, bool Inverted = false) : PrintBlock;

/// <summary>Text on the left and an amount on the right of the same line (the left side wraps if long).</summary>
/// <param name="Left">Description.</param>
/// <param name="Right">Amount or other short value.</param>
/// <param name="Size">Size.</param>
/// <param name="Bold">Bold.</param>
/// <param name="Indent">Indent of the description.</param>
public sealed record PrintColumns(string Left, string Right, PrintSize Size = PrintSize.Normal, bool Bold = false, int Indent = 0) : PrintBlock;

/// <summary>A dashed line across the paper.</summary>
public sealed record PrintRule : PrintBlock;

/// <summary>Empty space.</summary>
/// <param name="Lines">Height in lines of normal text.</param>
public sealed record PrintSpace(double Lines = 1) : PrintBlock;

/// <summary>A printout: receipt, kitchen ticket or report.</summary>
/// <param name="Title">Short name used for file names and logs, e.g. "Receipt 045".</param>
/// <param name="Blocks">Content, top to bottom.</param>
public sealed record PrintDocument(string Title, IReadOnlyList<PrintBlock> Blocks)
{
    /// <summary>All text in the printout, one line per block (for tests and logs).</summary>
    public string PlainText => string.Join('\n', Blocks.Select(b => b switch
    {
        PrintText t => new string(' ', t.Indent * 2) + t.Text,
        PrintColumns c => $"{new string(' ', c.Indent * 2)}{c.Left} | {c.Right}",
        PrintRule => "----",
        _ => string.Empty,
    }));
}
