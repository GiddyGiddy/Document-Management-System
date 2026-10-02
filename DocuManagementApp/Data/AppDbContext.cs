using DocuManagementApp.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuManagementApp.Data;

public sealed class AppDbContext : DbContext
{
  public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
  {
  }

  public DbSet<DocumentRecord> Documents => Set<DocumentRecord>();

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
  }
}
