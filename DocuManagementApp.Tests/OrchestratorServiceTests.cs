using System.Text;
using OrchestratorService;

namespace DocuManagementApp.Tests;

public sealed class OrchestratorServiceTests
{
    [Theory]
    [InlineData("2026-invoice-104.pdf", "application/pdf", DocumentKind.Invoice)]
    [InlineData("supplier-agreement.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", DocumentKind.Contract)]
    [InlineData("passport-scan.png", "image/png", DocumentKind.IdentityDocument)]
    [InlineData("annual-report.pdf", "application/pdf", DocumentKind.FinancialStatement)]
    [InlineData("scan.pdf", "application/pdf", DocumentKind.Pdf)]
    [InlineData("records.csv", "text/csv", DocumentKind.Spreadsheet)]
    [InlineData("notes.txt", "text/plain", DocumentKind.Text)]
    [InlineData("opaque.bin", "application/octet-stream", DocumentKind.Unknown)]
    public void ClassifierUsesNameAndMediaType(string fileName, string contentType, DocumentKind expected)
    {
        var classifier = new DocumentClassifier();

        var result = classifier.Classify(fileName, contentType);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void EventParserAcceptsIngestionPayloadPropertyCasing()
    {
        var eventId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var payload = $$"""
            {
              "eventId": "{{eventId:D}}",
              "eventType": "document.ingested",
              "schemaVersion": 1,
              "correlation_id": "{{correlationId:D}}",
              "documents": [
                {
                  "documentId": "{{documentId:D}}",
                  "OriginalFileName": "invoice.pdf",
                  "ContentType": "application/pdf",
                  "SizeBytes": 1200
                }
              ]
            }
            """;

        var message = OrchestrationEvent.Parse(Encoding.UTF8.GetBytes(payload), null, null);

        Assert.Equal(eventId, message.EventId);
        Assert.Equal(correlationId, message.CorrelationId);
        Assert.Equal("document.ingested", message.EventType);
        var document = Assert.Single(message.Documents);
        Assert.Equal(documentId, document.DocumentId);
        Assert.Equal("invoice.pdf", document.OriginalFileName);
        Assert.Equal("application/pdf", document.ContentType);
        Assert.Equal(1200, document.SizeBytes);
    }

      [Fact]
      public void EventParserRejectsUnsupportedSchemaVersion()
      {
        var payload = $$"""
          {
            "eventId": "{{Guid.NewGuid():D}}",
            "eventType": "document.ingested",
            "schemaVersion": 2,
            "correlation_id": "{{Guid.NewGuid():D}}",
            "documents": []
          }
          """;

        Assert.Throws<InvalidDataException>(() => OrchestrationEvent.Parse(Encoding.UTF8.GetBytes(payload), null, null));
      }
}