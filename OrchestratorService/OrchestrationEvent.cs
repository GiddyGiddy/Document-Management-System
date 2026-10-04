using System.Text.Json;

namespace OrchestratorService;

public sealed record IngestedDocument(
    Guid DocumentId,
    string OriginalFileName,
    string ContentType,
    long SizeBytes);

public sealed record OrchestrationEvent(
    Guid EventId,
    Guid CorrelationId,
    string EventType,
    int SchemaVersion,
    IReadOnlyList<IngestedDocument> Documents,
    Guid? DocumentId,
    Guid? TaskId,
    string? TaskType,
    string? OutputReference,
    string? FailureSummary)
{
    public static OrchestrationEvent Parse(ReadOnlyMemory<byte> body, string? messageId, string? messageType)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var eventId = ReadGuid(root, "eventId") ?? ParseGuid(messageId, "MessageId");
        var correlationId = ReadGuid(root, "correlation_id") ??
            ReadGuid(root, "correlationId") ??
            throw new InvalidDataException("The event is missing a correlation ID.");
        var eventType = ReadString(root, "eventType") ?? messageType ??
            throw new InvalidDataException("The event is missing an event type.");
        var schemaVersion = ReadInt32(root, "schemaVersion") ??
            throw new InvalidDataException("The event is missing schemaVersion.");
        if (schemaVersion != 1)
        {
            throw new InvalidDataException($"Event schema version {schemaVersion} is not supported.");
        }

        var documents = new List<IngestedDocument>();
        if (TryGetProperty(root, "documents", out var documentsElement) && documentsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var document in documentsElement.EnumerateArray())
            {
                documents.Add(ParseDocument(document));
            }
        }
        else if (eventType == "document.ingested")
        {
            documents.Add(ParseDocument(root));
        }

        return new OrchestrationEvent(
            eventId,
            correlationId,
            eventType,
            schemaVersion,
            documents,
            ReadGuid(root, "documentId"),
            ReadGuid(root, "taskId"),
            ReadString(root, "taskType"),
            ReadString(root, "outputReference"),
            ReadString(root, "failureSummary"));
    }

    private static IngestedDocument ParseDocument(JsonElement document)
    {
        var documentId = ReadGuid(document, "documentId") ??
            throw new InvalidDataException("An ingested document is missing its document ID.");
        var originalFileName = ReadString(document, "originalFileName") ??
            throw new InvalidDataException("An ingested document is missing its original file name.");
        var contentType = ReadString(document, "contentType") ?? "application/octet-stream";
        var sizeBytes = TryGetProperty(document, "sizeBytes", out var sizeElement) && sizeElement.TryGetInt64(out var size)
            ? size
            : 0;

        return new IngestedDocument(documentId, originalFileName, contentType, sizeBytes);
    }

    private static Guid? ReadGuid(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        return Guid.TryParse(value, out var result) ? result : null;
    }

    private static Guid ParseGuid(string? value, string fieldName)
    {
        return Guid.TryParse(value, out var result)
            ? result
            : throw new InvalidDataException($"The event has no valid {fieldName}.");
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? ReadInt32(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}