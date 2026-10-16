namespace DocuManagementApp.Models;

public sealed class DocumentRevision
{
  public Guid Id { get; set; } = Guid.NewGuid();
  public Guid DocumentId { get; set; }
  public int RevisionNumber { get; set; }
  public int? BaseRevisionNumber { get; set; }
  public byte[] FileContent { get; set; } = Array.Empty<byte>();
  public string ContentType { get; set; } = "application/octet-stream";
  public long SizeBytes { get; set; }
  public string EditedBy { get; set; } = "anonymous";
  public string? Comment { get; set; }
  public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
