using System.ComponentModel;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace SearchService;

public sealed class AgenticSearchOptions
{
    public string Provider { get; set; } = "OpenAICompatible";
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public int MaximumToolCalls { get; set; } = 2;

    public bool IsConfigured => Enabled &&
        (Provider is "OpenAICompatible" or "FoundryLocal") &&
        Uri.TryCreate(Endpoint, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(ModelName) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        MaximumToolCalls is > 0 and <= 5;
}

public sealed class AgenticSearchService(
    SearchOptions options,
    SearchDatabase database,
    ITextEmbeddingClient embeddingClient,
    HttpClient httpClient)
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private const string Instructions = """
        You answer questions about indexed documents. You have exactly one tool: execute_hybrid_search.
        For questions about documents, call that tool rather than guessing. Convert explicit and relative dates into ISO yyyy-MM-dd filters using the current date supplied by the user message. Keep tenant identity implicit; never request or produce a tenant ID.
        Use the tool's returned chunks and citations as your only factual evidence. Cite each material claim with the returned source URL and page number. If the search returns no relevant chunks, say no matching documents were found.
        Do not reveal internal reasoning or narrate tool calls. Give only a concise final answer, at most two sentences.
        Do not write SQL, call other services, or claim that a search succeeded unless the tool returned results.
        """;

    public async Task<AgenticSearchResponse> AnswerAsync(string question, CancellationToken cancellationToken)
    {
        if (!options.AgenticSearch.IsConfigured)
        {
            throw new AgenticSearchUnavailableException("Agentic search is disabled or its model endpoint/deployment is not configured.");
        }

        if (!options.Embeddings.IsConfigured)
        {
            throw new AgenticSearchUnavailableException("Agentic search requires the same embedding endpoint used for indexed chunks.");
        }

        var modelOptions = options.AgenticSearch;
        if (modelOptions.Provider == "FoundryLocal")
        {
            return await AnswerWithFoundryLocalAsync(question, cancellationToken);
        }

        var client = new OpenAIClient(
            new ApiKeyCredential(modelOptions.ApiKey),
            new OpenAIClientOptions { Endpoint = new Uri(modelOptions.Endpoint, UriKind.Absolute) });
        var chatClient = client.GetChatClient(modelOptions.ModelName);
        var tool = new HybridSearchFunction(database, embeddingClient, modelOptions.MaximumToolCalls);
        var agent = chatClient.AsAIAgent(
            name: "DocumentSearchAgent",
            instructions: Instructions,
            tools: [AIFunctionFactory.Create(tool.ExecuteHybridSearchAsync)]);

        var prompt = $"Current date: {DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd}.\nUser question: {question}";
        var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken);
        return new AgenticSearchResponse(response.Text, tool.Sources);
    }

    private async Task<AgenticSearchResponse> AnswerWithFoundryLocalAsync(string question, CancellationToken cancellationToken)
    {
        var modelOptions = options.AgenticSearch;
        var endpoint = new Uri(new Uri(modelOptions.Endpoint.TrimEnd('/') + "/"), "chat/completions");
        var tool = new HybridSearchFunction(database, embeddingClient, modelOptions.MaximumToolCalls);
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = $"{Instructions}\nFor your final response, output only `FINAL:` followed by the concise user-facing answer. Do not reveal reasoning or narrate tool execution."
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = $"Current date: {DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd}.\nUser question: {question}\n/no_think"
            }
        };
        var tools = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "execute_hybrid_search",
                    ["description"] = "Search indexed documents using a semantic query and optional exact filters. Returns matching chunks and citations.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["semanticQuery"] = new JsonObject { ["type"] = "string", ["description"] = "Concepts or clauses to search for." },
                            ["documentKind"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Optional supported document kind.",
                                ["enum"] = new JsonArray(
                                    JsonValue.Create("Invoice"), JsonValue.Create("Contract"),
                                    JsonValue.Create("IdentityDocument"), JsonValue.Create("FinancialStatement"),
                                    JsonValue.Create("Pdf"), JsonValue.Create("Image"),
                                    JsonValue.Create("Spreadsheet"), JsonValue.Create("Text"), JsonValue.Create("Unknown"))
                            },
                            ["entityName"] = new JsonObject { ["type"] = "string", ["description"] = "Optional exact entity name." },
                            ["dateFrom"] = new JsonObject { ["type"] = "string", ["description"] = "Optional inclusive ISO date." },
                            ["dateTo"] = new JsonObject { ["type"] = "string", ["description"] = "Optional inclusive ISO date." },
                            ["keyword"] = new JsonObject { ["type"] = "string", ["description"] = "Optional keyword or phrase." },
                            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Maximum result count from 1 to 20.", ["default"] = 8 }
                        },
                        ["required"] = new JsonArray(JsonValue.Create("semanticQuery"))
                    }
                }
            }
        };

        var toolCallsUsed = 0;
        while (true)
        {
            var requestBody = new JsonObject
            {
                ["model"] = modelOptions.ModelName,
                ["messages"] = messages.DeepClone(),
                ["tools"] = tools.DeepClone(),
                ["tool_choice"] = toolCallsUsed == 0 ? "required" : toolCallsUsed < modelOptions.MaximumToolCalls ? "auto" : "none",
                ["max_completion_tokens"] = 512,
                ["stream"] = false
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(requestBody))
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", modelOptions.ApiKey);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Foundry Local chat endpoint returned HTTP {(int)response.StatusCode}.");
            }

            using var completion = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!completion.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("message", out var message))
            {
                throw new InvalidDataException("Foundry Local chat response is missing its assistant message.");
            }

            if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() == 0)
            {
                var answer = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                    ? content.GetString() ?? string.Empty
                    : string.Empty;
                return new AgenticSearchResponse(GetUserFacingFoundryAnswer(answer), tool.Sources);
            }

            messages.Add(JsonNode.Parse(message.GetRawText()));
            foreach (var call in calls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                var toolName = function.GetProperty("name").GetString();
                var argumentsJson = function.GetProperty("arguments").GetString() ?? "{}";
                string result;
                if (toolName != "execute_hybrid_search")
                {
                    result = "Search was not run: unsupported tool name.";
                }
                else if (toolCallsUsed >= modelOptions.MaximumToolCalls)
                {
                    result = "Search tool call limit reached. Answer only from the results already returned.";
                }
                else
                {
                    var arguments = JsonSerializer.Deserialize<FoundrySearchArguments>(argumentsJson, WebJsonOptions)
                        ?? throw new InvalidDataException("Foundry Local returned invalid search tool arguments.");
                    result = await tool.ExecuteHybridSearchAsync(
                        arguments.SemanticQuery,
                        arguments.DocumentKind,
                        arguments.EntityName,
                        arguments.DateFrom,
                        arguments.DateTo,
                        arguments.Keyword,
                        arguments.Limit,
                        cancellationToken);
                    toolCallsUsed++;
                }

                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call.GetProperty("id").GetString(),
                    ["content"] = result
                });
            }
        }
    }

    private static string GetUserFacingFoundryAnswer(string response)
    {
        const string finalMarker = "FINAL:";
        var markerIndex = response.LastIndexOf(finalMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            return response[(markerIndex + finalMarker.Length)..].Trim();
        }

        var paragraphs = response.Split(
            ["\r\n\r\n", "\n\n"],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return paragraphs.Length == 0 ? string.Empty : paragraphs[^1];
    }

    private sealed record FoundrySearchArguments(
        string SemanticQuery,
        string? DocumentKind = null,
        string? EntityName = null,
        string? DateFrom = null,
        string? DateTo = null,
        string? Keyword = null,
        int Limit = 8);
}

