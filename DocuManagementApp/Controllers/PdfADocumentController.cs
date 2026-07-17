using DocuManagementApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace DocuManagementApp.Controllers
{
  public sealed class GeneratePdfaRequest
  {
    public string? XmlPath { get; set; }
    public string? XslPath { get; set; }
    public string? ImageBasePath { get; set; }
    public string? OutputPdfPath { get; set; }
    public bool UseDefaultEncoding { get; set; }
    public int ComplianceLevel { get; set; } = 1;
    public bool DisablePdfEncryption { get; set; } = true;
    public bool EnforceConformance { get; set; } = true;
    public string? Author { get; set; }
    public string? Title { get; set; }
    public string? Subject { get; set; }
    public string? Keywords { get; set; }
  }

  public sealed class ConvertPdfToPdfaRequest
  {
    public string? SourcePdfPath { get; set; }
    public string? OutputPdfPath { get; set; }
    public int ComplianceLevel { get; set; } = 1;
    public bool DisablePdfEncryption { get; set; } = true;
    public bool EnforceConformance { get; set; } = true;
  }

  public sealed class ValidatePdfaRequest
  {
    public string? PdfPath { get; set; }
  }

  [ApiController]
  [Route("api/pdfa")]
  public class PdfADocumentController : ControllerBase
  {
    private readonly PdfADocumentService _pdfaService;

    public PdfADocumentController(PdfADocumentService pdfaService)
    {
      _pdfaService = pdfaService;
    }

    [HttpPost("generate")]
    public IActionResult Generate([FromBody] GeneratePdfaRequest? request)
    {
      if (request is null)
      {
        return BadRequest(new { message = "Request body is required." });
      }

      _pdfaService.XMLPfad = request.XmlPath;
      _pdfaService.XSLPfad = request.XslPath;
      _pdfaService.BildPfad = request.ImageBasePath;
      _pdfaService.PdfaZielPfad = request.OutputPdfPath;
      _pdfaService.PdfaCompliance = request.ComplianceLevel;
      _pdfaService.DisablePdfEncryptionForCompliance = request.DisablePdfEncryption;
      _pdfaService.EnforcePdfaConformanceOutput = request.EnforceConformance;
      _pdfaService.PdfAuthor = request.Author ?? string.Empty;
      _pdfaService.PdfTitle = request.Title ?? string.Empty;
      _pdfaService.PdfSubject = request.Subject ?? string.Empty;
      _pdfaService.PdfKeywords = request.Keywords ?? string.Empty;

      int result = request.UseDefaultEncoding
        ? _pdfaService.DokumentAlsPdfaGenerierenMitDefaultEncoding()
        : _pdfaService.DokumentAlsPdfaGenerieren();

      return MapResult(result, "PDF/A generation finished.");
    }

    [HttpPost("convert")]
    public IActionResult Convert([FromBody] ConvertPdfToPdfaRequest? request)
    {
      if (request is null)
      {
        return BadRequest(new { message = "Request body is required." });
      }

      _pdfaService.QuellPfad = request.SourcePdfPath;
      _pdfaService.PdfaZielPfad = request.OutputPdfPath;
      _pdfaService.PdfaCompliance = request.ComplianceLevel;
      _pdfaService.DisablePdfEncryptionForCompliance = request.DisablePdfEncryption;
      _pdfaService.EnforcePdfaConformanceOutput = request.EnforceConformance;

      int result = _pdfaService.PdfNachPdfaKonvertieren();
      return MapResult(result, "PDF to PDF/A conversion finished.");
    }

    [HttpPost("validate")]
    public IActionResult Validate([FromBody] ValidatePdfaRequest? request)
    {
      if (request is null || string.IsNullOrWhiteSpace(request.PdfPath))
      {
        return BadRequest(new { message = "PdfPath is required." });
      }

      int result = _pdfaService.ValidatePdfaConformance(request.PdfPath);
      if (result == 0)
      {
        return Ok(new
        {
          message = "PDF/A validation passed.",
          report = _pdfaService.GetLastPdfaValidationReport()
        });
      }

      return BadRequest(new
      {
        message = _pdfaService.GetErrorMessage(),
        report = _pdfaService.GetLastPdfaValidationReport(),
        resultCode = result
      });
    }

    [HttpGet("suggestions")]
    public IActionResult Suggestions([FromQuery] string pdfPath)
    {
      if (string.IsNullOrWhiteSpace(pdfPath))
      {
        return BadRequest(new { message = "pdfPath query parameter is required." });
      }

      string suggestions = _pdfaService.GetPdfaConformanceSuggestions(pdfPath);
      return Ok(new { suggestions });
    }

    [HttpGet("last-report")]
    public IActionResult LastReport()
    {
      return Ok(new
      {
        report = _pdfaService.GetLastPdfaValidationReport(),
        error = _pdfaService.GetErrorMessage()
      });
    }

    private IActionResult MapResult(int resultCode, string successMessage)
    {
      if (resultCode == 0)
      {
        return Ok(new
        {
          message = successMessage,
          report = _pdfaService.GetLastPdfaValidationReport()
        });
      }

      return BadRequest(new
      {
        message = _pdfaService.GetErrorMessage(),
        report = _pdfaService.GetLastPdfaValidationReport(),
        resultCode
      });
    }
  }
}