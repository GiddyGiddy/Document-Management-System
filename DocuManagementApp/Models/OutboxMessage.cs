namespace DocuManagementApp.Models;

public sealed class OutboxMessage
{
  public Guid Id { get; set; } = Guid.NewGuid();
  public Guid CorrelationId { get; set; }
  public Guid DocumentId { get; set; }
  public string EventType { get; set; } = string.Empty;
  public int SchemaVersion { get; set; } = 1;
  public string Payload { get; set; } = "{}";
  public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
  public int AttemptCount { get; set; }
  public DateTimeOffset? LastAttemptAtUtc { get; set; }
  public DateTimeOffset? NextAttemptAtUtc { get; set; }
  public DateTimeOffset? LockedUntilUtc { get; set; }
  public Guid? LockToken { get; set; }
  public string? LastError { get; set; }
  public DateTimeOffset? PublishedAtUtc { get; set; }
}