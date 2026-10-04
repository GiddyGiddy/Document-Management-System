# Orchestrator Service

The .NET Worker Service consumes `document.ingested` from the durable `documents.events` topic exchange. It uses a dedicated `orchestrator.document-ingested` queue, so it does not compete with other event consumers. The consumer also listens for `document.task.completed` and `document.task.failed` to advance the document state machine.

The worker uses PostgreSQL database `orchestratordb` by default. Create the database before starting the worker; the worker creates its inbox, workflow, task, and outbox tables idempotently at startup. Configure connection details and RabbitMQ credentials through environment variables or a secret manager, not in this file:

```powershell
$env:ConnectionStrings__Orchestrator = 'Host=localhost;Port=5432;Database=orchestratordb;Username=postgres;Password=<local-secret>'
$env:RabbitMq__UserName = $env:RABBITMQ_DEFAULT_USER
$env:RabbitMq__Password = $env:RABBITMQ_DEFAULT_PASS
dotnet run --project .\OrchestratorService\OrchestratorService.csproj
```

The classifier is deliberately local and metadata-only: it uses the original filename and content type to identify invoices, contracts, identity documents, financial statements, and common file types. Replace `IDocumentClassifier` with a model-backed implementation when a model provider and credentials are selected.

## Task Events

On ingestion, the service persists workflow state and an extraction task in one PostgreSQL transaction. Its outbox publishes `document.task.requested` to routes such as `document.task.extraction.requested`. A task request includes `eventId`, `correlationId`, `documentId`, `taskId`, `taskType`, `documentKind`, and optional `inputReference`. The command is retried with capped exponential backoff if RabbitMQ has no queue bound for its route or publisher confirmation fails.

The extraction service should publish its result to `document.task.completed` using the same exchange. Example result:

```json
{
  "eventId": "stable-result-event-id",
  "eventType": "document.task.completed",
  "schemaVersion": 1,
  "correlationId": "correlation-id-from-request",
  "documentId": "document-id-from-request",
  "taskId": "task-id-from-request",
  "taskType": "extraction",
  "outputReference": "opaque-reference-to-extracted-content"
}
```

After extraction, the orchestrator requests search indexing and requests semantic review for invoices, contracts, and financial statements. When all requested tasks complete, the workflow becomes `Completed`; a failed task moves it to `Failed`. Result producers must use stable event IDs so redelivery can be safely deduplicated.

The current ingestion event carries metadata and document IDs, not file bytes or a content URI. Before implementing extraction, provide a secured document-read API keyed by `documentId` or move document content to shared object storage and include an opaque storage reference in the task contract. Semantic comparison also needs an explicit relationship/target-document contract; it is not scheduled by this first implementation.