using System.Text.Json;

namespace SearchService;

public sealed record SearchIndexCommand(
    Guid EventId,
    Guid CorrelationId,
    Guid DocumentId,
    Guid TaskId,
    string DocumentKind,
    string ArtifactReference)
{
    public static SearchIndexCommand Parse(ReadOnlyMemory<byte> body, string? messageId)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (!string.Equals(ReadString(root, "eventType"), "document.task.requested", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unsupported Search Service command event type.");
        }

        if (!string.Equals(ReadString(root, "taskType"), "search-index", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The task request is not a search-index task.");
        }

        if (ReadInt32(root, "schemaVersion") != 1)
        {
            throw new InvalidDataException("Unsupported Search Service command schema version.");
        }

        var artifactReference = ReadString(root, "inputReference");
        if (!Uri.TryCreate(artifactReference, UriKind.Absolute, out var artifactUri) ||
            artifactUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidDataException("The search-index task is missing a valid artifact URL.");
        }

        return new SearchIndexCommand(
            ReadGuid(root, "eventId") ?? ParseGuid(messageId, "MessageId"),
            RequiredGuid(root, "correlationId"),
            RequiredGuid(root, "documentId"),
            RequiredGuid(root, "taskId"),
            ReadString(root, "documentKind") ?? "Unknown",
            artifactUri.ToString());
    }

    private static Guid RequiredGuid(JsonElement element, string name)
    {
        return ReadGuid(element, name) ?? throw new InvalidDataException($"The search-index task is missing valid {name}.");
    }

    private static Guid? ReadGuid(JsonElement element, string name)
    {
        return Guid.TryParse(ReadString(element, name), out var value) ? value : null;
    }

    private static Guid ParseGuid(string? value, string name)
    {
        return Guid.TryParse(value, out var result) ? result : throw new InvalidDataException($"The search-index task has no valid {name}.");
    }

    private static string? ReadString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
        }

        return null;
    }

    private static int? ReadInt32(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.TryGetInt32(out var value))
            {
                return value;
            }
        }

        return null;
    }
}

public sealed record ExtractionArtifactResponse(
    Guid TaskId,
    Guid CorrelationId,
    Guid DocumentId,
    string DocumentKind,
    string LayoutMarkdown,
    JsonElement Layout,
    JsonElement ExtractionResult,
    int Attempts,
    DateTimeOffset UpdatedAtUtc);

public sealed record SearchFilter(
    string? DocumentKind = null,
    string? EntityName = null,
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    string? Keyword = null);

public sealed record DeterministicSearchRequest(SearchFilter Filter, int Limit = 50);

public sealed record HybridSearchRequest(
    string SemanticQuery,
    SearchFilter Filter,
    int Limit = 10);

public sealed record AgenticSearchRequest(string Question);

public sealed record AgenticSearchResponse(string Answer, IReadOnlyList<SearchResult> Sources);

public sealed record SearchCitation(Guid DocumentId, string SourceUrl, int PageNumber, JsonElement? BoundingBox);

public sealed record SearchResult(
    Guid DocumentId,
    string DocumentKind,
    string EntityName,
    DateOnly? DocumentDate,
    string Text,
    double? Score,
    SearchCitation Citation);

public static class SearchDocumentKinds
{
    public static IReadOnlySet<string> Supported { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Invoice",
        "Contract",
        "IdentityDocument",
        "FinancialStatement",
        "Pdf",
        "Image",
        "Spreadsheet",
        "Text",
        "Unknown"
    };
}