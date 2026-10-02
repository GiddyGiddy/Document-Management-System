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
    public async Task<IActionResult> Generate([FromBody] GeneratePdfaRequest? request, CancellationToken cancellationToken)
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
        ? await _pdfaService.DokumentAlsPdfaGenerierenMitDefaultEncodingAsync(cancellationToken)
        : await _pdfaService.DokumentAlsPdfaGenerierenAsync(cancellationToken);

      return MapResult(result, "PDF/A generation finished.");
    }

    [HttpPost("convert")]
    public async Task<IActionResult> Convert([FromBody] ConvertPdfToPdfaRequest? request, CancellationToken cancellationToken)
    {
      if (request is null)
      {
        return BadRequest(new { message = "Request body is required." });
      }

      if (string.IsNullOrWhiteSpace(request.SourcePdfPath) || string.IsNullOrWhiteSpace(request.OutputPdfPath))
      {
        return BadRequest(new { message = "SourcePdfPath and OutputPdfPath are required." });
      }
      if (request.ComplianceLevel != (int)PdfADocumentService.PdfAComplianceLevel.PdfA2B)
      {
        return BadRequest(new { message = "Only PDF/A-2b is supported." });
      }

      var result = await _pdfaService.ConvertPdfToPdfaAsync(request.SourcePdfPath, request.OutputPdfPath, cancellationToken);
      return result.IsCompliant
        ? Ok(new { message = "PDF/A-2b conversion and validation passed.", report = result.Report })
        : BadRequest(new { message = "PDF/A conversion or validation failed.", report = result.Report });
    }

    [HttpPost("validate")]
    public async Task<IActionResult> Validate([FromBody] ValidatePdfaRequest? request, CancellationToken cancellationToken)
    {
      if (request is null || string.IsNullOrWhiteSpace(request.PdfPath))
      {
        return BadRequest(new { message = "PdfPath is required." });
      }

      var result = await _pdfaService.ValidatePdfaAsync(request.PdfPath, cancellationToken);
      if (result.IsCompliant)
      {
        return Ok(new
        {
          message = "PDF/A validation passed.",
          report = result.Report
        });
      }

      return BadRequest(new
      {
        message = "PDF/A validation failed.",
        report = result.Report
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