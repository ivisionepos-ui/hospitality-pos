namespace Pos.Hub.Printing;

/// <summary>A black-and-white picture for a thermal printer: each row is WidthBytes bytes, 8 dots per byte, first dot in the top bit.</summary>
/// <param name="WidthBytes">Bytes per row (dots ÷ 8).</param>
/// <param name="Height">Rows.</param>
/// <param name="Bits">Rows one after another; a 1 bit is a black dot.</param>
public sealed record MonoImage(int WidthBytes, int Height, byte[] Bits);

/// <summary>
/// ESC/POS commands, understood by almost all receipt and kitchen thermal printers (Epson TM range, most 80 mm and
/// 58 mm printers, Star printers in ESC/POS mode). Printouts are sent as a picture ("GS v 0" raster), so the £ sign,
/// bold text and the layout print exactly as in the PDF, whatever character set the printer has.
/// </summary>
public static class EscPos
{
    /// <summary>Most rows sent in one picture command (some printers have small buffers).</summary>
    public const int BandRows = 255;

    /// <summary>ESC @: reset the printer to its defaults.</summary>
    public static readonly byte[] Initialise = [0x1B, 0x40];

    /// <summary>ESC d 4 then GS V 66 0: feed past the cutter and cut, leaving a small tab (partial cut).</summary>
    public static readonly byte[] FeedAndCut = [0x1B, 0x64, 0x04, 0x1D, 0x56, 0x42, 0x00];

    /// <summary>ESC d 6: feed so the printout can be torn off (printers without a cutter).</summary>
    public static readonly byte[] FeedOnly = [0x1B, 0x64, 0x06];

    /// <summary>
    /// ESC p: pulse the cash drawer connector, 50 ms on and 500 ms off. Pin 2 is the usual drawer socket; pin 5 the
    /// second one on printers that have two.
    /// </summary>
    public static byte[] DrawerKick(int pin) => [0x1B, 0x70, pin == 5 ? (byte)0x01 : (byte)0x00, 0x19, 0xFA];

    /// <summary>A whole printout: reset, the picture (in bands), then cut (or feed).</summary>
    public static byte[] Printout(MonoImage image, bool cut)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var stream = new MemoryStream();
        stream.Write(Initialise);
        for (var top = 0; top < image.Height; top += BandRows)
        {
            var rows = Math.Min(BandRows, image.Height - top);
            // GS v 0 m xL xH yL yH: m = 0 (normal size), x = bytes per row, y = rows.
            stream.Write([0x1D, 0x76, 0x30, 0x00, (byte)(image.WidthBytes & 0xFF), (byte)(image.WidthBytes >> 8), (byte)(rows & 0xFF), (byte)(rows >> 8)]);
            stream.Write(image.Bits, top * image.WidthBytes, rows * image.WidthBytes);
        }

        stream.Write(cut ? FeedAndCut : FeedOnly);
        return stream.ToArray();
    }
}
