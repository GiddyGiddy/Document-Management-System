using ExtractionService;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection("RabbitMq"))
    .Validate(options => !string.IsNullOrWhiteSpace(options.UserName), "RabbitMq:UserName must be supplied through user secrets or environment variables.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Password), "RabbitMq:Password must be supplied through user secrets or environment variables.")
    .Validate(options => options.Port is > 0 and <= 65535, "RabbitMq:Port must be a valid TCP port.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Exchange) && !string.IsNullOrWhiteSpace(options.RequestQueue), "RabbitMq:Exchange and RabbitMq:RequestQueue must be supplied.")
    .Validate(options => options.RetryDelaySeconds > 0, "RabbitMq:RetryDelaySeconds must be positive.")
    .Validate(options => options.PollIntervalSeconds > 0 && options.LeaseSeconds > 0 && options.ConfirmTimeoutSeconds > 0, "RabbitMQ polling, lease, and confirm settings must be positive.")
    .Validate(options => options.RetryBaseSeconds > 0 && options.RetryMaxSeconds >= options.RetryBaseSeconds, "RabbitMQ retry settings must be positive and RetryMaxSeconds must be at least RetryBaseSeconds.")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("Extraction");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Extraction must be configured.");
}

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<ExtractionDatabase>();
builder.Services.AddOptions<FoundryModelOptions>()
    .Bind(builder.Configuration.GetSection("Foundry"))
    .Validate(options => options.Provider is "FoundryLocal" or "MicrosoftFoundry", "Foundry:Provider must be FoundryLocal or MicrosoftFoundry.")
    .ValidateOnStart();
builder.Services.AddHttpClient<FoundryDocumentAgent>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
});
builder.Services.AddSingleton<IDocumentExtractionAgent>(serviceProvider => serviceProvider.GetRequiredService<FoundryDocumentAgent>());
builder.Services.AddSingleton<IDocumentCorrectionAgent>(serviceProvider => serviceProvider.GetRequiredService<FoundryDocumentAgent>());
builder.Services.AddSingleton<DocumentExtractionValidator>();
builder.Services.AddSingleton<DocumentCriticLoop>();
builder.Services.AddOptions<DocumentContentApiOptions>()
    .Bind(builder.Configuration.GetSection("DocumentContentApi"))
    .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _), "DocumentContentApi:BaseUrl must be an absolute URL.")
    .ValidateOnStart();
builder.Services.AddOptions<DocumentIntelligenceOptions>()
    .Bind(builder.Configuration.GetSection("DocumentIntelligence"))
    .Validate(options => Uri.TryCreate(options.Endpoint, UriKind.Absolute, out _), "DocumentIntelligence:Endpoint must be an absolute URL.")
    .Validate(options => options.ApiVersion == "2024-11-30", "DocumentIntelligence:ApiVersion must match the supported v4 Layout container API.")
    .Validate(options => options.PollIntervalSeconds > 0 && options.TimeoutSeconds > 0, "Document Intelligence poll interval and timeout must be positive.")
    .ValidateOnStart();
builder.Services.AddHttpClient<IDocumentContentClient, DocumentContentApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DocumentContentApiOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/");
});
builder.Services.AddHttpClient<IDocumentLayoutClient, DocumentIntelligenceLayoutClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DocumentIntelligenceOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds + 30);
});
builder.Services.AddOptions<ExtractionProcessingOptions>()
    .Bind(builder.Configuration.GetSection("ExtractionProcessing"))
    .Validate(options => options.PollIntervalSeconds > 0 && options.LeaseSeconds > 0 && options.MaxLayoutAttempts > 0, "Extraction poll interval, lease, and max attempts must be positive.")
    .Validate(options => options.RetryBaseSeconds > 0 && options.RetryMaxSeconds >= options.RetryBaseSeconds, "Extraction retry settings must be positive and RetryMaxSeconds must be at least RetryBaseSeconds.")
    .Validate(options => !options.EnableModelExtraction || FoundryDocumentAgent.IsConfigured(builder.Configuration.GetSection("Foundry").Get<FoundryModelOptions>() ?? new FoundryModelOptions()), "Model extraction is enabled but the selected Foundry endpoint/model is not configured.")
    .ValidateOnStart();
builder.Services.AddSingleton<IExtractionJobStore>(serviceProvider => serviceProvider.GetRequiredService<ExtractionDatabase>());
builder.Services.AddSingleton<ExtractionLayoutPipeline>();
builder.Services.AddHostedService<ExtractionRequestConsumer>();
builder.Services.AddHostedService<ExtractionJobProcessor>();
builder.Services.AddHostedService<ExtractionOutboxPublisherService>();

var host = builder.Build();
await host.Services.GetRequiredService<ExtractionDatabase>().InitializeAsync(CancellationToken.None);
await host.RunAsync();