using DocuManagementApp.Data;
using DocuManagementApp.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuManagementApp.Services;

public sealed class RevisionInfo
{
  public int RevisionNumber { get; set; }
  public int? BaseRevisionNumber { get; set; }
  public string ContentType { get; set; } = string.Empty;
  public long SizeBytes { get; set; }
  public string EditedBy { get; set; } = string.Empty;
  public string? Comment { get; set; }
  public DateTimeOffset CreatedAtUtc { get; set; }
}

public enum RevisionOutcome { Created, NotFound, Conflict, Invalid }

public sealed class RevisionResult
{
  public RevisionOutcome Outcome { get; init; }
  public RevisionInfo? Revision { get; init; }
  public int CurrentRevision { get; init; }
}

public interface IDocumentRevisionService
{
  Task<IReadOnlyList<RevisionInfo>?> ListAsync(Guid documentId, CancellationToken ct = default);
  Task<DocumentRevision?> GetAsync(Guid documentId, int revisionNumber, CancellationToken ct = default);
  Task<RevisionResult> CreateAsync(Guid documentId, byte[] content, string? contentType, int expectedRevision, string editedBy, string? comment, CancellationToken ct = default);
  Task<RevisionResult> RestoreAsync(Guid documentId, int revisionNumber, int expectedRevision, string editedBy, CancellationToken ct = default);
}

public sealed class DocumentRevisionService : IDocumentRevisionService
{
  private readonly AppDbContext _db;

  public DocumentRevisionService(AppDbContext db) => _db = db;

  public async Task<IReadOnlyList<RevisionInfo>?> ListAsync(Guid documentId, CancellationToken ct = default)
  {
    var doc = await _db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct);
    if (doc is null) return null;

    var list = await _db.DocumentRevisions.AsNoTracking()
      .Where(r => r.DocumentId == documentId)
      .Select(r => new RevisionInfo
      {
        RevisionNumber = r.RevisionNumber,
        BaseRevisionNumber = r.BaseRevisionNumber,
        ContentType = r.ContentType,
        SizeBytes = r.SizeBytes,
        EditedBy = r.EditedBy,
        Comment = r.Comment,
        CreatedAtUtc = r.CreatedAtUtc
      })
      .ToListAsync(ct);

    if (list.Count == 0)
    {
      list.Add(new RevisionInfo
      {
        RevisionNumber = 1,
        ContentType = doc.ContentType,
        SizeBytes = doc.SizeBytes,
        EditedBy = "original",
        CreatedAtUtc = doc.UploadedAtUtc
      });
    }

    return list.OrderBy(r => r.RevisionNumber).ToList();
  }

  public async Task<DocumentRevision?> GetAsync(Guid documentId, int revisionNumber, CancellationToken ct = default)
  {
    var rev = await _db.DocumentRevisions.AsNoTracking()
      .FirstOrDefaultAsync(r => r.DocumentId == documentId && r.RevisionNumber == revisionNumber, ct);
    if (rev is not null) return rev;

    // Unedited document: revision 1 is the original upload.
    if (revisionNumber != 1) return null;
    var doc = await _db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct);
    if (doc is null || doc.CurrentRevisionNumber != 0) return null;

    return new DocumentRevision
    {
      DocumentId = doc.Id,
      RevisionNumber = 1,
      FileContent = doc.FileContent,
      ContentType = doc.ContentType,
      SizeBytes = doc.SizeBytes,
      EditedBy = "original",
      CreatedAtUtc = doc.UploadedAtUtc
    };
  }

  public async Task<RevisionResult> CreateAsync(Guid documentId, byte[] content, string? contentType, int expectedRevision, string editedBy, string? comment, CancellationToken ct = default)
  {
    if (content is null || content.Length == 0)
    {
      return new RevisionResult { Outcome = RevisionOutcome.Invalid };
    }

    var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
    if (doc is null) return new RevisionResult { Outcome = RevisionOutcome.NotFound };

    var current = Math.Max(doc.CurrentRevisionNumber, 1);
    if (expectedRevision != current)
    {
      return new RevisionResult { Outcome = RevisionOutcome.Conflict, CurrentRevision = current };
    }

    await using var tx = await _db.Database.BeginTransactionAsync(ct);

    if (doc.CurrentRevisionNumber == 0)
    {
      _db.DocumentRevisions.Add(new DocumentRevision
      {
        DocumentId = doc.Id,
        RevisionNumber = 1,
        FileContent = doc.FileContent,
        ContentType = doc.ContentType,
        SizeBytes = doc.SizeBytes,
        EditedBy = "original",
        CreatedAtUtc = doc.UploadedAtUtc
      });
    }

    var next = current + 1;
    var revision = new DocumentRevision
    {
      DocumentId = doc.Id,
      RevisionNumber = next,
      BaseRevisionNumber = current,
      FileContent = content,
      ContentType = string.IsNullOrWhiteSpace(contentType) ? doc.ContentType : contentType,
      SizeBytes = content.LongLength,
      EditedBy = string.IsNullOrWhiteSpace(editedBy) ? "anonymous" : editedBy,
      Comment = comment,
      CreatedAtUtc = DateTimeOffset.UtcNow
    };
    _db.DocumentRevisions.Add(revision);

    doc.FileContent = revision.FileContent;
    doc.ContentType = revision.ContentType;
    doc.SizeBytes = revision.SizeBytes;
    doc.CurrentRevisionNumber = next;

    try
    {
      await _db.SaveChangesAsync(ct);
      await tx.CommitAsync(ct);
    }
    catch (DbUpdateException)
    {
      // A concurrent writer took the same revision number.
      return new RevisionResult { Outcome = RevisionOutcome.Conflict, CurrentRevision = current + 1 };
    }

    return new RevisionResult
    {
      Outcome = RevisionOutcome.Created,
      CurrentRevision = next,
      Revision = new RevisionInfo
      {
        RevisionNumber = next,
        BaseRevisionNumber = current,
        ContentType = revision.ContentType,
        SizeBytes = revision.SizeBytes,
        EditedBy = revision.EditedBy,
        Comment = revision.Comment,
        CreatedAtUtc = revision.CreatedAtUtc
      }
    };
  }

  public async Task<RevisionResult> RestoreAsync(Guid documentId, int revisionNumber, int expectedRevision, string editedBy, CancellationToken ct = default)
  {
    var source = await GetAsync(documentId, revisionNumber, ct);
    if (source is null) return new RevisionResult { Outcome = RevisionOutcome.NotFound };

    return await CreateAsync(documentId, source.FileContent, source.ContentType, expectedRevision, editedBy,
      $"Restored from revision {revisionNumber}", ct);
  }
}
