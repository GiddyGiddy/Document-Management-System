namespace OrchestratorService;

public sealed class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string Exchange { get; set; } = "documents.events";
    public string IngestionQueue { get; set; } = "orchestrator.document-ingested";
    public string DeadLetterExchange { get; set; } = "documents.dead-letter";
    public string DeadLetterQueue { get; set; } = "orchestrator.dead-letter";
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int BatchSize { get; set; } = 20;
    public int PollIntervalSeconds { get; set; } = 2;
    public int LeaseSeconds { get; set; } = 60;
    public int ConfirmTimeoutSeconds { get; set; } = 10;
    public int RetryBaseSeconds { get; set; } = 2;
    public int RetryMaxSeconds { get; set; } = 300;
}