using ExtractionService;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocuManagementApp.Tests;

public sealed class ExtractionLayoutPipelineTests
{
    [Fact]
    public async Task SuccessfulLayoutIsPersistedForTheExtractionAgentStage()
    {
        var layout = new DocumentLayoutResult("# Invoice", "{\"analyzeResult\":{\"content\":\"# Invoice\"}}");
        var store = new FakeJobStore();
        var pipeline = CreatePipeline(new FakeContentClient(), new FakeLayoutClient(layout), store);

        await pipeline.ProcessAsync(CreateJob(), CancellationToken.None);

        Assert.Equal(layout, store.LayoutReady);
        Assert.Null(store.FailureSummary);
    }

    [Fact]
    public async Task LayoutFailureIsRecordedWithTheConfiguredAttemptLimit()
    {
        var store = new FakeJobStore();
        var pipeline = CreatePipeline(
            new FakeContentClient(),
            new FakeLayoutClient(exception: new IOException("Layout container unavailable.")),
            store);

        await pipeline.ProcessAsync(CreateJob(attemptCount: 3), CancellationToken.None);

        Assert.Contains("Layout container unavailable", store.FailureSummary);
        Assert.Equal(3, store.MaxAttempts);
        Assert.Null(store.LayoutReady);
    }

        [Fact]
        public async Task EnabledModelStageRunsCriticAndStoresOutcome()
        {
            var store = new FakeJobStore();
            var pipeline = CreatePipeline(
                new FakeContentClient(),
                new FakeLayoutClient(new DocumentLayoutResult("# Invoice", "{\"analyzeResult\":{\"content\":\"# Invoice\"}}")),
                store,
                enableModelExtraction: true);

            await pipeline.ProcessAsync(CreateJob(), CancellationToken.None);

            Assert.IsType<ExtractionSuccess>(store.ExtractionOutcome);
            Assert.Null(store.FailureSummary);
        }

    private static ExtractionLayoutPipeline CreatePipeline(
        IDocumentContentClient contentClient,
        IDocumentLayoutClient layoutClient,
            FakeJobStore store,
            bool enableModelExtraction = false,
            IDocumentClassifierAgent? classifier = null)
    {
            var fakeAgent = new FakeInvoiceAgent();
        return new ExtractionLayoutPipeline(
            contentClient,
            layoutClient,
                new DocumentCriticLoop(fakeAgent, fakeAgent, new DocumentExtractionValidator()),
            store,
            Options.Create(new ExtractionProcessingOptions
            {
                    EnableModelExtraction = enableModelExtraction,
                MaxLayoutAttempts = 3,
                RetryBaseSeconds = 1,
                RetryMaxSeconds = 5
            }),
            NullLogger<ExtractionLayoutPipeline>.Instance,
            classifier);
    }

    private static ExtractionJob CreateJob(int attemptCount = 1, string kind = "Invoice")
    {
        return new ExtractionJob(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            kind, null, attemptCount, Guid.NewGuid());
    }

    private sealed class FakeClassifier(DocumentClassification result) : IDocumentClassifierAgent
    {
        public int Calls { get; private set; }

