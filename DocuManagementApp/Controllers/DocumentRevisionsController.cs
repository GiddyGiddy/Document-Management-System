using DocuManagementApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace DocuManagementApp.Controllers;

public sealed class CreateRevisionRequest
{
  public string ContentBase64 { get; set; } = string.Empty;
  public string? ContentType { get; set; }
  public int ExpectedRevision { get; set; }
  public string? Comment { get; set; }
}

public sealed class RestoreRevisionRequest
{
  public int ExpectedRevision { get; set; }
}

[ApiController]
[Route("api/documents/{documentId:guid}/revisions")]
public sealed class DocumentRevisionsController : ControllerBase
{
  private readonly IDocumentRevisionService _revisions;

  public DocumentRevisionsController(IDocumentRevisionService revisions) => _revisions = revisions;

  [HttpGet]
  public async Task<IActionResult> List(Guid documentId, CancellationToken ct)
  {
    var list = await _revisions.ListAsync(documentId, ct);
    return list is null ? NotFound() : Ok(list);
  }

  [HttpGet("{revisionNumber:int}/content")]
  public async Task<IActionResult> Content(Guid documentId, int revisionNumber, CancellationToken ct)
  {
    var rev = await _revisions.GetAsync(documentId, revisionNumber, ct);
    return rev is null ? NotFound() : File(rev.FileContent, rev.ContentType);
  }

  [HttpPost]
  [RequestSizeLimit(30 * 1024 * 1024)]
  public async Task<IActionResult> Create(Guid documentId, [FromBody] CreateRevisionRequest request, CancellationToken ct)
  {
    byte[] content;
    try
    {
      content = Convert.FromBase64String(request.ContentBase64 ?? string.Empty);
    }
    catch (FormatException)
    {
      return BadRequest("contentBase64 is not valid base64.");
    }

    var result = await _revisions.CreateAsync(documentId, content, request.ContentType,
      request.ExpectedRevision, Editor(), request.Comment, ct);
    return Map(result);
  }

  [HttpPost("{revisionNumber:int}/restore")]
  public async Task<IActionResult> Restore(Guid documentId, int revisionNumber, [FromBody] RestoreRevisionRequest request, CancellationToken ct)
  {
    var result = await _revisions.RestoreAsync(documentId, revisionNumber, request.ExpectedRevision, Editor(), ct);
    return Map(result);
  }

  // TODO: replace with the authenticated principal once RBAC is in place.
  private string Editor() => Request.Headers["X-Editor"].FirstOrDefault() ?? "anonymous";

  private IActionResult Map(RevisionResult result) => result.Outcome switch
  {
    RevisionOutcome.Created => StatusCode(StatusCodes.Status201Created, result.Revision),
    RevisionOutcome.NotFound => NotFound(),
    RevisionOutcome.Invalid => BadRequest("Content must not be empty."),
    _ => Conflict(new { message = "Document was modified by someone else.", currentRevision = result.CurrentRevision })
  };
}
