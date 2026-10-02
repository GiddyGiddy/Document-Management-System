using Microsoft.AspNetCore.Mvc;
using DocuManagementApp.Services;
using DocuManagementApp.Models;
using System.IO;

namespace DocuManagementApp.Controllers
{
  public sealed class UploadFileRequest
  {
    public string? FileName { get; set; }
    public string? ContentBase64 { get; set; }
    public string? ContentType { get; set; }
    public bool ArchiveAsPdfA { get; set; }
  }

  [ApiController]
  [Route("api/fileupload")]
  public class FileUploadController : ControllerBase
  {
    private readonly ILogger<FileUploadController> _logger;
    private readonly IDocumentStorageService _documentStorageService;
    private readonly IDocumentIngestionService _documentIngestionService;
    private readonly IOfficeToPdfConversionService _officeConversionService;
    private readonly IPdfAProcessingService _pdfaService;

    public FileUploadController(
      ILogger<FileUploadController> logger,
      IDocumentStorageService documentStorageService,
      IDocumentIngestionService documentIngestionService,
      IOfficeToPdfConversionService officeConversionService,
      IPdfAProcessingService pdfaService)
    {
      _logger = logger;
      _documentStorageService = documentStorageService;
      _documentIngestionService = documentIngestionService;
      _officeConversionService = officeConversionService;
      _pdfaService = pdfaService;
      _logger.LogInformation("FileUploadController instantiated.");
    }

    [HttpPost("upload")]
    [Consumes("application/json")]
    [RequestSizeLimit(30 * 1024 * 1024)]
    public async Task<IActionResult> UploadFile([FromBody] UploadFileRequest? request, CancellationToken cancellationToken)
    {
      _logger.LogInformation("Upload request received at {Time}.", DateTime.Now);

      if (request is null || string.IsNullOrWhiteSpace(request.FileName) || string.IsNullOrWhiteSpace(request.ContentBase64))
      {
        _logger.LogWarning("Upload rejected: missing file name or content.");
        return BadRequest(new { message = "No file payload was provided." });
      }

      _logger.LogInformation("Processing file '{FileName}', base64 length: {Length} chars.", request.FileName, request.ContentBase64.Length);

      byte[] fileBytes;
      try
      {
        fileBytes = Convert.FromBase64String(request.ContentBase64);
      }
      catch (FormatException ex)
      {
        _logger.LogError(ex, "Failed to decode base64 content for file '{FileName}'.", request.FileName);
        return BadRequest(new { message = "Invalid base64 file content." });
      }

      if (fileBytes.Length == 0)
      {
        _logger.LogWarning("Upload rejected: decoded file content is empty for '{FileName}'.", request.FileName);
        return BadRequest(new { message = "Empty file content." });
      }

      var safeFileName = Path.GetFileName(request.FileName);
      var sourceIsPdf = string.Equals(Path.GetExtension(safeFileName), ".pdf", StringComparison.OrdinalIgnoreCase);
      var documentsToIngest = new List<IngestionDocumentInput>
      {
        new(safeFileName, fileBytes, request.ContentType, DocumentOutputFormat.Original)
      };
      var requestedFormat = request.ArchiveAsPdfA ? DocumentOutputFormat.PdfA : DocumentOutputFormat.Original;
      string? conversionDirectory = null;

      try
      {
        if (request.ArchiveAsPdfA)
        {
          if (!sourceIsPdf && !_officeConversionService.IsSupportedExtension(safeFileName))
          {
            return BadRequest(new { message = "PDF/A archiving is supported for PDF, Word, Excel, and OpenDocument files." });
          }

          conversionDirectory = Path.Combine(Path.GetTempPath(), "document-ingestion", Guid.NewGuid().ToString("N"));
          Directory.CreateDirectory(conversionDirectory);
          var sourcePath = Path.Combine(conversionDirectory, safeFileName);
          await System.IO.File.WriteAllBytesAsync(sourcePath, fileBytes, cancellationToken);

          string pdfPath;
          if (sourceIsPdf)
          {
            pdfPath = sourcePath;
          }
          else
          {
            var pdfResult = await _officeConversionService.ConvertToPdfAsync(sourcePath, conversionDirectory, cancellationToken);
            if (!pdfResult.Success || pdfResult.OutputPdfPath is null)
            {
              _logger.LogError("Office conversion failed for uploaded file '{FileName}': {Error}", safeFileName, pdfResult.ErrorMessage);
              return StatusCode(StatusCodes.Status500InternalServerError, new { message = pdfResult.ErrorMessage ?? "Office-to-PDF conversion failed." });
            }

            pdfPath = pdfResult.OutputPdfPath;
          }

          var pdfaPath = Path.Combine(conversionDirectory, Path.GetFileNameWithoutExtension(safeFileName) + ".pdfa.pdf");
          var pdfaResult = await _pdfaService.ConvertToPdfAAsync(pdfPath, pdfaPath, cancellationToken);
          if (!pdfaResult.IsCompliant)
          {
            _logger.LogError("PDF/A conversion failed for uploaded file '{FileName}': {Report}", safeFileName, pdfaResult.Report);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "PDF/A conversion or validation failed.", report = pdfaResult.Report });
          }

          var pdfaBytes = await System.IO.File.ReadAllBytesAsync(pdfaPath, cancellationToken);
          documentsToIngest.Add(new IngestionDocumentInput(
            Path.GetFileName(pdfaPath),
            pdfaBytes,
            "application/pdf",
            DocumentOutputFormat.PdfA));
        }

