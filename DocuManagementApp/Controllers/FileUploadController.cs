using Microsoft.AspNetCore.Mvc;

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
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<FileUploadController> _logger;

    public FileUploadController(IWebHostEnvironment environment, ILogger<FileUploadController> logger)
    {
      _environment = environment;
      _logger = logger;
      _logger.LogInformation("FileUploadController instantiated.");
    }

    [HttpPost("upload")]
    [Consumes("application/json")]
    [RequestSizeLimit(30 * 1024 * 1024)]
    public async Task<IActionResult> UploadFile([FromBody] UploadFileRequest? request)
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

      var uploadsPath = Path.Combine(_environment.ContentRootPath, "Uploads");
      Directory.CreateDirectory(uploadsPath);

      var safeFileName = Path.GetFileName(request.FileName);
      var storedFileName = $"{Guid.NewGuid():N}_{safeFileName}";
      var fullPath = Path.Combine(uploadsPath, storedFileName);

      await System.IO.File.WriteAllBytesAsync(fullPath, fileBytes);

      _logger.LogInformation("File saved: '{StoredFileName}', size: {Size} bytes.", storedFileName, fileBytes.Length);

      return Ok(new
      {
        message = "File uploaded successfully.",
        originalFileName = safeFileName,
        storedFileName,
        size = fileBytes.Length
      });
    }
  }
}