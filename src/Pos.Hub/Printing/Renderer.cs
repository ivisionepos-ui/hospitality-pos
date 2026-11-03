using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Pos.Hub.Printing;

/// <summary>
/// Draws a printout at printer resolution (203 dots per inch): 576 dots wide for 80 mm paper, 384 for 58 mm.
/// Text is shaped with HarfBuzz so Arabic letters join correctly, and lines mixing Arabic and English are
/// laid out in the right order. The same drawing goes to PNG, PDF, Windows printers and (later) thermal printers.
/// </summary>
public sealed class Renderer : IDisposable
{
    /// <summary>Printer dots per inch.</summary>
    public const float Dpi = 203f;

    private readonly SKTypeface _regular;
    private readonly SKTypeface _bold;
    private readonly SKShaper _regularShaper;
    private readonly SKShaper _boldShaper;

    /// <summary>Loads the fonts.</summary>
    public Renderer(string regularFontFile, string boldFontFile)
    {
        _regular = LoadFont(regularFontFile);
        _bold = LoadFont(boldFontFile);
        _regularShaper = new SKShaper(_regular);
        _boldShaper = new SKShaper(_bold);
    }

    /// <summary>Draws the printout and returns it as a PNG image.</summary>
    public byte[] RenderPng(PrintDocument document, int widthDots)
    {
        var ops = Layout(document, widthDots, out var height);
        using var bitmap = new SKBitmap(widthDots, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            Draw(canvas, ops);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Draws the printout as a one-page PDF exactly the width of the paper roll.</summary>
    public byte[] RenderPdf(PrintDocument document, int widthDots, double paperWidthMm)
    {
        var ops = Layout(document, widthDots, out var height);
        var pointsPerDot = 72f / Dpi;
        var marginDots = (float)((paperWidthMm / 25.4 * Dpi) - widthDots) / 2f;
        using var stream = new MemoryStream();
        using (var pdf = SKDocument.CreatePdf(stream))
        {
            var pageWidth = (float)(paperWidthMm / 25.4 * 72);
            using var canvas = pdf.BeginPage(pageWidth, (height + 40) * pointsPerDot);
            canvas.Scale(pointsPerDot);
            canvas.Translate(Math.Max(0, marginDots), 20);
            Draw(canvas, ops);
            pdf.EndPage();
            pdf.Close();
        }

        return stream.ToArray();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _regularShaper.Dispose();
        _boldShaper.Dispose();
        _regular.Dispose();
        _bold.Dispose();
    }

    /// <summary>True for characters of the Arabic script.</summary>
    public static bool IsArabic(char c) =>
        c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ࢠ' and <= 'ࣿ'
            or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿';

    private static bool IsNeutral(char c) => char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c);

    private static SKTypeface LoadFont(string file)
    {
        var path = Path.IsPathRooted(file) ? file : Path.Combine(AppContext.BaseDirectory, file);
        return SKTypeface.FromFile(path) ?? throw new FileNotFoundException($"Font not found: {path}", path);
    }

    private static float FontSize(PrintSize size) => size switch
    {
        PrintSize.Small => 20,
        PrintSize.Large => 32,
        PrintSize.Huge => 56,
        _ => 24,
    };

    // ---------- Layout ----------

    /// <summary>A piece of text placed at an x position on a baseline.</summary>
    private sealed record DrawOp(float X, float Y, string Text, float Size, bool Bold, bool White, SKRect? Band, bool Dashed);

    private List<DrawOp> Layout(PrintDocument document, int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(document);
        var ops = new List<DrawOp>();
        const float margin = 4;
        var y = 8f;

        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case PrintSpace space:
                    y += (float)space.Lines * FontSize(PrintSize.Normal) * 1.3f;
                    break;

                case PrintRule:
                    y += 10;
                    ops.Add(new DrawOp(margin, y, string.Empty, 0, false, false, new SKRect(margin, y, width - margin, y + 2), Dashed: true));
                    y += 12;
                    break;

                case PrintText text:
                {
                    var size = FontSize(text.Size);
                    var indent = text.Indent * size;
                    var lines = Wrap(text.Text, size, text.Bold, width - (2 * margin) - indent);
                    foreach (var line in lines)
                    {
                        var lineHeight = size * 1.3f;
                        var rtl = StartsRtl(line);
                        var lineWidth = Measure(line, size, text.Bold);
                        var start = rtl ? width - margin - indent - lineWidth : margin + indent;
                        var x = text.Align switch
                        {
                            PrintAlign.Center => (width - lineWidth) / 2,
                            PrintAlign.End => rtl ? margin + indent : width - margin - lineWidth,
                            _ => start,
                        };
                        SKRect? band = text.Inverted ? new SKRect(0, y, width, y + lineHeight + 4) : null;
                        ops.Add(new DrawOp(x, y + size, line, size, text.Bold, text.Inverted, band, Dashed: false));
                        y += lineHeight + (text.Inverted ? 4 : 0);
                    }

                    break;
                }

                case PrintColumns columns:
                {
                    var size = FontSize(columns.Size);
                    var indent = columns.Indent * size;
                    var rightWidth = Measure(columns.Right, size, columns.Bold);
                    var leftSpace = width - (2 * margin) - indent - rightWidth - size;
                    var lines = Wrap(columns.Left, size, columns.Bold, Math.Max(leftSpace, width / 3f));
                    for (var i = 0; i < lines.Count; i++)
                    {
                        ops.Add(new DrawOp(margin + indent, y + size, lines[i], size, columns.Bold, false, null, false));
                        if (i == 0)
                        {
                            ops.Add(new DrawOp(width - margin - rightWidth, y + size, columns.Right, size, columns.Bold, false, null, false));
                        }

                        y += size * 1.3f;
                    }

                    break;
                }

                default:
                    throw new NotSupportedException($"Unknown print block {block.GetType().Name}.");
            }
        }