        var ingestion = await _documentIngestionService.IngestAsync(
          null,
          documentsToIngest,
          requestedFormat,
          cancellationToken);
        var savedDocument = ingestion.Documents[0];

        _logger.LogInformation(
          "Ingested document '{DocumentId}' and {RenditionCount} rendition(s); outbox event '{EventId}' committed.",
          ingestion.DocumentId,
          ingestion.Documents.Count - 1,
          ingestion.EventId);

        return Ok(new
        {
          message = "File uploaded successfully.",
          id = savedDocument.Id,
          correlationId = ingestion.CorrelationId,
          originalFileName = savedDocument.OriginalFileName,
          storedFileName = savedDocument.Id.ToString(),
          size = savedDocument.Size,
          processingStatus = ingestion.ProcessingStatus.ToString(),
          requestedOutputFormat = ingestion.RequestedOutputFormat.ToString(),
          eventId = ingestion.EventId,
          renditions = ingestion.Documents.Skip(1).Select(rendition => new
          {
            id = rendition.Id,
            originalFileName = rendition.OriginalFileName,
            storedFileName = rendition.Id.ToString(),
            size = rendition.Size
          })
        });
      }
      finally
      {
        if (conversionDirectory is not null)
        {
          TryDeleteDirectory(conversionDirectory);
        }
      }
    }

    [HttpGet("files")]
    public async Task<IActionResult> GetUploadedFiles(CancellationToken cancellationToken)
    {
      var files = await _documentStorageService.GetDocumentsAsync(cancellationToken);
      return Ok(files);
    }

    [HttpGet("download/{id:guid}")]
    public async Task<IActionResult> DownloadFile([FromRoute] Guid id, CancellationToken cancellationToken)
    {
      var document = await _documentStorageService.GetDocumentByIdAsync(id, cancellationToken);
      if (document is null)
      {
        return NotFound(new { message = "File not found." });
      }

      var stream = new MemoryStream(document.Content);
      var contentType = document.ContentType == "application/octet-stream"
        ? DocumentStorageService.ResolveContentTypeForExtension(document.OriginalFileName)
        : document.ContentType;

      Response.Headers.ContentDisposition = $"inline; filename=\"{document.OriginalFileName}\"";
      return File(stream, contentType, enableRangeProcessing: true);
    }

    [HttpPost("{id:guid}/convert-to-pdf")]
    public async Task<IActionResult> ConvertToPdf([FromRoute] Guid id, [FromQuery] bool toPdfA, CancellationToken cancellationToken)
    {
      var document = await _documentStorageService.GetDocumentByIdAsync(id, cancellationToken);
      if (document is null)
      {
        return NotFound(new { message = "File not found." });
      }

      var isPdf = string.Equals(Path.GetExtension(document.OriginalFileName), ".pdf", StringComparison.OrdinalIgnoreCase);
      if (isPdf && !toPdfA)
      {
        return BadRequest(new { message = "The uploaded document is already a PDF. Request PDF/A conversion instead." });
      }

      if (!isPdf && !_officeConversionService.IsSupportedExtension(document.OriginalFileName))
      {
        return BadRequest(new { message = "Only Word or Excel documents can be converted (.doc, .docx, .xls, .xlsx, .rtf, .odt, .ods)." });
      }

      var workDirectory = Path.Combine(Path.GetTempPath(), "office-conversion", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(workDirectory);

      try
      {
        var sourcePath = Path.Combine(workDirectory, Path.GetFileName(document.OriginalFileName));
        await System.IO.File.WriteAllBytesAsync(sourcePath, document.Content, cancellationToken);

        string finalPdfPath;
        if (isPdf)
        {
          finalPdfPath = sourcePath;
        }
        else
        {
          var conversionResult = await _officeConversionService.ConvertToPdfAsync(sourcePath, workDirectory, cancellationToken);
          if (!conversionResult.Success || conversionResult.OutputPdfPath is null)
          {
            _logger.LogError("Word/Excel to PDF conversion failed for document '{DocumentId}': {Error}", id, conversionResult.ErrorMessage);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = conversionResult.ErrorMessage ?? "Conversion failed." });
          }

          finalPdfPath = conversionResult.OutputPdfPath;
        }

        if (toPdfA)
        {
          var pdfaPath = Path.Combine(workDirectory, Path.GetFileNameWithoutExtension(finalPdfPath) + "-pdfa.pdf");
          var pdfaResult = await _pdfaService.ConvertToPdfAAsync(finalPdfPath, pdfaPath, cancellationToken);
          if (!pdfaResult.IsCompliant)
          {
            _logger.LogError("PDF/A conversion failed for document '{DocumentId}': {Report}", id, pdfaResult.Report);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "PDF/A conversion or validation failed.", report = pdfaResult.Report });
          }

          finalPdfPath = pdfaPath;
        }

        var pdfBytes = await System.IO.File.ReadAllBytesAsync(finalPdfPath, cancellationToken);
        var convertedFileName = Path.GetFileNameWithoutExtension(document.OriginalFileName) + (toPdfA ? ".pdfa.pdf" : ".pdf");

        var requestedFormat = toPdfA ? DocumentOutputFormat.PdfA : DocumentOutputFormat.Pdf;
        var ingestion = await _documentIngestionService.IngestAsync(
          id,
          [new IngestionDocumentInput(convertedFileName, pdfBytes, "application/pdf", requestedFormat)],
          requestedFormat,
          cancellationToken);
        var savedDocument = ingestion.Documents[0];

        _logger.LogInformation("Converted document '{SourceId}' to {Kind} as new document '{NewId}', outbox event '{EventId}'.", id, toPdfA ? "PDF/A" : "PDF", savedDocument.Id, ingestion.EventId);

        return Ok(new
        {
          message = toPdfA ? "Document converted to PDF/A successfully." : "Document converted to PDF successfully.",
          id = savedDocument.Id,
          correlationId = ingestion.CorrelationId,
          originalFileName = savedDocument.OriginalFileName,
          storedFileName = savedDocument.Id.ToString(),
          size = savedDocument.Size,
          processingStatus = DocumentProcessingStatus.Completed.ToString(),
          requestedOutputFormat = (toPdfA ? DocumentOutputFormat.PdfA : DocumentOutputFormat.Pdf).ToString()
        });
      }
      finally
      {
        TryDeleteDirectory(workDirectory);
      }
    }

    private void TryDeleteDirectory(string path)
    {
      try
      {
        if (Directory.Exists(path))
        {
          Directory.Delete(path, recursive: true);
        }
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Failed to clean up temporary conversion directory '{Path}'.", path);
      }
    }
  }
}