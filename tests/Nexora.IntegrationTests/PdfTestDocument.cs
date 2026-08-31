using System.Globalization;
using System.Text;

namespace Nexora.IntegrationTests;

internal static class PdfTestDocument
{
    public static byte[] CreateTextPdf(string text) => CreateTextPdfPages(text);

    public static byte[] CreateTextPdfPages(params string[] pages)
    {
        var pageCount = Math.Max(1, pages.Length);
        var objects = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
        };
        var pageIds = Enumerable.Range(3, pageCount).ToArray();
        var contentIds = Enumerable.Range(3 + pageCount, pageCount).ToArray();
        var fontId = contentIds[^1] + 1;
        objects.Add($"2 0 obj\n<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] /Count {pageCount} >>\nendobj\n");
        for (var index = 0; index < pageCount; index++)
        {
            objects.Add($"{pageIds[index]} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {fontId} 0 R >> >> /Contents {contentIds[index]} 0 R >>\nendobj\n");
        }
        for (var index = 0; index < pageCount; index++)
        {
            var value = pages.Length == 0 ? string.Empty : pages[index];
            var lines = value.Split('\n');
            var commands = string.Join("\n", lines.Select((line, lineIndex) =>
                $"1 0 0 1 72 {720 - lineIndex * 16} Tm ({Escape(line)}) Tj"));
            var stream = $"BT\n/F1 12 Tf\n{commands}\nET\n";
            objects.Add($"{contentIds[index]} 0 obj\n<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream\nendobj\n");
        }
        objects.Add($"{fontId} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        var document = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        foreach (var item in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(document.ToString()));
            document.Append(item);
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(document.ToString());
        document.Append(string.Format(CultureInfo.InvariantCulture, "xref\n0 {0}\n0000000000 65535 f \n", offsets.Count));
        foreach (var offset in offsets.Skip(1))
            document.Append(string.Format(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset));
        document.Append(string.Format(CultureInfo.InvariantCulture, "trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n", offsets.Count, xrefOffset));
        return Encoding.ASCII.GetBytes(document.ToString());
    }

    public static byte[] CreatePositionedTextPdf(params (string Text, double X, double Y)[] words)
    {
        var commands = string.Join("\n", words.Select(word =>
            $"1 0 0 1 {word.X.ToString(CultureInfo.InvariantCulture)} {word.Y.ToString(CultureInfo.InvariantCulture)} Tm ({Escape(word.Text)}) Tj"));
        return CreateCustomPagePdf(commands);
    }

    public static byte[] CreateImageOnlyPdf()
    {
        var pageContent = "q\n1 0 0 1 72 720 cm\n/Im1 Do\nQ\n";
        var imageContent = "0\n";
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n",
            $"4 0 obj\n<< /Length {Encoding.ASCII.GetByteCount(pageContent)} >>\nstream\n{pageContent}endstream\nendobj\n",
            $"5 0 obj\n<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length {Encoding.ASCII.GetByteCount(imageContent)} >>\nstream\n{imageContent}endstream\nendobj\n"
        };
        var document = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        foreach (var item in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(document.ToString()));
            document.Append(item);
        }
        var xrefOffset = Encoding.ASCII.GetByteCount(document.ToString());
        document.Append(string.Format(CultureInfo.InvariantCulture, "xref\n0 {0}\n0000000000 65535 f \n", offsets.Count));
        foreach (var offset in offsets.Skip(1))
            document.Append(string.Format(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset));
        document.Append(string.Format(CultureInfo.InvariantCulture, "trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n", offsets.Count, xrefOffset));
        return Encoding.ASCII.GetBytes(document.ToString());
    }

    public static byte[] CreateUnicodeTextPdf(string text)
    {
        var codePoints = text.EnumerateRunes().Select(rune => rune.Value).ToArray();
        var hexText = string.Concat(codePoints.Select(value => value.ToString("X4", CultureInfo.InvariantCulture)));
        var mappings = codePoints.Distinct().Select(value =>
            $"<{value:X4}> <{value:X4}>").ToArray();
        var cmap = $"/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n{mappings.Length} beginbfchar\n{string.Join("\n", mappings)}\nendbfchar\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n";
        var pageContent = $"BT\n/F1 14 Tf\n72 720 Td\n<{hexText}> Tj\nET\n";
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n",
            $"4 0 obj\n<< /Length {Encoding.ASCII.GetByteCount(pageContent)} >>\nstream\n{pageContent}endstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /NexoraUnicode /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 7 0 R >>\nendobj\n",
            "6 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /NexoraUnicode /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 8 0 R /DW 1000 /CIDToGIDMap /Identity >>\nendobj\n",
            $"7 0 obj\n<< /Length {Encoding.ASCII.GetByteCount(cmap)} >>\nstream\n{cmap}endstream\nendobj\n",
            "8 0 obj\n<< /Type /FontDescriptor /FontName /NexoraUnicode /Flags 4 /FontBBox [0 -200 1000 1000] /ItalicAngle 0 /Ascent 1000 /Descent -200 /CapHeight 700 /StemV 80 >>\nendobj\n"
        };
        var document = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        foreach (var item in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(document.ToString()));
            document.Append(item);
        }
        var xrefOffset = Encoding.ASCII.GetByteCount(document.ToString());
        document.Append(string.Format(CultureInfo.InvariantCulture, "xref\n0 {0}\n0000000000 65535 f \n", offsets.Count));
        foreach (var offset in offsets.Skip(1))
            document.Append(string.Format(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset));
        document.Append(string.Format(CultureInfo.InvariantCulture, "trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n", offsets.Count, xrefOffset));
        return Encoding.ASCII.GetBytes(document.ToString());
    }

    private static byte[] CreateCustomPagePdf(string commands)
    {
        var stream = $"BT\n/F1 12 Tf\n{commands}\nET\n";
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n",
            $"4 0 obj\n<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n"
        };
        var document = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        foreach (var item in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(document.ToString()));
            document.Append(item);
        }
        var xrefOffset = Encoding.ASCII.GetByteCount(document.ToString());
        document.Append(string.Format(CultureInfo.InvariantCulture, "xref\n0 {0}\n0000000000 65535 f \n", offsets.Count));
        foreach (var offset in offsets.Skip(1))
            document.Append(string.Format(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset));
        document.Append(string.Format(CultureInfo.InvariantCulture, "trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n", offsets.Count, xrefOffset));
        return Encoding.ASCII.GetBytes(document.ToString());
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("(", "\\(", StringComparison.Ordinal)
        .Replace(")", "\\)", StringComparison.Ordinal);
}
