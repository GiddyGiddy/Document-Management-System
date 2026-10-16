using DocuManagementApp.Data;
using DocuManagementApp.Models;
using DocuManagementApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DocuManagementApp.Tests;

public sealed class DocumentRevisionServiceTests
{
  private static (AppDbContext Db, DocumentRevisionService Svc, Guid Id) Create()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString())
      .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
      .Options;
    var db = new AppDbContext(options);
    var doc = new DocumentRecord
    {
      Id = Guid.NewGuid(),
      OriginalFileName = "a.txt",
      ContentType = "text/plain",
      FileContent = new byte[] { 1, 2, 3 },
      SizeBytes = 3,
      UploadedAtUtc = DateTimeOffset.UtcNow
    };
    db.Documents.Add(doc);
    db.SaveChanges();
    return (db, new DocumentRevisionService(db), doc.Id);
  }

  [Fact]
  public async Task FirstEdit_SnapshotsOriginalAndCreatesRevision2()
  {
    var (db, svc, id) = Create();
    var result = await svc.CreateAsync(id, new byte[] { 9, 9 }, null, 1, "bob", "fix");

    Assert.Equal(RevisionOutcome.Created, result.Outcome);
    Assert.Equal(2, result.CurrentRevision);
    var doc = await db.Documents.FindAsync(id);
    Assert.Equal(new byte[] { 9, 9 }, doc!.FileContent);
    Assert.Equal(2, doc.CurrentRevisionNumber);
    var original = await svc.GetAsync(id, 1);
    Assert.Equal(new byte[] { 1, 2, 3 }, original!.FileContent);
    Assert.Equal(2, (await svc.ListAsync(id))!.Count);
  }

  [Fact]
  public async Task StaleExpectedRevision_ReturnsConflict()
  {
    var (_, svc, id) = Create();
    await svc.CreateAsync(id, new byte[] { 5 }, null, 1, "a", null);
    var result = await svc.CreateAsync(id, new byte[] { 6 }, null, 1, "b", null);

    Assert.Equal(RevisionOutcome.Conflict, result.Outcome);
    Assert.Equal(2, result.CurrentRevision);
  }

  [Fact]
  public async Task Restore_CreatesNewRevisionWithOldContent()
  {
    var (db, svc, id) = Create();
    await svc.CreateAsync(id, new byte[] { 5 }, null, 1, "a", null);
    var result = await svc.RestoreAsync(id, 1, 2, "a");

    Assert.Equal(RevisionOutcome.Created, result.Outcome);
    Assert.Equal(3, result.CurrentRevision);
    Assert.Equal(new byte[] { 1, 2, 3 }, (await db.Documents.FindAsync(id))!.FileContent);
  }

  [Fact]
  public async Task UnknownDocument_ReturnsNotFound()
  {
    var (_, svc, _) = Create();
    var result = await svc.CreateAsync(Guid.NewGuid(), new byte[] { 1 }, null, 1, "a", null);
    Assert.Equal(RevisionOutcome.NotFound, result.Outcome);
    Assert.Null(await svc.ListAsync(Guid.NewGuid()));
  }

  [Fact]
  public async Task EmptyContent_IsInvalid()
  {
    var (_, svc, id) = Create();
    var result = await svc.CreateAsync(id, Array.Empty<byte>(), null, 1, "a", null);
    Assert.Equal(RevisionOutcome.Invalid, result.Outcome);
  }
}
