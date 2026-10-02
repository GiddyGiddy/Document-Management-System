using DocuManagementApp.Controllers;
using DocuManagementApp.Models;
using DocuManagementApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocuManagementApp.Tests;

public sealed class FileUploadControllerTests
{
    private readonly FakeDocumentStorageService _storage = new();
    private readonly FakeDocumentIngestionService _ingestion;
    private readonly FakeOfficeToPdfConversionService _conversion = new();
    private readonly FakePdfAProcessingService _pdfa = new();
    private readonly FileUploadController _controller;

    public FileUploadControllerTests()
    {
        _ingestion = new FakeDocumentIngestionService(_storage);
        _controller = new FileUploadController(
            NullLogger<FileUploadController>.Instance,
            _storage,
            _ingestion,
            _conversion,
            _pdfa);
    }

    [Fact]
    public async Task UploadFileRejectsMissingPayload()
    {
        var result = await _controller.UploadFile(null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_storage.SavedDocuments);
    }

    [Fact]
    public async Task UploadFileRejectsInvalidBase64()
    {
        var result = await _controller.UploadFile(
            new UploadFileRequest { FileName = "report.txt", ContentBase64 = "%%%" },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_storage.SavedDocuments);
    }

    [Fact]
    public async Task UploadFileRejectsEmptyContent()
    {
        var result = await _controller.UploadFile(
            new UploadFileRequest { FileName = "report.txt", ContentBase64 = string.Empty },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_storage.SavedDocuments);
    }

