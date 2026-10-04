using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;
using SearchService;

namespace DocuManagementApp.Tests;

public sealed class SearchServiceTests
{
    [Fact]
    public void ParsesOrchestratorSearchIndexTaskAndArtifactUrl()
    {
        var eventId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var artifactUrl = $"http://extraction.local/api/internal/extraction-artifacts/{taskId:D}";
        var payload = $$"""
            {
              "eventId": "{{eventId:D}}",
              "eventType": "document.task.requested",
              "schemaVersion": 1,
              "correlationId": "{{correlationId:D}}",
              "documentId": "{{documentId:D}}",
              "taskId": "{{taskId:D}}",
              "taskType": "search-index",
              "documentKind": "Invoice",
              "inputReference": "{{artifactUrl}}"
            }
            """;

        var command = SearchIndexCommand.Parse(Encoding.UTF8.GetBytes(payload), null);

        Assert.Equal(eventId, command.EventId);
        Assert.Equal(correlationId, command.CorrelationId);
        Assert.Equal(documentId, command.DocumentId);
        Assert.Equal(taskId, command.TaskId);
        Assert.Equal("Invoice", command.DocumentKind);
        Assert.Equal(artifactUrl, command.ArtifactReference);
    }

    [Fact]
    public void RejectsSearchIndexCommandsWithoutArtifactUrl()
    {
        var payload = $$"""
            {
              "eventId": "{{Guid.NewGuid():D}}",
              "eventType": "document.task.requested",
              "schemaVersion": 1,
              "correlationId": "{{Guid.NewGuid():D}}",
              "documentId": "{{Guid.NewGuid():D}}",
              "taskId": "{{Guid.NewGuid():D}}",
              "taskType": "search-index",
              "documentKind": "Invoice",
              "inputReference": null
            }
            """;

        Assert.Throws<InvalidDataException>(() => SearchIndexCommand.Parse(Encoding.UTF8.GetBytes(payload), null));
    }

    [Fact]
    public void ChunkerKeepsParagraphPageAndPolygonCitation()
    {
        using var layout = JsonDocument.Parse("""
                        {
                            "analyzeResult": {
                                "paragraphs": [
                                    {
                                        "content": "Invoice total 100.00",
                                        "boundingRegions": [
                                            { "pageNumber": 2, "polygon": [1, 2, 3, 2, 3, 4, 1, 4] }
                                        ]
                                    }
                                ]
                            }
                        }
            """);
                using var extraction = JsonDocument.Parse("""{"vendorName":"Northwind"}""");
        var artifact = new ExtractionArtifactResponse(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Invoice", "Invoice total 100.00",
            layout.RootElement.Clone(), extraction.RootElement.Clone(), 1, DateTimeOffset.UtcNow);

        var chunk = Assert.Single(SearchChunker.Create(artifact));

        Assert.Equal("Invoice total 100.00", chunk.Text);
        Assert.Equal(2, chunk.PageNumber);
        Assert.Equal(8, Assert.IsType<JsonElement>(chunk.BoundingBox).GetArrayLength());
    }

    [Fact]
    public async Task EmbeddingClientBatchesAndOrdersVectorsByResponseIndex()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(JsonResponse("""
                {"data":[{"index":1,"embedding":[0,1]},{"index":0,"embedding":[1,0]}]}
                """));
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleEmbeddingClient(httpClient, new SearchOptions
        {
            EmbeddingDimensions = 2,
            Embeddings = new EmbeddingOptions
            {
                Provider = "FoundryLocal",
                Endpoint = "http://embedding.local/v1/",
                ModelName = "test-embed",
                ApiKey = "test-key"
            }
        });

        var vectors = await client.EmbedAsync(["first", "second"], CancellationToken.None);

        Assert.Equal("http://embedding.local/v1/embeddings", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer", capturedRequest?.Headers.Authorization?.Scheme);
        Assert.Equal(1, vectors[0][0]);
        Assert.Equal(1, vectors[1][1]);
    }

    [Fact]
    public async Task EmbeddingClientSupportsMicrosoftFoundryApiKeyHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(JsonResponse("""
                {"data":[{"index":0,"embedding":[1,0]}]}
                """));
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleEmbeddingClient(httpClient, new SearchOptions
        {
            EmbeddingDimensions = 2,
            Embeddings = new EmbeddingOptions
            {
                Provider = "MicrosoftFoundry",
                Endpoint = "https://example.services.ai.azure.com/openai/v1/",
                ModelName = "embedding-deployment",
                ApiKey = "foundry-key"
            }
        });

        await client.EmbedAsync(["invoice"], CancellationToken.None);

