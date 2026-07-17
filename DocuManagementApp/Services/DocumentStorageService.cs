using DocuManagementApp.Data;
using DocuManagementApp.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuManagementApp.Services
{

public sealed class DocumentListItem
{
  public string StoredFileName { get; set; } = string.Empty;
  public string OriginalFileName { get; set; } = string.Empty;
  public long Size { get; set; }
  public DateTimeOffset UploadedAt { get; set; }
}

public sealed class StoredDocumentResult
{
  public Guid Id { get; set; }
  public string OriginalFileName { get; set; } = string.Empty;
  public long Size { get; set; }
}

public sealed class DocumentDownloadResult
{
  public Guid Id { get; set; }
  public string OriginalFileName { get; set; } = string.Empty;
  public string ContentType { get; set; } = "application/octet-stream";
  public byte[] Content { get; set; } = Array.Empty<byte>();
}

public interface IDocumentStorageService
{
  Task<StoredDocumentResult> SaveDocumentAsync(string originalFileName, byte[] content, string? contentType, CancellationToken cancellationToken);
  Task<IReadOnlyList<DocumentListItem>> GetDocumentsAsync(CancellationToken cancellationToken);
  Task<DocumentDownloadResult?> GetDocumentByIdAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class DocumentStorageService : IDocumentStorageService
{
  private readonly AppDbContext _dbContext;

  public DocumentStorageService(AppDbContext dbContext)
  {
    _dbContext = dbContext;
  }

  public async Task<StoredDocumentResult> SaveDocumentAsync(string originalFileName, byte[] content, string? contentType, CancellationToken cancellationToken)
  {
    var document = new DocumentRecord
    {
      OriginalFileName = originalFileName,
      ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
      FileContent = content,
      SizeBytes = content.Length,
      UploadedAtUtc = DateTimeOffset.UtcNow
    };

    _dbContext.Documents.Add(document);
    await _dbContext.SaveChangesAsync(cancellationToken);

    return new StoredDocumentResult
    {
      Id = document.Id,
      OriginalFileName = document.OriginalFileName,
      Size = document.SizeBytes
    };
  }

  public async Task<IReadOnlyList<DocumentListItem>> GetDocumentsAsync(CancellationToken cancellationToken)
  {
    return await _dbContext.Documents
      .OrderByDescending(x => x.UploadedAtUtc)
      .Select(x => new DocumentListItem
      {
        StoredFileName = x.Id.ToString(),
        OriginalFileName = x.OriginalFileName,
        Size = x.SizeBytes,
        UploadedAt = x.UploadedAtUtc
      })
      .ToListAsync(cancellationToken);
  }

  public async Task<DocumentDownloadResult?> GetDocumentByIdAsync(Guid id, CancellationToken cancellationToken)
  {
    return await _dbContext.Documents
      .AsNoTracking()
      .Where(x => x.Id == id)
      .Select(x => new DocumentDownloadResult
      {
        Id = x.Id,
        OriginalFileName = x.OriginalFileName,
        ContentType = x.ContentType,
        Content = x.FileContent
      })
      .SingleOrDefaultAsync(cancellationToken);
  }
}
}