public sealed class HybridSearchFunction(
    SearchDatabase database,
    ITextEmbeddingClient embeddingClient,
    int maximumCalls)
{
    private int _callCount;
    private readonly ConcurrentQueue<SearchResult> _sources = new();

    public IReadOnlyList<SearchResult> Sources => _sources.ToArray();

    [Description("Search indexed documents using a semantic query and optional exact filters. Returns matching text chunks with PDF URLs, pages, and bounding polygons. Tenant scope is applied by the service and must not be provided by the caller.")]
    public async Task<string> ExecuteHybridSearchAsync(
        [Description("Semantic text to search for, phrased as concepts or clauses rather than a user-facing question.")] string semanticQuery,
        [Description("Optional document kind: Invoice, Contract, IdentityDocument, FinancialStatement, Pdf, Image, Spreadsheet, Text, or Unknown.")] string? documentKind = null,
        [Description("Optional exact entity/customer/vendor name filter.")] string? entityName = null,
        [Description("Optional inclusive lower document date in ISO yyyy-MM-dd format.")] string? dateFrom = null,
        [Description("Optional inclusive upper document date in ISO yyyy-MM-dd format.")] string? dateTo = null,
        [Description("Optional keyword or phrase filter applied to indexed text.")] string? keyword = null,
        [Description("Maximum number of matching chunks to return, from 1 to 20.")] int limit = 8,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _callCount) > maximumCalls)
        {
            return "Search tool call limit reached. Answer only from the results already returned.";
        }

        if (string.IsNullOrWhiteSpace(semanticQuery) || semanticQuery.Length > 1000)
        {
            return "Search was not run: semanticQuery must contain between 1 and 1000 characters.";
        }

        if (documentKind is not null && !SearchDocumentKinds.Supported.Contains(documentKind))
        {
            return "Search was not run: documentKind is not supported.";
        }

        if (!TryParseDate(dateFrom, out var parsedFrom) || !TryParseDate(dateTo, out var parsedTo) ||
            (parsedFrom.HasValue && parsedTo.HasValue && parsedFrom > parsedTo))
        {
            return "Search was not run: dates must be ISO yyyy-MM-dd and dateFrom must not be after dateTo.";
        }

        var vectors = await embeddingClient.EmbedAsync([semanticQuery], cancellationToken);
        var filter = new SearchFilter(documentKind, entityName, parsedFrom, parsedTo, keyword);
        var results = await database.SearchHybridAsync(filter, Math.Clamp(limit, 1, 20), vectors[0], cancellationToken);
        foreach (var result in results)
        {
            _sources.Enqueue(result);
        }

        return JsonSerializer.Serialize(results);
    }

    private static bool TryParseDate(string? value, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        date = parsed;
        return true;
    }
}

public sealed class AgenticSearchUnavailableException(string message) : Exception(message);