        height = (int)Math.Ceiling(y + 24);
        return ops;
    }

    private List<string> Wrap(string text, float size, bool bold, float maxWidth)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = string.Empty;
            foreach (var word in words)
            {
                var candidate = current.Length == 0 ? word : $"{current} {word}";
                if (current.Length > 0 && Measure(candidate, size, bold) > maxWidth)
                {
                    result.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }

            result.Add(current);
        }

        return result;
    }

    // ---------- Mixed Arabic and English ----------

    private static bool StartsRtl(string text)
    {
        foreach (var c in text)
        {
            if (IsArabic(c))
            {
                return true;
            }

            if (char.IsLetter(c))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits a line into runs of Arabic and non-Arabic text. Spaces and punctuation stay with the run before them.
    /// Runs are returned in the order they appear on paper from left to right.
    /// </summary>
    private static List<(string Text, bool Arabic)> VisualRuns(string line)
    {
        var runs = new List<(string Text, bool Arabic)>();
        var current = new System.Text.StringBuilder();
        bool? arabic = null;
        foreach (var c in line)
        {
            if (!IsNeutral(c))
            {
                // Letters decide the run; digits always read left to right, so they belong to non-Arabic runs.
                var isArabic = IsArabic(c);
                if (arabic is { } kind && kind != isArabic)
                {
                    runs.Add((current.ToString(), kind));
                    current.Clear();
                }

                arabic = isArabic;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            runs.Add((current.ToString(), arabic ?? false));
        }

        if (StartsRtl(line))
        {
            runs.Reverse();
        }

        return runs;
    }

    private float Measure(string text, float size, bool bold)
    {
        using var font = new SKFont(bold ? _bold : _regular, size);
        var shaper = bold ? _boldShaper : _regularShaper;
        return VisualRuns(text).Sum(r => shaper.Shape(r.Text, font).Width);
    }

    // ---------- Drawing ----------

    private void Draw(SKCanvas canvas, List<DrawOp> ops)
    {
        using var black = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var dash = new SKPaint { Color = SKColors.Black, StrokeWidth = 2, PathEffect = SKPathEffect.CreateDash([8, 6], 0) };

        foreach (var op in ops)
        {
            if (op.Dashed && op.Band is { } rule)
            {
                canvas.DrawLine(rule.Left, rule.Top, rule.Right, rule.Top, dash);
                continue;
            }

            if (op.Band is { } band)
            {
                canvas.DrawRect(band, black);
            }

            using var font = new SKFont(op.Bold ? _bold : _regular, op.Size);
            var shaper = op.Bold ? _boldShaper : _regularShaper;
            var x = op.X;
            foreach (var (run, _) in VisualRuns(op.Text))
            {
                canvas.DrawShapedText(shaper, run, x, op.Y, SKTextAlign.Left, font, op.White ? white : black);
                x += shaper.Shape(run, font).Width;
            }
        }
    }
}
