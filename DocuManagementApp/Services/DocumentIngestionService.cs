using System.Text.Json;
using DocuManagementApp.Data;
using DocuManagementApp.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuManagementApp.Services;

public sealed record IngestionDocumentInput(
    string OriginalFileName,
    byte[] Content,
    string? ContentType,
    DocumentOutputFormat OutputFormat);

public sealed record DocumentIngestionResult(
    Guid DocumentId,
    IReadOnlyList<StoredDocumentResult> Documents,
    Guid EventId,
    DocumentProcessingStatus ProcessingStatus,
    DocumentOutputFormat RequestedOutputFormat);

public interface IDocumentIngestionService
{
    Task<DocumentIngestionResult> IngestAsync(
        Guid? sourceDocumentId,
        IReadOnlyList<IngestionDocumentInput> documents,
        DocumentOutputFormat requestedOutputFormat,
        CancellationToken cancellationToken);
}

public sealed class DocumentIngestionService : IDocumentIngestionService
{
    private const string EventType = "document.ingested";
    private const int EventSchemaVersion = 1;
    private readonly AppDbContext _dbContext;

    public DocumentIngestionService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<DocumentIngestionResult> IngestAsync(
        Guid? sourceDocumentId,
        IReadOnlyList<IngestionDocumentInput> documents,
        DocumentOutputFormat requestedOutputFormat,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0)
        {
            throw new ArgumentException("At least one document must be ingested.", nameof(documents));
        }

        if (documents.Any(document => string.IsNullOrWhiteSpace(document.OriginalFileName) || document.Content.Length == 0))
        {
            throw new ArgumentException("Each ingested document must have a file name and non-empty content.", nameof(documents));
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        DocumentRecord? sourceDocument = null;
        if (sourceDocumentId.HasValue)
        {
            sourceDocument = await _dbContext.Documents
                .SingleOrDefaultAsync(document => document.Id == sourceDocumentId.Value, cancellationToken);
            if (sourceDocument is null)
            {
                throw new InvalidOperationException($"Source document '{sourceDocumentId}' was not found.");
            }

            sourceDocument.ProcessingStatus = DocumentProcessingStatus.Completed;
            sourceDocument.RequestedOutputFormat = requestedOutputFormat;
            sourceDocument.ProcessingCompletedAtUtc = now;
            sourceDocument.FailureSummary = null;
        }

        var documentRecords = documents.Select(input => new DocumentRecord
        {
            OriginalFileName = Path.GetFileName(input.OriginalFileName),
            ContentType = string.IsNullOrWhiteSpace(input.ContentType)
                ? DocumentStorageService.ResolveContentTypeForExtension(input.OriginalFileName)
                : input.ContentType,
            FileContent = input.Content,
            SizeBytes = input.Content.LongLength,
            UploadedAtUtc = now,
            ProcessingStatus = DocumentProcessingStatus.Completed,
            RequestedOutputFormat = requestedOutputFormat,
            ProcessingCompletedAtUtc = now
        }).ToArray();

        var documentId = sourceDocument?.Id ?? documentRecords[0].Id;
        var eventId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            eventId,
            eventType = EventType,
            schemaVersion = EventSchemaVersion,
            documentId,
            requestedOutputFormat = requestedOutputFormat.ToString(),
            occurredAtUtc = now,
            documents = documentRecords.Select((record, index) => new
            {
                documentId = record.Id,
                record.OriginalFileName,
                record.ContentType,
                record.SizeBytes,
                outputFormat = documents[index].OutputFormat.ToString()
            })
        });

        _dbContext.Documents.AddRange(documentRecords);
        _dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = eventId,
            DocumentId = documentId,
            EventType = EventType,
            SchemaVersion = EventSchemaVersion,
            Payload = payload,
            CreatedAtUtc = now,
            AttemptCount = 0
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var storedDocuments = documentRecords.Select(record => new StoredDocumentResult
        {
            Id = record.Id,
            OriginalFileName = record.OriginalFileName,
            Size = record.SizeBytes
        }).ToArray();

        return new DocumentIngestionResult(
            documentId,
            storedDocuments,
            eventId,
            DocumentProcessingStatus.Completed,
            requestedOutputFormat);
    }
}