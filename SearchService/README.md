# Search Service

Search Service consumes the Orchestrator's `document.task.search-index.requested` command from the durable `documents.events` topic exchange using its own `search.document-index-requests` queue. It fetches the protected extraction artifact at `inputReference`, validates the artifact IDs against the task, chunks Layout paragraphs while retaining page/polygon citations, and upserts document metadata and chunks. Search success is written to an outbox and returned as `document.task.completed` for the Orchestrator's existing lifecycle consumer.

## Storage

The service uses PostgreSQL with the `vector` extension (pgvector). Create the `searchdb` database and install the pgvector server extension before starting the service; its startup schema initialization creates the extension, metadata/chunk tables, GIN full-text index, B-tree filter indexes, and HNSW cosine index. Startup fails visibly if the server does not provide pgvector.

Configure local credentials outside source control:

```powershell
$env:ConnectionStrings__Search = 'Host=localhost;Port=5432;Database=searchdb;Username=postgres;Password=<local-secret>'
$env:Search__TenantId = 'local-development'
$env:Search__RabbitMq__HostName = 'localhost'
$env:Search__RabbitMq__Port = '5673'
$env:Search__RabbitMq__UserName = '<local-rabbitmq-user>'
$env:Search__RabbitMq__Password = '<local-rabbitmq-password>'
$env:Search__ArtifactApi__BaseUrl = 'http://localhost:5085'
$env:Search__ArtifactApi__ApiKey = '<same-internal-key-as-extraction-service>'
$env:Search__ApiKey = '<search-api-key-for-a-trusted-bff>'
dotnet run --project .\SearchService\SearchService.csproj
```

Embeddings are separately configured so the service does not assume the chat model supports embeddings. With the endpoint/model left empty, indexing and deterministic search still work, but chunks have no vectors and hybrid search returns `503`. The adapter supports both providers:

```powershell
# Foundry Local OpenAI-compatible endpoint; optional API key is sent as Bearer.
$env:Search__Embeddings__Provider = 'FoundryLocal'
$env:Search__Embeddings__Endpoint = 'http://localhost:5273/v1/'
$env:Search__Embeddings__ModelName = '<loaded-embedding-model-id>'

# Or Microsoft Foundry v1; API key is sent in the api-key header.
$env:Search__Embeddings__Provider = 'MicrosoftFoundry'
$env:Search__Embeddings__Endpoint = 'https://<resource>.services.ai.azure.com/openai/v1/'
$env:Search__Embeddings__ModelName = '<embedding-deployment-name>'
dotnet user-secrets set 'Search:Embeddings:ApiKey' '<key>' --project .\SearchService\SearchService.csproj
```

Configure `Search__EmbeddingDimensions` to match that embedding model. The same provider/model/dimensions must be used for indexing and query-time embeddings. A custom `Search:Embeddings:ApiKeyHeaderName` overrides the provider default for compatible gateways.

For local multilingual embeddings, BGE-M3 is supported through Ollama's OpenAI-compatible endpoint. It returns 1024-dimensional vectors:

```powershell
ollama pull bge-m3
dotnet user-secrets set 'Search:Embeddings:Provider' 'Ollama' --project .\SearchService\SearchService.csproj
dotnet user-secrets set 'Search:Embeddings:Endpoint' 'http://localhost:11434/v1/' --project .\SearchService\SearchService.csproj
dotnet user-secrets set 'Search:Embeddings:ModelName' 'bge-m3' --project .\SearchService\SearchService.csproj
dotnet user-secrets set 'Search:EmbeddingDimensions' '1024' --project .\SearchService\SearchService.csproj
```

At startup, Search safely changes the `search_chunks.embedding` column width only when existing vectors are all null. If non-null vectors exist, startup fails with a re-embedding requirement instead of discarding or truncating them.

## Query API

Both routes require `X-Search-Api-Key`. Keep this key in a trusted backend-for-frontend or API gateway; do not embed it in Angular code.

`POST /api/search/deterministic` accepts `filter.documentKind`, `filter.entityName`, `filter.dateFrom`, `filter.dateTo`, `filter.keyword`, and `limit`. It performs only structural and PostgreSQL full-text filtering; it makes no model calls.

`POST /api/search/hybrid` accepts the same filter plus `semanticQuery` and `limit`. It embeds the query and orders matching chunks by pgvector cosine distance while applying the structural filters in SQL.

Results include document metadata and a citation with document ID, PDF URL/page fragment, page number, and polygon from Document Intelligence Layout where available. A UI viewer can use the polygon to highlight the cited region.

`POST /api/search/agentic` is an optional Microsoft Agent Framework Chat Completions agent. Its only function tool is the typed `execute_hybrid_search`; it cannot issue SQL or choose a tenant. Enable it only after configuring a chat-capable deployment and an embedding model:

```powershell
$env:Search__AgenticSearch__Provider = 'FoundryLocal'
$env:Search__AgenticSearch__Enabled = 'true'
$env:Search__AgenticSearch__Endpoint = 'http://localhost:5273/v1/'
$env:Search__AgenticSearch__ModelName = 'qwen3-4b-generic-cpu:3'
$env:Search__AgenticSearch__ApiKey = 'foundry-local'
$env:Search__AgenticSearch__MaximumToolCalls = '2'
```

For Microsoft Foundry, use the default `OpenAICompatible` provider, its OpenAI-compatible `/openai/v1/` endpoint, and the deployed model name. Foundry Local uses its `/v1/` endpoint and the model ID returned by `/v1/models`; its local API key value is required by configuration but is not a cloud secret. Keep other credentials in user-secrets rather than source control. The Foundry Local provider uses a structured chat-completions tool-call loop. The route accepts `{ "question": "..." }` and returns a summarized answer with citations from returned source URLs/pages. It remains unavailable (`503`) unless agent and embedding configurations are both valid.

## Tenant Boundary

The current upload API has no authentication or tenant claims. This first Search implementation therefore scopes all indexed rows to the configured `Search:TenantId` and does not accept a tenant ID in request bodies. It is single-tenant development scaffolding, not production multi-tenancy. Before exposing it to multiple customers, derive tenant ID from a verified identity claim at the upload and query boundaries, propagate it through the extraction artifact and task event, and replace the single configured scope with that trusted tenant context.