        Assert.Equal("foundry-key", capturedRequest?.Headers.GetValues("api-key").Single());
        Assert.Null(capturedRequest?.Headers.Authorization);
    }

    [Fact]
    public async Task EmbeddingClientUsesOllamaOpenAiCompatibleEndpointWithoutApiKey()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(JsonResponse("""
                {"data":[{"index":0,"embedding":[1,0]}]}
                """));
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleEmbeddingClient(httpClient, new SearchOptions
        {
            EmbeddingDimensions = 2,
            Embeddings = new EmbeddingOptions
            {
                Provider = "Ollama",
                Endpoint = "http://localhost:11434/v1/",
                ModelName = "bge-m3"
            }
        });

        var vectors = await client.EmbedAsync(["Vertragsverlängerung"], CancellationToken.None);

        Assert.True(new EmbeddingOptions
        {
            Provider = "Ollama",
            Endpoint = "http://localhost:11434/v1/",
            ModelName = "bge-m3"
        }.IsConfigured);
        Assert.Equal("http://localhost:11434/v1/embeddings", capturedRequest?.RequestUri?.ToString());
        Assert.Null(capturedRequest?.Headers.Authorization);
        Assert.Equal(1, vectors[0][0]);
    }

    [Fact]
    public async Task ArtifactClientDoesNotSendKeyToUnexpectedHost()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP call."));
        using var httpClient = new HttpClient(handler);
        var client = new ExtractionArtifactClient(httpClient, new SearchOptions
        {
            ArtifactApi = new ArtifactApiOptions
            {
                BaseUrl = "http://extraction.local",
                ApiKey = "internal-key"
            }
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAsync(
            "http://attacker.invalid/api/internal/extraction-artifacts/00000000-0000-0000-0000-000000000001",
            CancellationToken.None));
    }

    [Fact]
    public async Task AgenticSearchIsUnavailableUntilModelIsConfigured()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=searchdb");
        using var httpClient = new HttpClient();
        var options = new SearchOptions();
        var agent = new AgenticSearchService(options,
            new SearchDatabase(dataSource, options),
            new OpenAiCompatibleEmbeddingClient(httpClient, options),
            httpClient);

        await Assert.ThrowsAsync<AgenticSearchUnavailableException>(
            () => agent.AnswerAsync("Find agreements", CancellationToken.None));
    }

    [Fact]
    public async Task FoundryLocalAgentUsesLoadedModelAndStructuredSearchTool()
    {
        string? capturedRequestUri = null;
        string? capturedAuthorizationScheme = null;
        string? capturedRequestBody = null;
        var handler = new StubHttpMessageHandler(async (request, _) =>
        {
            capturedRequestUri = request.RequestUri?.ToString();
            capturedAuthorizationScheme = request.Headers.Authorization?.Scheme;
            capturedRequestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
                {"choices":[{"message":{"role":"assistant","content":"I searched the index and considered the available evidence.\n\nFINAL: No matching documents were found.","tool_calls":[]},"finish_reason":"stop"}]}
                """);
        });
        using var httpClient = new HttpClient(handler);
        await using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=searchdb");
        var options = new SearchOptions
        {
            Embeddings = new EmbeddingOptions
            {
                Provider = "Ollama",
                Endpoint = "http://localhost:11434/v1/",
                ModelName = "bge-m3"
            },
            AgenticSearch = new AgenticSearchOptions
            {
                Provider = "FoundryLocal",
                Enabled = true,
                Endpoint = "http://localhost:5273/v1/",
                ModelName = "qwen3-4b-generic-cpu:3",
                ApiKey = "test-key"
            }
        };
        var agent = new AgenticSearchService(options, new SearchDatabase(dataSource, options),
            new OpenAiCompatibleEmbeddingClient(httpClient, options), httpClient);

        var answer = await agent.AnswerAsync("Find vendor renewal terms", CancellationToken.None);

        Assert.Equal("No matching documents were found.", answer.Answer);
        Assert.Equal("http://localhost:5273/v1/chat/completions", capturedRequestUri);
        Assert.Equal("Bearer", capturedAuthorizationScheme);
        using var requestBody = JsonDocument.Parse(capturedRequestBody!);
        Assert.Equal("qwen3-4b-generic-cpu:3", requestBody.RootElement.GetProperty("model").GetString());
        Assert.Equal("required", requestBody.RootElement.GetProperty("tool_choice").GetString());
        Assert.Equal(512, requestBody.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Contains("/no_think", requestBody.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal("execute_hybrid_search", requestBody.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Contains("Contract", requestBody.RootElement.GetProperty("tools")[0].GetProperty("function")
            .GetProperty("parameters").GetProperty("properties").GetProperty("documentKind").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task SearchFunctionRejectsInvalidDocumentKindWithoutQuerying()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=searchdb");
        using var httpClient = new HttpClient();
        var options = new SearchOptions
        {
            Embeddings = new EmbeddingOptions
            {
                Endpoint = "http://embedding.local/v1/",
                ModelName = "test-embedding",
                ApiKey = "test-key"
            }
        };
        var function = new HybridSearchFunction(
            new SearchDatabase(dataSource, options),
            new OpenAiCompatibleEmbeddingClient(httpClient, options),
            maximumCalls: 2);

        var result = await function.ExecuteHybridSearchAsync(
            "renewal terms", "DROP TABLE search_documents", null, null, null, null, 5, CancellationToken.None);

        Assert.Contains("documentKind is not supported", result);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
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