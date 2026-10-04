using System.Text;
using ExtractionService;
using Microsoft.Extensions.Options;

namespace DocuManagementApp.Tests;

public sealed class ExtractionRequestTests
{
    [Fact]
    public void ParsesOrchestratorExtractionTaskRequest()
    {
        var eventId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var payload = $$"""
            {
              "eventId": "{{eventId:D}}",
              "eventType": "document.task.requested",
              "schemaVersion": 1,
              "correlationId": "{{correlationId:D}}",
              "documentId": "{{documentId:D}}",
              "taskId": "{{taskId:D}}",
              "taskType": "extraction",
              "documentKind": "Pdf",
              "inputReference": null
            }
            """;

        var request = ExtractionRequest.Parse(Encoding.UTF8.GetBytes(payload), null);

        Assert.Equal(eventId, request.EventId);
        Assert.Equal(correlationId, request.CorrelationId);
        Assert.Equal(documentId, request.DocumentId);
        Assert.Equal(taskId, request.TaskId);
        Assert.Equal("Pdf", request.DocumentKind);
        Assert.Null(request.InputReference);
        Assert.Contains("document.task.requested", request.RawPayload);
    }

    [Fact]
    public void RejectsNonExtractionTask()
    {
        var payload = $$"""
            {
              "eventId": "{{Guid.NewGuid():D}}",
              "eventType": "document.task.requested",
              "schemaVersion": 1,
              "correlationId": "{{Guid.NewGuid():D}}",
              "documentId": "{{Guid.NewGuid():D}}",
              "taskId": "{{Guid.NewGuid():D}}",
              "taskType": "search-index"
            }
            """;

        Assert.Throws<InvalidDataException>(() => ExtractionRequest.Parse(Encoding.UTF8.GetBytes(payload), null));
    }

    [Fact]
    public async Task ContentClientDownloadsDocumentById()
    {
        var documentId = Guid.NewGuid();
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5176/") };
        var client = new DocumentContentApiClient(httpClient);

        var document = await client.GetAsync(documentId, CancellationToken.None);

        Assert.Equal($"api/fileupload/download/{documentId:D}", capturedRequest?.RequestUri?.PathAndQuery.TrimStart('/'));
        Assert.Equal("application/pdf", document.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3 }, document.Bytes);
    }

    [Fact]
    public async Task LayoutClientRequestsMarkdownAndPollsAnalysisOperation()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler((request, cancellationToken) =>
        {
            requestCount++;
            if (request.Method == HttpMethod.Post)
            {
                Assert.Contains("outputContentFormat=markdown", request.RequestUri!.Query);
                Assert.Equal("secret-for-test", request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
                var accepted = new HttpResponseMessage(System.Net.HttpStatusCode.Accepted);
                accepted.Headers.Location = new Uri("http://layout.test/formrecognizer/operations/test-operation?api-version=2024-11-30");
                return Task.FromResult(accepted);
            }

            var completed = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"status":"succeeded","analyzeResult":{"content":"# Invoice\\nTotal: 42.00","pages":[]}}
                    """)
            };
            return Task.FromResult(completed);
        });
        using var httpClient = new HttpClient(handler);
        var client = new DocumentIntelligenceLayoutClient(httpClient, Options.Create(new DocumentIntelligenceOptions
        {
            Endpoint = "http://layout.test",
            ApiKey = "secret-for-test",
            PollIntervalSeconds = 1,
            TimeoutSeconds = 10
        }));

        var result = await client.AnalyzeAsync(new DocumentContent([1, 2, 3], "application/pdf"), CancellationToken.None);

        Assert.Equal(2, requestCount);
        Assert.Contains("# Invoice", result.Markdown);
        Assert.Contains("\"pages\":[]", result.RawJson);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return sendAsync(request, cancellationToken);
        }
    }
}