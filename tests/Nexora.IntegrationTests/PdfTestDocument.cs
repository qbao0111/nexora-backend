using System.Globalization;
using System.Text;

namespace Nexora.IntegrationTests;

internal static class PdfTestDocument
{
    public static byte[] CreateTextPdf(string text)
    {
        var escapedText = text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n",
            $"4 0 obj\n<< /Length {Encoding.ASCII.GetByteCount($"BT\n/F1 12 Tf\n72 720 Td\n({escapedText}) Tj\nET\n")} >>\nstream\nBT\n/F1 12 Tf\n72 720 Td\n({escapedText}) Tj\nET\nendstream\nendobj\n",
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
}
