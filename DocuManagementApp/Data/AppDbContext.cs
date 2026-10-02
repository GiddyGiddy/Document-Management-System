using DocuManagementApp.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuManagementApp.Data;

public sealed class AppDbContext : DbContext
{
  public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
  {
  }

  public DbSet<DocumentRecord> Documents => Set<DocumentRecord>();
  public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.Entity<DocumentRecord>(entity =>
    {
      entity.ToTable("documents");
      entity.HasKey(x => x.Id);
      entity.Property(x => x.OriginalFileName).HasMaxLength(512).IsRequired();
      entity.Property(x => x.ContentType).HasMaxLength(256).IsRequired();
      entity.Property(x => x.FileContent).IsRequired();
      entity.Property(x => x.SizeBytes).IsRequired();
      entity.Property(x => x.UploadedAtUtc).IsRequired();
      entity.Property(x => x.ProcessingStatus)
        .HasConversion<string>()
        .HasMaxLength(32)
        .HasDefaultValue(DocumentProcessingStatus.Completed)
        .IsRequired();
      entity.Property(x => x.RequestedOutputFormat)
        .HasConversion<string>()
        .HasMaxLength(16)
        .HasDefaultValue(DocumentOutputFormat.Original)
        .IsRequired();
      entity.Property(x => x.ProcessingStartedAtUtc);
      entity.Property(x => x.ProcessingCompletedAtUtc);
      entity.Property(x => x.FailureSummary).HasMaxLength(2000);
      entity.HasIndex(x => x.UploadedAtUtc);
      entity.HasIndex(x => new { x.ProcessingStatus, x.UploadedAtUtc });
    });

    modelBuilder.Entity<OutboxMessage>(entity =>
    {
      entity.ToTable("outbox_messages");
      entity.HasKey(x => x.Id);
      entity.Property(x => x.EventType).HasMaxLength(128).IsRequired();
      entity.Property(x => x.SchemaVersion).IsRequired();
      entity.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
      entity.Property(x => x.CreatedAtUtc).IsRequired();
      entity.Property(x => x.AttemptCount).HasDefaultValue(0).IsRequired();
      entity.Property(x => x.LastAttemptAtUtc);
      entity.Property(x => x.NextAttemptAtUtc);
      entity.Property(x => x.LockedUntilUtc);
      entity.Property(x => x.LockToken);
      entity.Property(x => x.LastError).HasMaxLength(4000);
      entity.Property(x => x.PublishedAtUtc);
      entity.HasOne<DocumentRecord>()
        .WithMany()
        .HasForeignKey(x => x.DocumentId)
        .OnDelete(DeleteBehavior.Restrict);
      entity.HasIndex(x => x.DocumentId);
      entity.HasIndex(x => new { x.NextAttemptAtUtc, x.CreatedAtUtc })
        .HasDatabaseName("IX_outbox_messages_Unpublished_NextAttempt_CreatedAt")
        .HasFilter("\"PublishedAtUtc\" IS NULL");
    });
  }
}
