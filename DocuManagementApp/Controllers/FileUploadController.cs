using Microsoft.AspNetCore.Mvc;
using DocuManagementApp.Services;
using System.IO;

namespace DocuManagementApp.Controllers
{
  public sealed class UploadFileRequest
  {
    public string? FileName { get; set; }
    public string? ContentBase64 { get; set; }
  }

  [ApiController]
  [Route("api/fileupload")]
  public class FileUploadController : ControllerBase
  {
    private readonly ILogger<FileUploadController> _logger;
    private readonly IDocumentStorageService _documentStorageService;

    public FileUploadController(ILogger<FileUploadController> logger, IDocumentStorageService documentStorageService)
    {
      _logger = logger;
      _documentStorageService = documentStorageService;
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
      var savedDocument = await _documentStorageService.SaveDocumentAsync(
        safeFileName,
        fileBytes,
        "application/octet-stream",
        cancellationToken);

      _logger.LogInformation("File saved to PostgreSQL with id '{DocumentId}', size: {Size} bytes.", savedDocument.Id, savedDocument.Size);

      return Ok(new
      {
        message = "File uploaded successfully.",
        id = savedDocument.Id,
        originalFileName = savedDocument.OriginalFileName,
        storedFileName = savedDocument.Id.ToString(),
        size = savedDocument.Size
      });
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
      return File(stream, document.ContentType, document.OriginalFileName, enableRangeProcessing: true);
    }
  }
}