    [Fact]
    public async Task UploadFileSavesDecodedContentAndSanitizedFileName()
    {
        var result = await _controller.UploadFile(
            new UploadFileRequest
            {
                FileName = Path.Combine("private", "report.txt"),
                ContentBase64 = Convert.ToBase64String([1, 2, 3]),
                ContentType = "text/plain"
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = Assert.Single(_storage.SavedDocuments);
        Assert.Equal("report.txt", saved.FileName);
        Assert.Equal(new byte[] { 1, 2, 3 }, saved.Content);
        Assert.Equal("text/plain", saved.ContentType);
        var listedDocument = Assert.Single(_storage.Documents);
        Assert.Equal(DocumentProcessingStatus.Completed.ToString(), listedDocument.ProcessingStatus);
        Assert.Equal(DocumentOutputFormat.Original.ToString(), listedDocument.RequestedOutputFormat);
        Assert.Equal("document.ingested", Assert.Single(_ingestion.OutboxMessages).EventType);
        Assert.Equal(listedDocument.CorrelationId, Assert.Single(_ingestion.OutboxMessages).CorrelationId);
    }

    [Fact]
    public async Task UploadWithPdfARequestIngestsOriginalRenditionAndOutboxTogether()
    {
        var result = await _controller.UploadFile(
            new UploadFileRequest
            {
                FileName = "report.pdf",
                ContentBase64 = Convert.ToBase64String([1, 2, 3]),
                ContentType = "application/pdf",
                ArchiveAsPdfA = true
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(2, _storage.SavedDocuments.Count);
        Assert.Contains(_storage.SavedDocuments, document => document.FileName == "report.pdf");
        Assert.Contains(_storage.SavedDocuments, document => document.FileName == "report.pdfa.pdf");
        var outbox = Assert.Single(_ingestion.OutboxMessages);
        Assert.Equal("document.ingested", outbox.EventType);
        Assert.Equal(1, outbox.SchemaVersion);
        Assert.NotEqual(Guid.Empty, outbox.CorrelationId);
        Assert.Contains($"\"correlation_id\":\"{outbox.CorrelationId:D}\"", outbox.Payload);
        Assert.All(_storage.Documents, item => Assert.Equal(outbox.CorrelationId, item.CorrelationId));
        Assert.Contains("PdfA", outbox.Payload);
        Assert.Contains("report.pdfa.pdf", outbox.Payload);
        Assert.Equal(0, outbox.AttemptCount);
        Assert.Null(outbox.PublishedAtUtc);
        Assert.Equal(_storage.Documents[0].StoredFileName, outbox.DocumentId.ToString());
    }

    [Fact]
    public async Task ConvertToPdfReturnsNotFoundForUnknownDocument()
    {
        var result = await _controller.ConvertToPdf(Guid.NewGuid(), false, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(0, _conversion.ConvertCallCount);
    }

    [Fact]
    public async Task ConvertToPdfRejectsPdfWhenPdfAWasNotRequested()
    {
        var documentId = Guid.NewGuid();
        _storage.AddDocument(documentId, "report.pdf", [1, 2, 3]);

        var result = await _controller.ConvertToPdf(documentId, false, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(0, _conversion.ConvertCallCount);
        Assert.Empty(_storage.SavedDocuments);
    }

    [Fact]
    public async Task ConvertToPdfAConvertsUploadedPdfDirectlyAndStoresPdfaCopy()
    {
        var documentId = Guid.NewGuid();
        _storage.AddDocument(documentId, "report.pdf", [1, 2, 3]);
        _pdfa.PdfAContent = [7, 8, 9];

        var result = await _controller.ConvertToPdf(documentId, true, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, _conversion.ConvertCallCount);
        var converted = Assert.Single(_storage.SavedDocuments);
        Assert.Equal("report.pdfa.pdf", converted.FileName);
        Assert.Equal("application/pdf", converted.ContentType);
        Assert.Equal(new byte[] { 7, 8, 9 }, converted.Content);

        var filesResult = Assert.IsType<OkObjectResult>(await _controller.GetUploadedFiles(CancellationToken.None));
        var files = Assert.IsAssignableFrom<IReadOnlyList<DocumentListItem>>(filesResult.Value);
        Assert.Contains(files, file => file.OriginalFileName == "report.pdf");
        Assert.Contains(files, file => file.OriginalFileName == "report.pdfa.pdf");
    }

    [Fact]
    public async Task ConvertToPdfReturnsServerErrorWhenOfficeConversionFails()
    {
        var documentId = Guid.NewGuid();
        _storage.AddDocument(documentId, "report.docx", [1, 2, 3]);
        _conversion.Result = new OfficeConversionResult { Success = false, ErrorMessage = "Conversion failed." };

        var result = await _controller.ConvertToPdf(documentId, false, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, status.StatusCode);
        Assert.Empty(_storage.SavedDocuments);
    }

    [Fact]
    public async Task ConvertToPdfARejectsPdfThatFailsConformanceChecks()
    {
        var documentId = Guid.NewGuid();
        _storage.AddDocument(documentId, "report.docx", [1, 2, 3]);
        _pdfa.Result = new PdfAValidationResult(false, "veraPDF reports non-compliant output.");

        var result = await _controller.ConvertToPdf(documentId, true, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, status.StatusCode);
        Assert.Empty(_storage.SavedDocuments);
    }

    [Fact]
    public async Task ConvertToPdfStoresConvertedPdfAsNewDocument()
    {
        var documentId = Guid.NewGuid();
        _storage.AddDocument(documentId, "report.docx", [1, 2, 3]);
        _conversion.PdfContent = [9, 8, 7];

        var result = await _controller.ConvertToPdf(documentId, false, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var converted = Assert.Single(_storage.SavedDocuments);
        Assert.Equal("report.pdf", converted.FileName);
        Assert.Equal("application/pdf", converted.ContentType);
        Assert.Equal(new byte[] { 9, 8, 7 }, converted.Content);
        var pdfListItem = Assert.Single(_storage.Documents.Where(file => file.OriginalFileName == "report.pdf"));
        Assert.Equal(DocumentProcessingStatus.Completed.ToString(), pdfListItem.ProcessingStatus);
        Assert.Equal(DocumentOutputFormat.Pdf.ToString(), pdfListItem.RequestedOutputFormat);

        var filesResult = Assert.IsType<OkObjectResult>(await _controller.GetUploadedFiles(CancellationToken.None));
        var files = Assert.IsAssignableFrom<IReadOnlyList<DocumentListItem>>(filesResult.Value);
        Assert.Contains(files, file => file.OriginalFileName == "report.pdf");
    }

    [Fact]
    public async Task ConvertToPdfAStoresValidatedRenditionAndListsItSeparately()
    {
        var documentId = Guid.NewGuid();
        _storage.AddDocument(documentId, "report.docx", [1, 2, 3]);
        _pdfa.PdfAContent = [7, 7, 7];

        var result = await _controller.ConvertToPdf(documentId, true, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var converted = Assert.Single(_storage.SavedDocuments);
        Assert.Equal("report.pdfa.pdf", converted.FileName);
        Assert.Equal("application/pdf", converted.ContentType);
        Assert.Equal(new byte[] { 7, 7, 7 }, converted.Content);
        var pdfaListItem = Assert.Single(_storage.Documents.Where(file => file.OriginalFileName == "report.pdfa.pdf"));
        Assert.Equal(DocumentProcessingStatus.Completed.ToString(), pdfaListItem.ProcessingStatus);
        Assert.Equal(DocumentOutputFormat.PdfA.ToString(), pdfaListItem.RequestedOutputFormat);

        var filesResult = Assert.IsType<OkObjectResult>(await _controller.GetUploadedFiles(CancellationToken.None));
        var files = Assert.IsAssignableFrom<IReadOnlyList<DocumentListItem>>(filesResult.Value);
        Assert.Contains(files, file => file.OriginalFileName == "report.docx");
        Assert.Contains(files, file => file.OriginalFileName == "report.pdfa.pdf");
    }

    private sealed class FakeDocumentStorageService : IDocumentStorageService
    {
        private readonly Dictionary<Guid, DocumentDownloadResult> _documents = [];

        public List<SavedDocument> SavedDocuments { get; } = [];
        public List<DocumentListItem> Documents { get; } = [];

        public void AddDocument(Guid id, string fileName, byte[] content)
        {
            var correlationId = Guid.NewGuid();
            AddStoredDocument(id, fileName, content, DocumentOutputFormat.Original, correlationId: correlationId);
            _documents[id] = new DocumentDownloadResult
            {
                Id = id,
                CorrelationId = correlationId,
                OriginalFileName = fileName,
                ContentType = "application/octet-stream",
                Content = content
            };
        }

        public StoredDocumentResult AddStoredDocument(
            Guid id,
            string fileName,
            byte[] content,
            DocumentOutputFormat requestedOutputFormat,
            string? contentType = null,
            Guid? correlationId = null)
        {
            var globalCorrelationId = correlationId ?? Guid.NewGuid();
            Documents.Add(new DocumentListItem
            {
                StoredFileName = id.ToString(),
                CorrelationId = globalCorrelationId,
                OriginalFileName = fileName,
                Size = content.Length,
                UploadedAt = DateTimeOffset.UtcNow,
                ProcessingStatus = DocumentProcessingStatus.Completed.ToString(),
                RequestedOutputFormat = requestedOutputFormat.ToString(),
                ProcessingCompletedAt = DateTimeOffset.UtcNow
            });
            _documents[id] = new DocumentDownloadResult
            {
                Id = id,
                CorrelationId = globalCorrelationId,
                OriginalFileName = fileName,
                ContentType = contentType ?? DocumentStorageService.ResolveContentTypeForExtension(fileName),
                Content = content
            };
            return new StoredDocumentResult { Id = id, CorrelationId = globalCorrelationId, OriginalFileName = fileName, Size = content.Length };
        }

        public void AddIngestedDocument(StoredDocumentResult result, byte[] content, string? contentType)
        {
            SavedDocuments.Add(new SavedDocument(result.OriginalFileName, content, contentType));
            _documents[result.Id].ContentType = contentType ?? "application/octet-stream";
        }

        public bool ContainsDocument(Guid id) => _documents.ContainsKey(id);
        public Guid GetCorrelationId(Guid id) => _documents[id].CorrelationId;

        public Task<IReadOnlyList<DocumentListItem>> GetDocumentsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<DocumentListItem>>(Documents.ToArray());
        }

        public Task<DocumentDownloadResult?> GetDocumentByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            _documents.TryGetValue(id, out var document);
            return Task.FromResult(document);
        }

    }

    private sealed class FakeDocumentIngestionService(FakeDocumentStorageService storage) : IDocumentIngestionService
    {
        public List<OutboxMessage> OutboxMessages { get; } = [];

        public Task<DocumentIngestionResult> IngestAsync(
            Guid? sourceDocumentId,
            IReadOnlyList<IngestionDocumentInput> documents,
            DocumentOutputFormat requestedOutputFormat,
            CancellationToken cancellationToken)
        {
            if (sourceDocumentId.HasValue && !storage.ContainsDocument(sourceDocumentId.Value))
            {
                throw new InvalidOperationException("Source document not found.");
            }

            var correlationId = sourceDocumentId.HasValue
                ? storage.GetCorrelationId(sourceDocumentId.Value)
                : Guid.NewGuid();
            var results = documents.Select(document =>
            {
                var saved = storage.AddStoredDocument(
                    Guid.NewGuid(),
                    document.OriginalFileName,
                    document.Content,
                    requestedOutputFormat,
                    document.ContentType,
                    correlationId);
                storage.AddIngestedDocument(saved, document.Content, document.ContentType);
                return saved;
            }).ToArray();

            var documentId = sourceDocumentId ?? results[0].Id;
            var eventId = Guid.NewGuid();
            OutboxMessages.Add(new OutboxMessage
            {
                Id = eventId,
                CorrelationId = correlationId,
                DocumentId = documentId,
                EventType = "document.ingested",
                SchemaVersion = 1,
                Payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    eventId,
                    eventType = "document.ingested",
                    schemaVersion = 1,
                    correlation_id = correlationId,
                    documentId,
                    processingStatus = DocumentProcessingStatus.Completed.ToString(),
                    requestedOutputFormat = requestedOutputFormat.ToString(),
                    documents = results.Select(result => new { documentId = result.Id, result.OriginalFileName })
                }),
                CreatedAtUtc = DateTimeOffset.UtcNow
            });

            return Task.FromResult(new DocumentIngestionResult(
                documentId,
                correlationId,
                results,
                eventId,
                DocumentProcessingStatus.Completed,
                requestedOutputFormat));
        }
    }

    private sealed class FakeOfficeToPdfConversionService : IOfficeToPdfConversionService
    {
        public OfficeConversionResult Result { get; set; } = new() { Success = true };
        public byte[] PdfContent { get; set; } = [4, 5, 6];
        public int ConvertCallCount { get; private set; }

        public bool IsSupportedExtension(string fileName)
        {
            return new[] { ".doc", ".docx", ".rtf", ".odt", ".xls", ".xlsx", ".ods" }
                .Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);
        }

        public async Task<OfficeConversionResult> ConvertToPdfAsync(
            string sourceFilePath,
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            ConvertCallCount++;
            if (!Result.Success)
            {
                return Result;
            }

            var outputPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(sourceFilePath) + ".pdf");
            await File.WriteAllBytesAsync(outputPath, PdfContent, cancellationToken);
            return new OfficeConversionResult { Success = true, OutputPdfPath = outputPath };
        }
    }

    private sealed class FakePdfAProcessingService : IPdfAProcessingService
    {
        public PdfAValidationResult Result { get; set; } = new(true, "Conformance: PASS");
        public byte[] PdfAContent { get; set; } = [4, 5, 6];

        public async Task<PdfAValidationResult> ConvertToPdfAAsync(string sourcePdfPath, string outputPdfPath, CancellationToken cancellationToken)
        {
            if (Result.IsCompliant)
            {
                await File.WriteAllBytesAsync(outputPdfPath, PdfAContent, cancellationToken);
            }
            return Result;
        }

        public Task<PdfAValidationResult> ValidateAsync(string pdfPath, CancellationToken cancellationToken)
        {
            return Task.FromResult(Result);
        }
    }

    private sealed record SavedDocument(string FileName, byte[] Content, string? ContentType);
}