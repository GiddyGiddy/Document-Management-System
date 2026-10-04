namespace SearchService;

public sealed class SearchOptions
{
    public string TenantId { get; set; } = "local-development";
    public string ApiKey { get; set; } = string.Empty;
    public string SourceDocumentBaseUrl { get; set; } = "http://localhost:5176/";
    public int EmbeddingDimensions { get; set; } = 1536;
    public RabbitMqOptions RabbitMq { get; set; } = new();
    public ArtifactApiOptions ArtifactApi { get; set; } = new();
    public EmbeddingOptions Embeddings { get; set; } = new();
    public AgenticSearchOptions AgenticSearch { get; set; } = new();
}

public sealed class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string Exchange { get; set; } = "documents.events";
    public string IndexQueue { get; set; } = "search.document-index-requests";
    public string DeadLetterExchange { get; set; } = "documents.dead-letter";
    public string DeadLetterQueue { get; set; } = "search.dead-letter";
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 2;
    public int LeaseSeconds { get; set; } = 60;
    public int ConfirmTimeoutSeconds { get; set; } = 10;
    public int RetryBaseSeconds { get; set; } = 2;
    public int RetryMaxSeconds { get; set; } = 300;
}

public sealed class ArtifactApiOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5085";
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class EmbeddingOptions
{
    public string Provider { get; set; } = "FoundryLocal";
    public string Endpoint { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string? ApiKeyHeaderName { get; set; }
    public bool IsConfigured =>
        Provider is "FoundryLocal" or "Ollama" or "MicrosoftFoundry" &&
        Uri.TryCreate(Endpoint, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(ModelName) &&
        (Provider != "MicrosoftFoundry" || !string.IsNullOrWhiteSpace(ApiKey));

    public string ResolvedApiKeyHeaderName => !string.IsNullOrWhiteSpace(ApiKeyHeaderName)
        ? ApiKeyHeaderName
        : Provider == "MicrosoftFoundry" ? "api-key" : "Authorization";
}