        public Task<DocumentClassification> ClassifyAsync(string markdown, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static Task<FakeJobStore> RunClassified(string jobKind, DocumentClassification classification, FakeClassifier? holder = null)
    {
        return RunClassifiedCore(jobKind, holder ?? new FakeClassifier(classification));
    }

    private static async Task<FakeJobStore> RunClassifiedCore(string jobKind, FakeClassifier classifier)
    {
        var store = new FakeJobStore();
        var pipeline = CreatePipeline(
            new FakeContentClient(),
            new FakeLayoutClient(new DocumentLayoutResult("# Doc", "{}")),
            store,
            enableModelExtraction: true,
            classifier: classifier);
        await pipeline.ProcessAsync(CreateJob(kind: jobKind), CancellationToken.None);
        return store;
    }

    [Fact]
    public async Task UnknownKindIsClassifiedThenExtracted()
    {
        var store = await RunClassified("Unknown", new DocumentClassification("Invoice", 0.95, "has invoice number"));

        var success = Assert.IsType<ExtractionSuccess>(store.ExtractionOutcome);
        Assert.Equal("Invoice", success.DocumentKind);
        Assert.Equal(0.95, success.Classification?.Confidence);
    }

    [Fact]
    public async Task LowConfidenceClassificationNeedsManualReview()
    {
        var store = await RunClassified("Unknown", new DocumentClassification("Invoice", 0.3, "unsure"));

        var pending = Assert.IsType<NeedsClassification>(store.ExtractionOutcome);
        Assert.Equal("Invoice", pending.Proposed.Kind);
    }

    [Fact]
    public async Task UnsupportedClassificationNeedsManualReview()
    {
        var store = await RunClassified("Pdf", new DocumentClassification("Unknown", 0.99, "no match"));

        Assert.IsType<NeedsClassification>(store.ExtractionOutcome);
    }

    [Fact]
    public async Task StructuredKindSkipsClassification()
    {
        var classifier = new FakeClassifier(new DocumentClassification("Contract", 0.99, "x"));
        var store = await RunClassified("Invoice", new DocumentClassification("Contract", 0.99, "x"), classifier);

        Assert.Equal(0, classifier.Calls);
        Assert.IsType<ExtractionSuccess>(store.ExtractionOutcome);
    }

    private sealed class FakeContentClient : IDocumentContentClient
    {
        public Task<DocumentContent> GetAsync(Guid documentId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new DocumentContent([1, 2, 3], "application/pdf"));
        }
    }

    private sealed class FakeLayoutClient(DocumentLayoutResult? result = null, Exception? exception = null) : IDocumentLayoutClient
    {
        public Task<DocumentLayoutResult> AnalyzeAsync(DocumentContent document, CancellationToken cancellationToken)
        {
            return exception is not null
                ? Task.FromException<DocumentLayoutResult>(exception)
                : Task.FromResult(result!);
        }
    }

        private sealed class FakeInvoiceAgent : IDocumentExtractionAgent, IDocumentCorrectionAgent
        {
            private const string InvoiceJson = """
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

            public Task<string> ExtractAsync(string documentKind, string markdown, CancellationToken cancellationToken)
            {
                return Task.FromResult(InvoiceJson);
            }

            public Task<string> CorrectAsync(string documentKind, string markdown, string candidateJson, IReadOnlyList<ExtractionValidationIssue> issues, CancellationToken cancellationToken)
            {
                return Task.FromResult(InvoiceJson);
            }
        }

    private sealed class FakeJobStore : IExtractionJobStore
    {
        public DocumentLayoutResult? LayoutReady { get; private set; }
        public string? FailureSummary { get; private set; }
        public int MaxAttempts { get; private set; }
            public DocumentExtractionOutcome? ExtractionOutcome { get; private set; }

        public Task<ExtractionJob?> ClaimNextJobAsync(TimeSpan leaseDuration, bool includeLayoutReady, CancellationToken cancellationToken)
        {
            return Task.FromResult<ExtractionJob?>(null);
        }

        public Task MarkLayoutReadyAsync(ExtractionJob job, DocumentLayoutResult layout, CancellationToken cancellationToken)
        {
            LayoutReady = layout;
            return Task.CompletedTask;
        }

        public Task MarkLayoutFailedAsync(ExtractionJob job, string failureSummary, int maxAttempts, TimeSpan retryDelay, CancellationToken cancellationToken)
        {
            FailureSummary = failureSummary;
            MaxAttempts = maxAttempts;
            return Task.CompletedTask;
        }

            public Task MarkExtractionOutcomeAsync(ExtractionJob job, DocumentLayoutResult layout, DocumentExtractionOutcome outcome, CancellationToken cancellationToken)
            {
                ExtractionOutcome = outcome;
                return Task.CompletedTask;
            }
    }
}