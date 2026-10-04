# Extraction Service

This .NET Worker Service consumes the orchestrator's current extraction command from the durable RabbitMQ topic exchange:

- Exchange: `documents.events`
- Routing key: `document.task.extraction.requested`
- Queue: `extraction.agent-extract-requests`
- Command envelope: `document.task.requested`, schema version 1, `taskType=extraction`

It persists the raw command in `extraction_inbox_events` and a deduplicated job in `extraction_jobs` before acknowledging RabbitMQ. The job processor downloads the document through the API's `/api/fileupload/download/{documentId}` endpoint, invokes the Document Intelligence Layout API, and saves both Markdown and raw Layout JSON. Completed Layout jobs are marked `ReadyForExtraction` for the next agent stage. Layout failures retry up to `ExtractionProcessing:MaxLayoutAttempts` and then remain visible as `LayoutFailed`.

## Local Configuration

Create the `extractiondb` PostgreSQL database, or point the worker at an existing development database, then configure credentials outside source control:

```powershell
$env:ConnectionStrings__Extraction = 'Host=localhost;Port=5432;Database=extractiondb;Username=postgres;Password=<local-secret>'
$env:RabbitMq__HostName = 'localhost'
$env:RabbitMq__Port = '5673'
$env:RabbitMq__UserName = '<local-rabbitmq-user>'
$env:RabbitMq__Password = '<local-rabbitmq-password>'
$env:DocumentContentApi__BaseUrl = 'http://localhost:5176/'
$env:DocumentIntelligence__Endpoint = 'http://localhost:5000'
$env:DocumentIntelligence__ApiKey = '<document-intelligence-container-key>'
$env:ExtractionArtifactApi__ApiKey = '<internal-artifact-api-key>'
dotnet run --project .\ExtractionService\ExtractionService.csproj
```

The Document Intelligence v4 Layout container requires an Azure Document Intelligence resource endpoint and key for billing, plus the accepted EULA. Keep those values in user-secrets or a secret manager; do not commit them. Container limits are substantial (Microsoft documents 8 CPU cores and 16 GB RAM minimum for Layout, with 24 GB recommended).

## Model Provider

Both model routes use the OpenAI-compatible chat-completions API while sharing the same kind-aware extraction and correction prompts and a maximum of three total attempts. Model processing is opt-in; while disabled, jobs stop at `ReadyForExtraction` and do not publish a completion event.

The worker supports every current orchestrator `DocumentKind`: `Invoice`, `Contract`, `IdentityDocument`, `FinancialStatement`, `Pdf`, `Image`, `Spreadsheet`, `Text`, and `Unknown`. Invoice, contract, identity, and financial-statement kinds use dedicated strict JSON shapes and validators. File-format-only kinds use a general-document shape with a summary, key facts, and tables; table rows are checked against their column counts.

For **Foundry Local**, set the local server endpoint and the model ID reported by the Foundry Local runtime:

```powershell
$env:Foundry__Provider = 'FoundryLocal'
$env:Foundry__FoundryLocal__Endpoint = 'http://localhost:5273/v1/'
$env:Foundry__FoundryLocal__ModelName = '<loaded-model-id>'
$env:ExtractionProcessing__EnableModelExtraction = 'true'
```

For Qwen3-4B, use the loaded model ID (for example, `qwen3-4b-generic-cpu:3`). The agent appends Qwen's `/no_think` cue to the user prompt so reasoning text does not precede the strict JSON extraction result.

Foundry Local API key is optional. If supplied, it is sent as a Bearer token.

For **Microsoft Foundry**, use the resource's OpenAI v1 endpoint, the exact deployed model name, and a key stored in user-secrets or a secret manager:

```powershell
$env:Foundry__Provider = 'MicrosoftFoundry'
$env:Foundry__MicrosoftFoundry__Endpoint = 'https://<resource>.services.ai.azure.com/openai/v1/'
$env:Foundry__MicrosoftFoundry__DeploymentName = '<deployment-name>'
$env:ExtractionProcessing__EnableModelExtraction = 'true'
dotnet user-secrets set 'Foundry:MicrosoftFoundry:ApiKey' '<key>' --project .\ExtractionService\ExtractionService.csproj
```

For Microsoft Foundry, the adapter sends the configured key in the `api-key` header. A project endpoint alone is not enough; deploy a chat-capable model and use its deployment name. The adapter asks for JSON-object output, then deserializes against the strict C# schema selected by `documentKind` and applies deterministic field/date/arithmetic checks before accepting it.

On success the service saves the kind-specific JSON and publishes `document.task.completed`; `outputReference` is an absolute URL built from `ExtractionArtifactApi:PublicBaseUrl`. The artifact endpoint serves the stored layout and extraction JSON at `/api/internal/extraction-artifacts/{taskId}` and requires the `X-Extraction-Artifact-Key` header. Configure the same random internal key in Search Service secrets and keep the endpoint on an internal network. After three invalid candidates the extractor saves the issues and publishes `document.task.failed`. Both outcomes use a transactional outbox, stable event IDs, publisher confirms, and retry backoff. These event types and fields match the orchestrator's current consumer contract. Identity-document and financial data can contain sensitive information, so restrict database/broker access and apply suitable retention policies to stored payloads and extraction results.