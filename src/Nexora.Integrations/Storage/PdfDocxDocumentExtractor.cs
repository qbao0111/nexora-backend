using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Options;
using Nexora.Business.Practice;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Nexora.Integrations.Storage;

public sealed class PdfDocxDocumentExtractor(IOptions<DocumentExtractionQualityOptions>? options = null)
    : IDocumentExtractor, IDetailedDocumentExtractor
{
    private const string PdfContentType = "application/pdf";
    private const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const double LineTolerance = 4;
    private const int MaxLayoutWords = 5_000;
    private readonly DocumentExtractionQualityOptions qualityOptions = options?.Value ?? new();

    public async Task<string> ExtractAsync(Stream content, string contentType, CancellationToken cancellationToken)
    {
        var result = await ExtractDetailedAsync(content, contentType, cancellationToken).ConfigureAwait(false);
        if (result.Quality == DocumentExtractionQuality.Failed)
            throw new InvalidDataException("Document text extraction quality is insufficient; OCR may be required.");
        return result.Text;
    }

    public Task<DocumentExtractionResult> ExtractDetailedAsync(
        Stream content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();

        if (!content.CanRead || (content.CanSeek && content.Length == 0))
            throw new InvalidDataException("Document is empty.");
        if (!content.CanSeek)
            throw new InvalidDataException("Document stream must be seekable.");

        try
        {
            content.Position = 0;
            var normalizedContentType = contentType?.Trim().ToLowerInvariant();
            var result = normalizedContentType switch
            {
                PdfContentType => ExtractPdf(content, cancellationToken),
                DocxContentType => ExtractDocx(content, cancellationToken),
                _ => throw new NotSupportedException("Only PDF and DOCX documents are supported.")
            };

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("Document could not be read.", exception);
        }
    }

    public DocumentExtractionResult EvaluateExtractedText(
        string text,
        int pageCount,
        DocumentExtractionMethod method,
        IEnumerable<string>? warnings = null)
    {
        var warningList = warnings?.ToList() ?? [];
        var normalized = NormalizePages([text ?? string.Empty], warningList);
        return Evaluate(normalized, Math.Max(1, pageCount), method, warningList);
    }

    private DocumentExtractionResult ExtractPdf(Stream content, CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(content);
        var pages = document.GetPages().ToArray();
        var fastPageTexts = new List<string>(pages.Length);
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // PdfPig's content-order extractor is the inexpensive normal path.
            fastPageTexts.Add(ContentOrderTextExtractor.GetText(page));
        }

        var fastWarnings = new List<string>();
        var fastText = NormalizePages(fastPageTexts, fastWarnings);
        var fastResult = Evaluate(
            fastText,
            pages.Length,
            DocumentExtractionMethod.PdfText,
            fastWarnings);

        var cachedWords = new Dictionary<int, Word[]>();
        var layoutNeeded = fastResult.Quality != DocumentExtractionQuality.Good;
        if (!layoutNeeded)
        {
            for (var index = 0; index < pages.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var words = GetBoundedWords(pages[index]);
                if (!LooksMultiColumn(words, pages[index].Width)) continue;
                cachedWords[index] = words;
                layoutNeeded = true;
                break;
            }
        }

        if (!layoutNeeded)
            return fastResult;

        var layoutPageTexts = new List<string>(pages.Length);
        var layoutWarnings = new List<string> { "LAYOUT_RECONSTRUCTION_ATTEMPTED" };
        for (var index = 0; index < pages.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!cachedWords.TryGetValue(index, out var words))
            {
                words = GetBoundedWords(pages[index]);
                cachedWords[index] = words;
            }

            layoutPageTexts.Add(ReconstructPage(words, pages[index].Width));
        }

        var layoutText = NormalizePages(layoutPageTexts, layoutWarnings);
        var layoutResult = Evaluate(
            layoutText,
            pages.Length,
            DocumentExtractionMethod.PdfLayoutReconstructed,
            layoutWarnings);

        // Keep the representation with the stronger deterministic quality signal, while
        // retaining an audit hint that the bounded fallback was attempted.
        var selected = layoutResult.QualityScore >= fastResult.QualityScore ? layoutResult : fastResult;
        return selected with
        {
            Warnings = selected.Warnings.Append("LAYOUT_RECONSTRUCTION_ATTEMPTED")
                .Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private DocumentExtractionResult ExtractDocx(Stream content, CancellationToken cancellationToken)
    {
        var effectiveStream = PrepareDocxStream(content);
        try
        {
            using var document = WordprocessingDocument.Open(effectiveStream, false);
            var body = document.MainDocumentPart?.Document?.Body
                ?? throw new InvalidDataException("DOCX document has no body.");
            var blocks = new List<string>();

            foreach (var element in body.Elements())
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (element)
                {
                    case Paragraph paragraph:
                        AddBlock(blocks, paragraph.InnerText);
                        break;
                    case Table table:
                        foreach (var row in table.Elements<TableRow>())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var cells = row.Elements<TableCell>()
                                .Select(cell => string.Join(" ", cell.Descendants<Paragraph>()
                                    .Select(paragraph => CollapseWhitespace(paragraph.InnerText))
                                    .Where(value => value.Length > 0)))
                                .Where(value => value.Length > 0)
                                .ToArray();
                            if (cells.Length > 0) AddBlock(blocks, string.Join(" | ", cells));
                        }
                        break;
                }
            }

            var warnings = new List<string>();
            var text = NormalizePages([string.Join("\n", blocks)], warnings);
            return Evaluate(text, 1, DocumentExtractionMethod.DocxOpenXml, warnings);
        }
        finally
        {
            if (!ReferenceEquals(effectiveStream, content))
                effectiveStream.Dispose();
        }
    }

    private static Stream PrepareDocxStream(Stream content)
    {
        if (!content.CanSeek || content.Length < 4) return content;
        content.Position = 0;
        var header = new byte[Math.Min(content.Length, 1024)];
        var read = content.Read(header, 0, header.Length);
        content.Position = 0;
        if (read < 4) return content;

        var slice = header.AsSpan(0, read);
        if (slice.Length >= 3 && slice[0] == 0xEF && slice[1] == 0xBB && slice[2] == 0xBF)
            slice = slice[3..];

        while (slice.Length > 0 && slice[0] is (byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)' ')
            slice = slice[1..];

        var preambleLength = read - slice.Length;
        if (preambleLength > 0 && slice.Length >= 4 && slice[0] == 0x50 && slice[1] == 0x4B && slice[2] == 0x03 && slice[3] == 0x04)
        {
            content.Position = preambleLength;
            var subStream = new MemoryStream(checked((int)(content.Length - preambleLength)));
            content.CopyTo(subStream);
            subStream.Position = 0;
            return subStream;
        }

        return content;
    }

    private static void AddBlock(List<string> blocks, string? value)
    {
        var normalized = CollapseWhitespace(value);
        if (normalized.Length > 0) blocks.Add(normalized);
    }

    private static Word[] GetBoundedWords(Page page) =>
        page.GetWords()
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .Take(MaxLayoutWords)
            .ToArray();

    private static bool LooksMultiColumn(Word[] words, double pageWidth)
    {
        if (words.Length < 12 || pageWidth <= 0) return false;

        var midpoint = pageWidth / 2;
        var margin = pageWidth * 0.08;
        var lines = GroupLines(words);
        var linesWithSeparatedClusters = lines.Count(line =>
            line.Words.Any(word => word.CenterX < midpoint - margin) &&
            line.Words.Any(word => word.CenterX > midpoint + margin));
        var leftWords = words.Count(word => word.BoundingBox.Right <= midpoint);
        var rightWords = words.Count(word => word.BoundingBox.Left >= midpoint);
        return linesWithSeparatedClusters >= 3 && leftWords >= 4 && rightWords >= 4;
    }

    private static string ReconstructPage(Word[] words, double pageWidth)
    {
        if (words.Length == 0) return string.Empty;
        var lines = GroupLines(words);
        if (!LooksMultiColumn(words, pageWidth))
            return string.Join("\n", lines.Select(line => JoinWords(line.Words.OrderBy(word => word.Left))));

        var midpoint = pageWidth / 2;
        var margin = pageWidth * 0.08;
        var fullWidth = new List<WordLine>();
        var leftColumn = new List<WordLine>();
        var rightColumn = new List<WordLine>();

        foreach (var line in lines)
        {
            var left = line.Words.Where(word => word.CenterX < midpoint - margin).OrderBy(word => word.Left).ToArray();
            var right = line.Words.Where(word => word.CenterX > midpoint + margin).OrderBy(word => word.Left).ToArray();
            if (left.Length > 0 && right.Length > 0)
            {
                leftColumn.Add(new WordLine(line.Y, left));
                rightColumn.Add(new WordLine(line.Y, right));
            }
            else if (left.Length > 0)
            {
                leftColumn.Add(new WordLine(line.Y, left));
            }
            else if (right.Length > 0)
            {
                rightColumn.Add(new WordLine(line.Y, right));
            }
            else
            {
                fullWidth.Add(line);
            }
        }

        return string.Join("\n", fullWidth.Concat(leftColumn).Concat(rightColumn)
            .Select(line => JoinWords(line.Words)));
    }

    private static WordLine[] GroupLines(IReadOnlyList<Word> words)
    {
        var groups = new List<List<PositionedWord>>();
        foreach (var word in words.OrderByDescending(item => item.BoundingBox.Bottom))
        {
            var positioned = new PositionedWord(
                word.Text.Trim(),
                word.BoundingBox.Left,
                word.BoundingBox.Right,
                word.BoundingBox.Bottom);
            var group = groups.LastOrDefault(item => Math.Abs(item[0].Y - positioned.Y) <= LineTolerance);
            if (group is null) groups.Add([positioned]);
            else group.Add(positioned);
        }

        return groups.Select(group => new WordLine(
                group.Average(word => word.Y),
                group.OrderBy(word => word.Left)
                    .Select(word => new PositionedWordValue(word.Text, word.Left, word.Right, word.Y))
                    .ToArray()))
            .ToArray();
    }

    private static string JoinWords(IEnumerable<PositionedWordValue> words) =>
        string.Join(" ", words.Select(word => word.Text).Where(value => value.Length > 0));

    private static string NormalizePages(IReadOnlyList<string> pages, List<string> warnings)
    {
        var normalizedPages = pages.Select(page => NormalizePage(page, warnings)).ToArray();
        if (normalizedPages.Length > 1)
        {
            var repeatedBoundaries = normalizedPages
                .SelectMany(page => BoundaryLines(page))
                .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() >= 2)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (repeatedBoundaries.Count > 0)
            {
                for (var index = 0; index < normalizedPages.Length; index++)
                {
                    var oldLines = normalizedPages[index].Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var lines = oldLines.Where(line => !repeatedBoundaries.Contains(line)).ToArray();
                    if (lines.Length != oldLines.Length) warnings.Add("REPEATED_HEADER_FOOTER_REMOVED");
                    normalizedPages[index] = string.Join('\n', lines);
                }
            }
        }

        return string.Join(Environment.NewLine, normalizedPages.Where(page => page.Length > 0)).Trim();
    }

    private static IEnumerable<string> BoundaryLines(string page)
    {
        var lines = page.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) yield break;
        yield return lines[0];
        if (lines.Length > 1) yield return lines[^1];
    }

    private static string NormalizePage(string? text, List<string> warnings)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var normalizedLines = new List<string>();
        string? previous = null;
        foreach (var rawLine in text.Replace("\0", string.Empty, StringComparison.Ordinal)
                     .Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n')
                     .Split('\n'))
        {
            var line = CollapseWhitespace(rawLine);
            if (line.Length == 0) continue;
            if (IsDecorativeSeparator(line))
            {
                warnings.Add("DECORATIVE_SEPARATOR_REMOVED");
                continue;
            }
            if (IsIsolatedPageNumber(line))
            {
                warnings.Add("ISOLATED_PAGE_NUMBER_REMOVED");
                continue;
            }
            if (string.Equals(previous, line, StringComparison.Ordinal))
            {
                warnings.Add("DUPLICATE_ADJACENT_LINE_REMOVED");
                continue;
            }
            normalizedLines.Add(line);
            previous = line;
        }
        return string.Join('\n', normalizedLines);
    }

    private DocumentExtractionResult Evaluate(
        string text,
        int pageCount,
        DocumentExtractionMethod method,
        ICollection<string> existingWarnings)
    {
        var warnings = existingWarnings.Distinct(StringComparer.Ordinal).ToList();
        var characterCount = text.Length;
        var wordCount = CountWords(text);
        var contentCharacters = text.Where(character => character is not '\n' and not '\r' and not '\t').ToArray();
        var printableCount = contentCharacters.Count(character => !char.IsControl(character) && character != '\uFFFD');
        var replacementCount = contentCharacters.Count(character => character == '\uFFFD');
        var controlCount = contentCharacters.Count(char.IsControl);
        var printableRatio = contentCharacters.Length == 0 ? 0 : (double)printableCount / contentCharacters.Length;
        var replacementRatio = contentCharacters.Length == 0 ? 0 : (double)replacementCount / contentCharacters.Length;
        var controlRatio = contentCharacters.Length == 0 ? 0 : (double)controlCount / contentCharacters.Length;
        var averageCharactersPerPage = pageCount <= 0 ? 0 : (double)printableCount / pageCount;
        var nonEmptyLines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var repeatedLineRatio = nonEmptyLines.Length == 0
            ? 0
            : (double)(nonEmptyLines.Length - nonEmptyLines.Distinct(StringComparer.Ordinal).Count()) / nonEmptyLines.Length;

        var score = 1.0;
        if (characterCount == 0 || wordCount == 0) score = 0;
        if (wordCount < qualityOptions.MinimumWords) score -= 0.35;
        if (averageCharactersPerPage < qualityOptions.MinimumCharactersPerPage) score -= 0.20;
        if (printableRatio < qualityOptions.MinimumPrintableRatio) score -= 0.20;
        if (replacementRatio > qualityOptions.MaximumReplacementRatio) score -= 0.25;
        if (controlRatio > qualityOptions.MaximumControlRatio) score -= 0.15;
        if (repeatedLineRatio > qualityOptions.MaximumRepeatedLineRatio) score -= 0.10;
        score = Math.Clamp(score, 0, 1);

        if (wordCount < qualityOptions.MinimumWords) warnings.Add("SUSPICIOUSLY_SHORT");
        if (printableRatio < qualityOptions.MinimumPrintableRatio) warnings.Add("LOW_PRINTABLE_RATIO");
        if (replacementRatio > qualityOptions.MaximumReplacementRatio) warnings.Add("REPLACEMENT_CHARACTERS");
        if (controlRatio > qualityOptions.MaximumControlRatio) warnings.Add("CONTROL_CHARACTERS");
        if (repeatedLineRatio > qualityOptions.MaximumRepeatedLineRatio) warnings.Add("REPEATED_LINES");

        var quality = characterCount == 0 || wordCount == 0 || score < qualityOptions.SuspiciousScore
            ? DocumentExtractionQuality.Failed
            : score >= qualityOptions.GoodScore &&
              wordCount >= qualityOptions.MinimumWords &&
              printableRatio >= qualityOptions.MinimumPrintableRatio &&
              replacementRatio <= qualityOptions.MaximumReplacementRatio &&
              controlRatio <= qualityOptions.MaximumControlRatio
                ? DocumentExtractionQuality.Good
                : DocumentExtractionQuality.Suspicious;
        if (quality != DocumentExtractionQuality.Good) warnings.Add("OCR_MAY_BE_REQUIRED");

        return new(
            text,
            pageCount,
            characterCount,
            wordCount,
            method,
            score,
            quality,
            printableRatio,
            replacementRatio,
            controlRatio,
            averageCharactersPerPage,
            repeatedLineRatio,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static int CountWords(string text)
    {
        var count = 0;
        var inWord = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                count++;
                inWord = true;
            }
        }
        return count;
    }

    private static string CollapseWhitespace(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (character is '\0' || char.GetUnicodeCategory(character) == UnicodeCategory.Control)
                continue;
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            builder.Append(character);
            pendingSpace = false;
        }
        return builder.ToString().Trim();
    }

    private static bool IsDecorativeSeparator(string value) =>
        value.Length >= 3 && value.All(character => character is '-' or '_' or '=' or '*' or '•' or '·' or '—' or '–' or '~');

    private static bool IsIsolatedPageNumber(string value) =>
        value.Length <= 3 && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 999;

    private sealed record PositionedWord(string Text, double Left, double Right, double Y)
    {
        public double CenterX => (Left + Right) / 2;
    }

    private sealed record PositionedWordValue(string Text, double Left, double Right, double Y)
    {
        public double CenterX => (Left + Right) / 2;
    }

    private sealed record WordLine(double Y, IReadOnlyList<PositionedWordValue> Words);
}
