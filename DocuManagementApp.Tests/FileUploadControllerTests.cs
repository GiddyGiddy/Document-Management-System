using DocuManagementApp.Controllers;
using DocuManagementApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocuManagementApp.Tests;

public sealed class FileUploadControllerTests
{
    private readonly FakeDocumentStorageService _storage = new();
    private readonly FakeOfficeToPdfConversionService _conversion = new();
    private readonly FileUploadController _controller;

    public FileUploadControllerTests()
    {
        _controller = new FileUploadController(
            NullLogger<FileUploadController>.Instance,
            _storage,
            _conversion,
            new PdfADocumentService());
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
    }

    [Fact]
    public async Task ConvertToPdfReturnsNotFoundForUnknownDocument()
    {
        var result = await _controller.ConvertToPdf(Guid.NewGuid(), false, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(0, _conversion.ConvertCallCount);
    }

    [Fact]
    public async Task ConvertToPdfRejectsUnsupportedDocumentType()
    {
        var documentId = Guid.NewGuid();
        _storage.AddDocument(documentId, "report.pdf", [1, 2, 3]);

        var result = await _controller.ConvertToPdf(documentId, false, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(0, _conversion.ConvertCallCount);
        Assert.Empty(_storage.SavedDocuments);
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
        _conversion.PdfContent = "%PDF-1.7\nnot a PDF/A file"u8.ToArray();

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
    }

    private sealed class FakeDocumentStorageService : IDocumentStorageService
    {
        private readonly Dictionary<Guid, DocumentDownloadResult> _documents = [];

        public List<SavedDocument> SavedDocuments { get; } = [];

        public void AddDocument(Guid id, string fileName, byte[] content)
        {
            _documents[id] = new DocumentDownloadResult
            {
                Id = id,
                OriginalFileName = fileName,
                ContentType = "application/octet-stream",
                Content = content
            };
        }

        public Task<StoredDocumentResult> SaveDocumentAsync(
            string originalFileName,
            byte[] content,
            string? contentType,
            CancellationToken cancellationToken)
        {
            SavedDocuments.Add(new SavedDocument(originalFileName, content, contentType));
            return Task.FromResult(new StoredDocumentResult
            {
                Id = Guid.NewGuid(),
                OriginalFileName = originalFileName,
                Size = content.Length
            });
        }

        public Task<IReadOnlyList<DocumentListItem>> GetDocumentsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<DocumentListItem>>([]);
        }

        public Task<DocumentDownloadResult?> GetDocumentByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            _documents.TryGetValue(id, out var document);
            return Task.FromResult(document);
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

    private sealed record SavedDocument(string FileName, byte[] Content, string? ContentType);
}