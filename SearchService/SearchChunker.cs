using System.Text.Json;
using System.Text.RegularExpressions;

namespace SearchService;

public sealed record SearchChunk(int ChunkIndex, string Text, int PageNumber, JsonElement? BoundingBox);

public static partial class SearchChunker
{
    private const int MaximumChunkCharacters = 1800;

    public static IReadOnlyList<SearchChunk> Create(ExtractionArtifactResponse artifact)
    {
        var paragraphs = GetParagraphs(artifact.Layout);
        if (paragraphs.Count > 0)
        {
            return SplitParagraphs(paragraphs);
        }

        return SplitMarkdown(artifact.LayoutMarkdown, artifact.Layout);
    }

    private static IReadOnlyList<SearchChunk> SplitParagraphs(IReadOnlyList<(string Text, int PageNumber, JsonElement? BoundingBox)> paragraphs)
    {
        var chunks = new List<SearchChunk>();
        foreach (var paragraph in paragraphs)
        {
            var text = paragraph.Text.Trim();
            for (var offset = 0; offset < text.Length; offset += MaximumChunkCharacters)
            {
                var length = Math.Min(MaximumChunkCharacters, text.Length - offset);
                var part = text.Substring(offset, length).Trim();
                if (part.Length > 0)
                {
                    chunks.Add(new SearchChunk(chunks.Count, part, paragraph.PageNumber, paragraph.BoundingBox));
                }
            }
        }

        return chunks;
    }

    private static IReadOnlyList<SearchChunk> SplitMarkdown(string markdown, JsonElement layout)
    {
        var pageTexts = PageBreakRegex().Split(markdown);
        var pages = GetPages(layout);
        var chunks = new List<SearchChunk>();
        for (var pageIndex = 0; pageIndex < pageTexts.Length; pageIndex++)
        {
            var paragraphs = pageTexts[pageIndex].Split(["\r\n\r\n", "\n\n", "\f"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var paragraph in paragraphs)
            {
                for (var offset = 0; offset < paragraph.Length; offset += MaximumChunkCharacters)
                {
                    var length = Math.Min(MaximumChunkCharacters, paragraph.Length - offset);
                    var text = paragraph.Substring(offset, length).Trim();
                    if (text.Length > 0)
                    {
                        chunks.Add(new SearchChunk(chunks.Count, text, pageIndex + 1, pages.GetValueOrDefault(pageIndex + 1)));
                    }
                }
            }
        }

        return chunks;
    }

    private static IReadOnlyList<(string Text, int PageNumber, JsonElement? BoundingBox)> GetParagraphs(JsonElement layout)
    {
        var analyzeResult = GetAnalyzeResult(layout);
        if (analyzeResult.ValueKind != JsonValueKind.Object ||
            !analyzeResult.TryGetProperty("paragraphs", out var paragraphs) || paragraphs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<(string Text, int PageNumber, JsonElement? BoundingBox)>();
        foreach (var paragraph in paragraphs.EnumerateArray())
        {
            if (!paragraph.TryGetProperty("content", out var content) || string.IsNullOrWhiteSpace(content.GetString()))
            {
                continue;
            }

            var pageNumber = 1;
            JsonElement? boundingBox = null;
            if (paragraph.TryGetProperty("boundingRegions", out var regions) && regions.ValueKind == JsonValueKind.Array)
            {
                var region = regions.EnumerateArray().FirstOrDefault();
                if (region.ValueKind == JsonValueKind.Object)
                {
                    if (region.TryGetProperty("pageNumber", out var page) && page.TryGetInt32(out var parsedPage))
                    {
                        pageNumber = parsedPage;
                    }

                    if (region.TryGetProperty("polygon", out var polygon))
                    {
                        boundingBox = polygon.Clone();
                    }
                }
            }

            results.Add((content.GetString()!, pageNumber, boundingBox));
        }

        return results;
    }

    private static Dictionary<int, JsonElement?> GetPages(JsonElement layout)
    {
        var pages = new Dictionary<int, JsonElement?>();
        var analyzeResult = GetAnalyzeResult(layout);
        if (!analyzeResult.TryGetProperty("pages", out var pageArray) || pageArray.ValueKind != JsonValueKind.Array)
        {
            return pages;
        }

        foreach (var page in pageArray.EnumerateArray())
        {
            if (!page.TryGetProperty("pageNumber", out var pageNumber) || !pageNumber.TryGetInt32(out var number))
            {
                continue;
            }

            if (page.TryGetProperty("width", out var width) && page.TryGetProperty("height", out var height) &&
                width.TryGetDouble(out var w) && height.TryGetDouble(out var h))
            {
                using var bounds = JsonDocument.Parse(JsonSerializer.Serialize(new[] { 0, 0, w, 0, w, h, 0, h }));
                pages[number] = bounds.RootElement.Clone();
            }
        }

        return pages;
    }

    private static JsonElement GetAnalyzeResult(JsonElement layout)
    {
        return layout.TryGetProperty("analyzeResult", out var analyzeResult) ? analyzeResult : layout;
    }

    [GeneratedRegex("<!--\\s*PageBreak\\s*-->|\\f", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageBreakRegex();
}