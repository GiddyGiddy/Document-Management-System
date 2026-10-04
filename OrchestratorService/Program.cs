using Npgsql;
using OrchestratorService;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection("RabbitMq"))
    .Validate(options => !string.IsNullOrWhiteSpace(options.UserName), "RabbitMq:UserName must be supplied through user secrets or environment variables.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Password), "RabbitMq:Password must be supplied through user secrets or environment variables.")
    .Validate(options => options.Port is > 0 and <= 65535, "RabbitMq:Port must be a valid TCP port.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Exchange) && !string.IsNullOrWhiteSpace(options.IngestionQueue), "RabbitMq:Exchange and RabbitMq:IngestionQueue must be supplied.")
    .Validate(options => options.BatchSize > 0 && options.PollIntervalSeconds > 0 && options.LeaseSeconds > 0 && options.ConfirmTimeoutSeconds > 0, "RabbitMQ batch, poll, lease, and confirm settings must be positive.")
    .Validate(options => options.RetryBaseSeconds > 0 && options.RetryMaxSeconds >= options.RetryBaseSeconds, "RabbitMQ retry settings must be positive and RetryMaxSeconds must be at least RetryBaseSeconds.")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("Orchestrator");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Orchestrator must be configured.");
}

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<IDocumentClassifier, DocumentClassifier>();
builder.Services.AddSingleton<OrchestratorDatabase>();
builder.Services.AddHostedService<RabbitMqConsumerService>();
builder.Services.AddHostedService<OutboxPublisherService>();

var host = builder.Build();
await host.Services.GetRequiredService<OrchestratorDatabase>().InitializeAsync(CancellationToken.None);
await host.RunAsync();