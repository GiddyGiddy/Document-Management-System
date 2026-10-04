using ExtractionService;

namespace DocuManagementApp.Tests;

public sealed class DocumentCriticLoopTests
{
    [Fact]
    public async Task CorrectsInvalidInvoiceTotalsAndReturnsValidatedJson()
    {
        var correctionAgent = new FakeCorrectionAgent([ValidInvoiceJson]);
        var loop = CreateLoop(new FakeExtractionAgent(InvalidTotalJson), correctionAgent);

        var result = await loop.RunAsync(SupportedDocumentKinds.Invoice, "# Invoice", CancellationToken.None);

        var success = Assert.IsType<ExtractionSuccess>(result);
        Assert.Equal(SupportedDocumentKinds.Invoice, success.DocumentKind);
        Assert.Equal(2, success.Attempts);
        Assert.Contains("\"total\": 10.00", success.Json);
        Assert.Single(correctionAgent.RequestedIssues);
        Assert.Contains(correctionAgent.RequestedIssues[0], issue => issue.Path == "total");
    }

    [Fact]
    public async Task ReturnsFailureAfterThreeTotalAttempts()
    {
        var correctionAgent = new FakeCorrectionAgent([InvalidTotalJson, InvalidTotalJson]);
        var loop = CreateLoop(new FakeExtractionAgent(InvalidTotalJson), correctionAgent);

        var result = await loop.RunAsync(SupportedDocumentKinds.Invoice, "# Invoice", CancellationToken.None);

        var failed = Assert.IsType<ExtractionFailed>(result);
        Assert.Equal(3, failed.Attempts);
        Assert.Equal(2, correctionAgent.RequestedIssues.Count);
        Assert.Contains(failed.Issues, issue => issue.Path == "total");
    }

    [Fact]
    public async Task RejectsUnexpectedJsonProperties()
    {
        var payload = ValidInvoiceJson.Replace("\"vendorName\"", "\"untrustedExtra\":\"value\",\"vendorName\"", StringComparison.Ordinal);
        var loop = CreateLoop(new FakeExtractionAgent(payload), new FakeCorrectionAgent([ValidInvoiceJson]));

        var result = await loop.RunAsync(SupportedDocumentKinds.Invoice, "# Invoice", CancellationToken.None);

        var success = Assert.IsType<ExtractionSuccess>(result);
        Assert.Equal(2, success.Attempts);
    }

    private static DocumentCriticLoop CreateLoop(FakeExtractionAgent extraction, FakeCorrectionAgent correction)
    {
        return new DocumentCriticLoop(extraction, correction, new DocumentExtractionValidator());
    }

    internal const string InvalidTotalJson = """
        {
          "vendorName": "Example Supplies",
          "invoiceNumber": "INV-100",
          "invoiceDate": "2026-10-01",
          "dueDate": "2026-10-31",
          "currency": "USD",
          "lineItems": [{ "description": "Paper", "quantity": 1, "unitPrice": 10.00, "lineTotal": 10.00 }],
          "subtotal": 10.00,
          "tax": 0.00,
          "total": 11.00
        }
        """;

    internal const string ValidInvoiceJson = """
        {
          "vendorName": "Example Supplies",
          "invoiceNumber": "INV-100",
          "invoiceDate": "2026-10-01",
          "dueDate": "2026-10-31",
          "currency": "USD",
          "lineItems": [{ "description": "Paper", "quantity": 1, "unitPrice": 10.00, "lineTotal": 10.00 }],
          "subtotal": 10.00,
          "tax": 0.00,
          "total": 10.00
        }
        """;

    private sealed class FakeExtractionAgent(string response) : IDocumentExtractionAgent
    {
        public Task<string> ExtractAsync(string documentKind, string markdown, CancellationToken cancellationToken)
        {
            Assert.Equal(SupportedDocumentKinds.Invoice, documentKind);
            return Task.FromResult(response);
        }
    }

    private sealed class FakeCorrectionAgent(IReadOnlyList<string> responses) : IDocumentCorrectionAgent
    {
        private int _callCount;

        public List<IReadOnlyList<ExtractionValidationIssue>> RequestedIssues { get; } = [];

        public Task<string> CorrectAsync(
            string documentKind,
            string markdown,
            string candidateJson,
            IReadOnlyList<ExtractionValidationIssue> issues,
            CancellationToken cancellationToken)
        {
            Assert.Equal(SupportedDocumentKinds.Invoice, documentKind);
            RequestedIssues.Add(issues);
            var response = responses[Math.Min(_callCount, responses.Count - 1)];
            _callCount++;
            return Task.FromResult(response);
        }
    }
}