using Microsoft.Extensions.Options;
using Npgsql;
using SearchService;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Search");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Search must be configured.");
}

builder.Services.AddOptions<SearchOptions>()
    .Bind(builder.Configuration.GetSection("Search"))
    .Validate(options => !string.IsNullOrWhiteSpace(options.TenantId), "Search:TenantId must be configured.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ApiKey), "Search:ApiKey must be supplied through user-secrets or environment variables.")
    .Validate(options => options.EmbeddingDimensions is > 0 and <= 16000, "Search:EmbeddingDimensions must be between 1 and 16000.")
    .Validate(options => options.Embeddings.Provider is "FoundryLocal" or "Ollama" or "MicrosoftFoundry", "Search:Embeddings:Provider must be FoundryLocal, Ollama, or MicrosoftFoundry.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.RabbitMq.UserName) && !string.IsNullOrWhiteSpace(options.RabbitMq.Password), "Search RabbitMQ credentials are required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ArtifactApi.ApiKey), "Search artifact API key must be supplied through user-secrets or environment variables.")
    .Validate(options => options.Embeddings.IsConfigured ||
        (string.IsNullOrWhiteSpace(options.Embeddings.Endpoint) && string.IsNullOrWhiteSpace(options.Embeddings.ModelName)),
        "Configure both Search:Embeddings:Endpoint and ModelName, or leave both empty.")
    .Validate(options => !options.AgenticSearch.Enabled || options.AgenticSearch.IsConfigured,
        "Agentic search is enabled but the chat endpoint, model, API key, or tool-call limit is invalid.")
    .Validate(options => options.AgenticSearch.Provider is "OpenAICompatible" or "FoundryLocal",
        "Search:AgenticSearch:Provider must be OpenAICompatible or FoundryLocal.")
    .Validate(options => !options.AgenticSearch.Enabled || options.Embeddings.IsConfigured,
        "Agentic search requires the embeddings model used to index chunks.")
    .ValidateOnStart();

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<IOptions<SearchOptions>>().Value);
builder.Services.AddSingleton<SearchDatabase>();
builder.Services.AddHttpClient<IExtractionArtifactClient, ExtractionArtifactClient>();
builder.Services.AddHttpClient<ITextEmbeddingClient, OpenAiCompatibleEmbeddingClient>(client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient<AgenticSearchService>(client => client.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddHostedService<SearchIndexConsumer>();
builder.Services.AddHostedService<SearchOutboxPublisher>();

var app = builder.Build();
var settings = app.Services.GetRequiredService<IOptions<SearchOptions>>().Value;
app.Urls.Add(builder.Configuration["Search:ListenUrl"] ?? "http://localhost:5190");
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapPost("/api/search/deterministic", async Task<IResult> (
    DeterministicSearchRequest request,
    HttpRequest httpRequest,
    SearchDatabase database,
    CancellationToken cancellationToken) =>
{
    if (!SearchApiSecurity.IsAuthorized(httpRequest, settings.ApiKey))
    {
        return Results.Unauthorized();
    }

    if (request.Filter is null)
    {
        return Results.BadRequest(new { error = "A search filter object is required." });
    }

    if (request.Filter.DateFrom.HasValue && request.Filter.DateTo.HasValue && request.Filter.DateFrom > request.Filter.DateTo)
    {
        return Results.BadRequest(new { error = "dateFrom must be earlier than or equal to dateTo." });
    }

    return Results.Ok(await database.SearchDeterministicAsync(request.Filter, request.Limit, cancellationToken));
});

app.MapPost("/api/search/hybrid", async Task<IResult> (
    HybridSearchRequest request,
    HttpRequest httpRequest,
    SearchDatabase database,
    ITextEmbeddingClient embeddingClient,
    CancellationToken cancellationToken) =>
{
    if (!SearchApiSecurity.IsAuthorized(httpRequest, settings.ApiKey))
    {
        return Results.Unauthorized();
    }

    if (request.Filter is null || string.IsNullOrWhiteSpace(request.SemanticQuery))
    {
        return Results.BadRequest(new { error = "A semantic query and filter object are required." });
    }

    if (!settings.Embeddings.IsConfigured)
    {
        return Results.Problem("Hybrid search is unavailable until an embedding endpoint and model are configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var vectors = await embeddingClient.EmbedAsync([request.SemanticQuery], cancellationToken);
    return Results.Ok(await database.SearchHybridAsync(request.Filter, request.Limit, vectors[0], cancellationToken));
});

app.MapPost("/api/search/agentic", async Task<IResult> (
    AgenticSearchRequest request,
    HttpRequest httpRequest,
    AgenticSearchService agent,
    CancellationToken cancellationToken) =>
{
    if (!SearchApiSecurity.IsAuthorized(httpRequest, settings.ApiKey))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.Question))
    {
        return Results.BadRequest(new { error = "A natural-language search question is required." });
    }

    if (!settings.AgenticSearch.IsConfigured || !settings.Embeddings.IsConfigured)
    {
        return Results.Problem("Agentic search is unavailable until both chat and embedding models are configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var response = await agent.AnswerAsync(request.Question, cancellationToken);
    return Results.Ok(response);
});

await app.Services.GetRequiredService<SearchDatabase>().InitializeAsync(CancellationToken.None);
await app.RunAsync();

internal static class SearchApiSecurity
{
    public static bool IsAuthorized(HttpRequest request, string configuredApiKey)
    {
        var supplied = request.Headers["X-Search-Api-Key"].ToString();
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(configuredApiKey);
        var suppliedBytes = System.Text.Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length > 0 && expectedBytes.Length == suppliedBytes.Length &&
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
